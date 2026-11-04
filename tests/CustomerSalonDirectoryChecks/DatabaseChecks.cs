using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.Data.SqlClient;

internal static class DatabaseChecks
{
    public static async Task Run()
    {
        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Run from the API repository.");
        var configured = Environment.GetEnvironmentVariable("SALON_DIRECTORY_TEST_CONNECTION");
        if (string.IsNullOrEmpty(configured)) {
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")));
            configured = settings.RootElement.GetProperty("ConnectionStrings").GetProperty("dbconnection").GetString();
        }
        var options = new SqlConnectionStringBuilder(configured);
        var host = options.DataSource.Split('\\', ',')[0];
        if (host != "." && host != "(localdb)" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && host != "127.0.0.1" && !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Database checks only run against a local SQL Server.");
        options.Encrypt = SqlConnectionEncryptOption.Optional;
        options.InitialCatalog = "master";
        await using var master = new SqlConnection(options.ConnectionString);
        await master.OpenAsync();
        var database = "SalonexaDirectoryChecks_" + Guid.NewGuid().ToString("N");
        await master.ExecuteAsync($"CREATE DATABASE [{database}]");
        options.InitialCatalog = database;
        await using var connection = new SqlConnection(options.ConnectionString);
        var checks = 0;
        void Check(bool condition, string label) {
            if (!condition) throw new Exception(label);
            checks++;
            Console.WriteLine("PASS SQL: " + label);
        }
        try {
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                CREATE TABLE dbo.LocationMaster(LocationMasterID uniqueidentifier PRIMARY KEY, LocationName nvarchar(150), IsActive bit);
                CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, LocationMasterID uniqueidentifier,
                    SalonName nvarchar(200), SalonAddress nvarchar(500), ContactNumber nvarchar(20),
                    IsActive bit, IsVisibleToCustomers bit, DisplayOrder int);
                """);
            var script = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "CustomerSalonDirectory.sql"));
            for (var run = 0; run < 2; run++)
                foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    if (!string.IsNullOrWhiteSpace(batch)) await connection.ExecuteAsync(batch);
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE name='IX_SalonMaster_CustomerDirectory'") == 1,
                "Migration is rerunnable and creates the filtered directory index once");
            var firstLocation = Guid.NewGuid();
            var secondLocation = Guid.NewGuid();
            var inactiveLocation = Guid.NewGuid();
            await connection.ExecuteAsync("INSERT dbo.LocationMaster VALUES(@firstLocation,N'First city',1),(@secondLocation,N'Second city',1),(@inactiveLocation,N'Closed city',0)",
                new { firstLocation, secondLocation, inactiveLocation });
            for (var index = 0; index < 65; index++)
                await connection.ExecuteAsync("INSERT dbo.SalonMaster VALUES(@id,@location,@name,NULL,NULL,1,1,0)",
                    new { id = Guid.NewGuid(), location = index % 2 == 0 ? firstLocation : secondLocation, name = index < 2 ? "Duplicate name" : $"Salon {index:000}" });
            foreach (var item in new[] {
                new { name = "Hidden salon", location = firstLocation, active = true, visible = false },
                new { name = "Inactive salon", location = firstLocation, active = false, visible = true },
                new { name = "Closed location salon", location = inactiveLocation, active = true, visible = true },
                new { name = "Literal %_[] salon", location = firstLocation, active = true, visible = true },
                new { name = "தமிழ் சலூன்", location = firstLocation, active = true, visible = true }
            }) await connection.ExecuteAsync("INSERT dbo.SalonMaster VALUES(NEWID(),@location,@name,NULL,NULL,@active,@visible,0)", item);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:dbconnection"] = options.ConnectionString
            }).Build();
            var service = new CustomerSalonDirectoryService(config);
            async Task<CustomerSalonPage> Page(string search = "", string? cursor = null, int size = 20) {
                var query = new CustomerSalonQuery { Search = search, Cursor = cursor, PageSize = size };
                if (!query.TryReadCursor(out var after)) throw new Exception("Invalid test cursor");
                return await service.GetAsync(query, after, CancellationToken.None);
            }
            var first = await Page();
            Check(first.Items.Count == 20 && first.NextCursor is not null, "Only the first batch is returned with a continuation cursor");
            var cards = first.Items.ToList();
            // Insert ahead of the cursor: later pages must not shift or repeat already seen cards.
            await connection.ExecuteAsync("INSERT dbo.SalonMaster VALUES(NEWID(),@firstLocation,N'New first salon',NULL,NULL,1,1,-1)", new { firstLocation });
            var cursor = first.NextCursor;
            var pageCount = 1;
            while (cursor is not null) {
                if (++pageCount > 10) throw new Exception("Pagination did not terminate");
                var page = await Page(cursor: cursor);
                cards.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            Check(cards.Count == 67 && cards.Select(item => item.SalonMasterID).Distinct().Count() == 67,
                "All eligible salons across locations are visited exactly once despite an insertion before the cursor");
            Check(cards.Count(item => item.SalonName == "Duplicate name") == 2, "Duplicate salon names paginate correctly using their unique IDs");
            Check(cards.All(item => item.SalonName is not "Hidden salon" and not "Inactive salon" and not "Closed location salon"),
                "Hidden salons, inactive salons and inactive locations never appear");
            Check(cards.All(item => item.LocationName.Length > 0 && item.SalonAddress == "" && item.ContactNumber == ""),
                "One query includes locations and normalizes nullable public details");
            Check((await Page("Salon 064")).Items.Single().SalonName == "Salon 064", "Server search finds salons beyond the first page");
            Check((await Page("Second city")).Items.All(item => item.LocationMasterID == secondLocation), "Search also matches the location name");
            Check((await Page("%_[]")).Items.Count == 1, "Search treats SQL wildcard characters literally");
            Check((await Page("' OR 1=1--")).Items.Count == 0, "Search is parameterized and cannot change query predicates");
            Check((await Page("தமிழ்")).Items.Single().SalonName == "தமிழ் சலூன்", "Multilingual salon search works");
            Check((await Page("does not exist")).NextCursor is null, "Empty searches end pagination");
            var single = await Page(size: 1);
            Check(single.Items.Count == 1 && single.NextCursor is not null, "Custom bounded page sizes work");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledRead = false;
            try { await service.GetAsync(new CustomerSalonQuery(), null, cancelled.Token); }
            catch (OperationCanceledException) { cancelledRead = true; }
            Check(cancelledRead, "Cancelled requests stop the database operation");
            Console.WriteLine($"{checks} SQL checks passed in a disposable database.");
        }
        finally {
            await connection.CloseAsync();
            SqlConnection.ClearAllPools();
            await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }
}
