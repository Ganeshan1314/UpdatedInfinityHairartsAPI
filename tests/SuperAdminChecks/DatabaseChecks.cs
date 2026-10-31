using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Services;
using Microsoft.Data.SqlClient;

internal static class DatabaseChecks
{
    public static async Task Run()
    {
        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Run from the API repository.");
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")));
        var connectionString = Environment.GetEnvironmentVariable("SUPERADMIN_TEST_CONNECTION")
            ?? settings.RootElement.GetProperty("ConnectionStrings").GetProperty("dbconnection").GetString();
        var connectionSettings = new SqlConnectionStringBuilder(connectionString);
        var host = connectionSettings.DataSource.Split('\\', ',')[0];
        if (host != "." && host != "(localdb)" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && host != "127.0.0.1" && !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Database checks only run against a local SQL Server.");
        // A disposable database keeps even temporary schema locks away from application data.
        connectionSettings.Encrypt = SqlConnectionEncryptOption.Optional;
        connectionSettings.InitialCatalog = "master";
        await using var master = new SqlConnection(connectionSettings.ConnectionString);
        await master.OpenAsync();
        var databaseName = "SalonexaSuperAdminChecks_" + Guid.NewGuid().ToString("N");
        await master.ExecuteAsync($"CREATE DATABASE [{databaseName}]");
        connectionSettings.InitialCatalog = databaseName;
        await using var connection = new SqlConnection(connectionSettings.ConnectionString);
        try {
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            CREATE TABLE dbo.LocationMaster(LocationMasterID uniqueidentifier PRIMARY KEY, LocationName nvarchar(150));
            INSERT dbo.LocationMaster VALUES(NEWID(), N'Fixture city');
            CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, SalonName nvarchar(200),
                LocationMasterID uniqueidentifier, SalonAddress nvarchar(500), ContactNumber nvarchar(20),
                IsActive bit NOT NULL DEFAULT 1, DisplayOrder int NOT NULL DEFAULT 0, UpdatedUtc datetime2 NULL);
            CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier,
                OwnerName nvarchar(150), EmailAddress nvarchar(254), IsActive bit, CreatedUtc datetime2 DEFAULT SYSUTCDATETIME());
            CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier,
                SalonMasterID uniqueidentifier, BookingStatusMasterID int, BookingDate date, CompletionStatus nvarchar(20));
            CREATE TABLE dbo.HairCut_Item_Bill_Master(UniqueBillID uniqueidentifier PRIMARY KEY, CreatedDate datetime,
                BillIDTotalAmount decimal(18,2), IsDeleted bit);
            """);
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS SQL: " + label); }

        // Each check rolls back both migration DDL and fixtures. Rejected procedures can roll back the
        // entire transaction, so independent failure checks intentionally use separate transactions.
        async Task InTransaction(Func<SqlTransaction, Task> check)
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            try {
                var script = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "SuperAdminDashboard.sql"));
                foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    if (!string.IsNullOrWhiteSpace(batch)) await connection.ExecuteAsync(batch, transaction: transaction);
                await check(transaction);
            }
            finally { if (transaction.Connection is not null) await transaction.RollbackAsync(); }
        }
        async Task<Guid> CreateSalon(SqlTransaction transaction, bool active = true)
        {
            var locationId = await connection.QueryFirstAsync<Guid>("SELECT TOP(1) LocationMasterID FROM dbo.LocationMaster", transaction: transaction);
            var id = Guid.NewGuid();
            await connection.ExecuteAsync("""
                INSERT dbo.SalonMaster(SalonMasterID, LocationMasterID, SalonName, SalonAddress, ContactNumber, IsActive, IsVisibleToCustomers)
                VALUES(@id, @locationId, N'Super admin rollback fixture', N'Test address', N'9999999999', @active, 0)
                """, new { id, locationId, active }, transaction);
            return id;
        }
        await InTransaction(async transaction => {
            var id = await CreateSalon(transaction);
            var customer = Guid.NewGuid();
            await connection.ExecuteAsync("""
                INSERT dbo.SeatBookingDetails VALUES
                    (NEWID(), @customer, @id, 2, '20261001', N'Completed'),
                    (NEWID(), @customer, @id, 2, '20261027', N'Pending'),
                    (NEWID(), NEWID(), @id, 2, '20261028', N'Pending'),
                    (NEWID(), NEWID(), @id, 1, '20261015', N'Pending');
                INSERT dbo.HairCut_Item_Bill_Master VALUES
                    (NEWID(), '20261001', 100, 0), (NEWID(), '20261027 23:59:59', 200, 0),
                    (NEWID(), '20261027', 999, 1), (NEWID(), '20261028', 500, 0);
                """, new { id, customer }, transaction);
            using (var data = await connection.QueryMultipleAsync("dbo.GetSuperAdminOverview", new { FromDate = new DateTime(2026, 10, 1), ToDate = new DateTime(2026, 10, 27) }, transaction, commandType: CommandType.StoredProcedure)) {
                var summary = await data.ReadSingleAsync<SuperAdminSummary>();
                var days = (await data.ReadAsync<SuperAdminDay>()).ToList();
                _ = (await data.ReadAsync<SuperAdminAudit>()).ToList();
                Check(summary.TotalSalons >= 1 && summary.AwaitingApproval >= 1, "Overview totals map to API records");
                Check(summary.Bookings == 2 && summary.CompletedBookings == 1 && summary.Customers == 1,
                    "Range boundaries, confirmed status and distinct customer counts are correct");
                Check(summary.BilledAmount == 300, "Billing includes both dates and excludes deleted and out-of-range bills");
                Check(days.Count == 7 && days[^1].BookingDate.Date == new DateTime(2026, 10, 27), "The weekly series includes seven days ending at To date");
            }
            using (var data = await connection.QueryMultipleAsync("dbo.GetSuperAdminSalons", new { Search = "Super admin rollback fixture", Status = "pending", Page = 1, PageSize = 20 }, transaction, commandType: CommandType.StoredProcedure)) {
                Check(await data.ReadSingleAsync<int>() == 1, "The pending directory counts the fixture once");
                var rows = (await data.ReadAsync<SuperAdminSalon>()).ToList();
                Check(rows.Count == 1 && rows[0].SalonMasterID == id && !rows[0].IsVisibleToCustomers, "Salon rows map to API records with persisted visibility");
            }
            async Task Set(bool visible, bool expected) => await connection.ExecuteAsync("dbo.SetSuperAdminSalonVisibility",
                new { SalonID = id, Visible = visible, ExpectedVisible = expected, Actor = "fixture-application-owner" }, transaction, commandType: CommandType.StoredProcedure);
            await Set(true, false);
            Check(await connection.QuerySingleAsync<bool>("SELECT IsVisibleToCustomers FROM dbo.SalonMaster WHERE SalonMasterID = @id", new { id }, transaction), "Approval persists to SalonMaster");
            var audit = await connection.QuerySingleAsync<SuperAdminAudit>("SELECT * FROM dbo.SuperAdminSalonAudit WHERE SalonMasterID = @id", new { id }, transaction);
            Check(audit.ActorUserName == "fixture-application-owner" && audit.IsVisibleToCustomers, "Approval records the actor and visibility atomically");
            await Set(true, true);
            Check(await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.SuperAdminSalonAudit WHERE SalonMasterID = @id", new { id }, transaction) == 1, "Unchanged visibility does not duplicate audit entries");
            var location = await connection.QuerySingleAsync<Guid>("SELECT LocationMasterID FROM dbo.SalonMaster WHERE SalonMasterID = @id", new { id }, transaction);
            var visible = await connection.QueryAsync<dynamic>("dbo.getActiveSalonsByLocation", new { LocationMasterID = location }, transaction, commandType: CommandType.StoredProcedure);
            Check(visible.Any(row => (Guid)row.SalonMasterID == id), "Approved salons appear in the customer picker");
            await Set(false, true);
            var hidden = await connection.QueryAsync<dynamic>("dbo.getActiveSalonsByLocation", new { LocationMasterID = location }, transaction, commandType: CommandType.StoredProcedure);
            Check(!hidden.Any(row => (Guid)row.SalonMasterID == id), "Hidden salons disappear from the customer picker");
        });
        foreach (var scenario in new[] { "inactive", "stale", "missing" }) await InTransaction(async transaction => {
            var id = scenario == "missing" ? Guid.NewGuid() : await CreateSalon(transaction, scenario != "inactive");
            try {
                await connection.ExecuteAsync("dbo.SetSuperAdminSalonVisibility", new { SalonID = id, Visible = true, ExpectedVisible = scenario == "stale", Actor = "fixture-owner" }, transaction, commandType: CommandType.StoredProcedure);
                throw new Exception("Expected a rejected visibility update: " + scenario);
            }
            catch (SqlException exception) when (exception.Number == (scenario == "inactive" ? 50045 : scenario == "stale" ? 50046 : 50044)) {
                Check(true, "The database rejects " + scenario + " salon approval");
            }
        });
        Console.WriteLine($"Passed {checks} SQL checks. All fixture transactions rolled back.");
        }
        finally {
            await connection.CloseAsync();
            SqlConnection.ClearPool(connection);
            await master.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
        }
    }
}
