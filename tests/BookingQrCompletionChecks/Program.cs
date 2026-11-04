using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;

// Real HTTP controller -> QR service -> SQL procedure, using a disposable local database.
// Existing application records and hosted services are never touched.
var configurationPath = Path.GetFullPath(args.Single());
var configuration = new ConfigurationBuilder().AddJsonFile(configurationPath).Build();
var settings = new SqlConnectionStringBuilder(configuration.GetConnectionString("dbconnection"));
if (!new[] { "localhost", ".", "(local)" }.Contains(settings.DataSource.ToLowerInvariant()))
    throw new InvalidOperationException("Only a local SQL Server is allowed.");
// The local SQL Server fixture does not have a TLS certificate installed.
settings.Encrypt = SqlConnectionEncryptOption.Optional;
var database = "SalonexaQrChecks_" + Guid.NewGuid().ToString("N");
settings.InitialCatalog = "master";
await using var master = new SqlConnection(settings.ConnectionString);
await master.OpenAsync();
await master.ExecuteAsync($"CREATE DATABASE [{database}]");
settings.InitialCatalog = database;
WebApplication? app = null;
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS: " + description);
    checks++;
}
try
{
    await using var db = new SqlConnection(settings.ConnectionString);
    await db.OpenAsync();
    await db.ExecuteAsync("""
        CREATE TABLE dbo.LocationMaster(LocationMasterID uniqueidentifier PRIMARY KEY, LocationName nvarchar(200));
        CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, IsActive bit NOT NULL,
            SalonName nvarchar(200) DEFAULT 'Fixture salon', LocationMasterID uniqueidentifier,
            SalonAddress nvarchar(500), ContactNumber nvarchar(20), OpeningTime time, ClosingTime time,
            ChairCount int DEFAULT 3, WeeklyHoliday int, Latitude float, Longitude float,
            City nvarchar(100), Pincode nvarchar(10), ImagePath nvarchar(500), IsVisibleToCustomers bit);
        CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier, IsActive bit NOT NULL, EmailAddress nvarchar(254));
        CREATE TABLE dbo.SalonService(SalonServiceID uniqueidentifier DEFAULT NEWID(), SalonMasterID uniqueidentifier,
            ServiceName nvarchar(150), Category nvarchar(80) DEFAULT 'Hair', Price decimal(18,2) DEFAULT 100,
            DurationMinutes int DEFAULT 30, Description nvarchar(500), ImagePath nvarchar(500), IsActive bit NOT NULL,
            CreatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(), UpdatedUtc datetime2(0));
        CREATE TABLE dbo.BookingStatusMaster(BookingStatusMasterID int PRIMARY KEY, BookingStatus nvarchar(50));
        INSERT dbo.BookingStatusMaster VALUES(1,'Selected'),(2,'Booked'),(3,'PaymentProcessing');
        CREATE TABLE dbo.CustomerRegistration(CustomerID uniqueidentifier PRIMARY KEY, FirstName nvarchar(150),LastName nvarchar(150), MobileNo nvarchar(20), GeneralAddress nvarchar(500),IsDeleted bit);
        CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier, BookingDate date, SeatCount int, BookingStatusMasterID int, SalonMasterID uniqueidentifier);
        CREATE TABLE dbo.TimeAllocation(TimeAllocationID uniqueidentifier PRIMARY KEY, FullTiming time);
        CREATE TABLE dbo.CustomerTimeSelection(SeatBookingDetailsID uniqueidentifier,TimeAllocationID uniqueidentifier,PRIMARY KEY(SeatBookingDetailsID,TimeAllocationID));
        CREATE TABLE dbo.HairCut_Item_Bill_Master(UniqueBillID uniqueidentifier,SeatBookingDetailsID uniqueidentifier,CreatedDate datetime,BillIDTotalAmount decimal(18,2),IsDeleted bit);
        CREATE TABLE dbo.HairCut_Item_Customer_Bill(UniqueBillID uniqueidentifier,HairCut_Item_ID uniqueidentifier,HairCut_Item_Total decimal(18,2),HairCut_Item_Quantity int,Created_Date datetime,IsDeleted bit);
        CREATE TABLE dbo.HairCut_GroupMaster(HairCutItemGroupMaster_ID uniqueidentifier,HairCutItemGroup nvarchar(100), OrderID int);
        CREATE TABLE dbo.HairCut_Item_List(HairCut_Item_ID uniqueidentifier,HairCut_Item_Code int,HairCut_Item_Name nvarchar(max),
            HairCut_Item_Price int,HairCut_Item_Quantity nvarchar(max),HairCut_Item_CGST decimal(18,2),HairCut_Item_SGST decimal(18,2),
            HairCut_Item_Taxable decimal(18,2),HairCutItemImage nvarchar(max),HairCutItemImageSystemPath nvarchar(max),
            HairCutItemImageS3Path nvarchar(max),HairCutItemGroupMaster_ID uniqueidentifier,Created_Date datetime,Created_By uniqueidentifier,IsDeleted bit);
        """);
    async Task Deploy()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configurationPath)!, "SQL_Update_QrCompletion_2026-10-21.sql"));
        // Keep the database guard, pointing it only at this disposable fixture.
        sql = sql.Replace("IF DB_NAME() <> N'InfinityHairArtsAdmin'", $"IF DB_NAME() <> N'{database}'");
        await db.ExecuteAsync(sql);
    }
    async Task DeployCounts()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configurationPath)!, "SQL_Update_BookingCounts_2026-10-22.sql"));
        sql = sql.Replace("IF DB_NAME() <> N'InfinityHairArtsAdmin'", $"IF DB_NAME() <> N'{database}'");
        await db.ExecuteAsync(sql);
        await DeployRange();
    }
    async Task DeployRange()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configurationPath)!, "SQL_Update_DashboardDateRange_2026-10-27.sql"));
        sql = sql.Replace("IF DB_NAME() <> N'InfinityHairArtsAdmin'", $"IF DB_NAME() <> N'{database}'");
        await db.ExecuteAsync(sql);
        sql = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configurationPath)!, "SQL_Update_DashboardAllCounts_2026-10-28.sql"));
        sql = sql.Replace("IF DB_NAME() <> N'InfinityHairArtsAdmin'", $"IF DB_NAME() <> N'{database}'");
        await db.ExecuteAsync(sql);
    }
    await Deploy();
    await DeployCounts();
    var salon = Guid.NewGuid();
    var otherSalon = Guid.NewGuid();
    var owner = Guid.NewGuid();
    var otherOwner = Guid.NewGuid();
    var customer = Guid.NewGuid();
    var slot = Guid.NewGuid();
    var secondSlot = Guid.NewGuid();
    var location = Guid.NewGuid();
    var secondCustomer = Guid.NewGuid();
    var foreignCustomer = Guid.NewGuid();
    var today = DateTime.UtcNow.AddMinutes(330).Date;
    await db.ExecuteAsync("""
        INSERT dbo.LocationMaster VALUES(@location,'Fixture location');
        INSERT dbo.SalonMaster(SalonMasterID,IsActive,LocationMasterID) VALUES(@salon,1,@location),(@otherSalon,1,@location);
        INSERT dbo.SalonOwner(SalonOwnerID,SalonMasterID,IsActive) VALUES(@owner,@salon,1),(@otherOwner,@otherSalon,1);
        INSERT dbo.SalonService(SalonMasterID,ServiceName,IsActive)
            VALUES(@salon,'Haircut',1),(@salon,'Styling',1),(@salon,'Inactive service',0),(@otherSalon,'Other salon service',1);
        INSERT dbo.CustomerRegistration VALUES(@customer,'QR','Fixture','9999999999','',0);
        INSERT dbo.CustomerRegistration VALUES(@secondCustomer,'Second','Fixture','9999999998','',0),(@foreignCustomer,'Other','Fixture','9999999997','',0);
        INSERT dbo.TimeAllocation VALUES(@slot,'00:00:00'),(@secondSlot,'00:01:00');
        """, new { salon, otherSalon, owner, otherOwner, customer, secondCustomer, foreignCustomer, location, slot, secondSlot });
    await db.ExecuteAsync("""
        UPDATE dbo.SalonService SET CreatedUtc = DATEADD(minute,-330,CAST(@today AS datetime2));
        UPDATE dbo.SalonService SET CreatedUtc = DATEADD(second,-1,DATEADD(minute,-330,DATEADD(day,1,CAST(@today AS datetime2))))
            WHERE SalonMasterID=@salon AND ServiceName='Styling';
        INSERT dbo.SalonService(SalonMasterID,ServiceName,IsActive,CreatedUtc) VALUES
            (@salon,'Earlier service',1,DATEADD(second,-1,DATEADD(minute,-330,CAST(@today AS datetime2)))),
            (@salon,'Future service',1,DATEADD(minute,-330,DATEADD(day,1,CAST(@today AS datetime2))));
        """, new { salon, today });
    async Task<Guid> Booking(DateTime date, bool multiple = false, Guid? salonId = null, int status = 2)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.SeatBookingDetails(SeatBookingDetailsID,CustomerID,BookingDate,SeatCount,BookingStatusMasterID,SalonMasterID)
            VALUES(@id,@customer,@date,1,@status,@salon);
            INSERT dbo.CustomerTimeSelection VALUES(@id,@slot);
            """, new { id, customer, date, status, salon = salonId ?? salon, slot });
        if (multiple) await db.ExecuteAsync("INSERT dbo.CustomerTimeSelection VALUES(@id,@secondSlot)", new { id, secondSlot });
        return id;
    }
    var booking = await Booking(today);
    var future = await Booking(today.AddDays(1));
    var foreign = await Booking(today, salonId: otherSalon);
    var unconfirmed = await Booking(today, status: 1);
    var multiple = await Booking(today.AddDays(-1), multiple: true);
    var simultaneous = await Booking(today.AddDays(-1), multiple: true);
    var rollback = await Booking(today.AddDays(-1));
    var legacy = await Booking(today.AddDays(-1));
    await db.ExecuteAsync("""
        UPDATE dbo.SeatBookingDetails SET CustomerID=@secondCustomer WHERE SeatBookingDetailsID=@future;
        UPDATE dbo.SeatBookingDetails SET CustomerID=@foreignCustomer WHERE SeatBookingDetailsID=@foreign;
        INSERT dbo.HairCut_Item_Bill_Master VALUES
            (NEWID(),@booking,@today,125.50,0), (NEWID(),@booking,DATEADD(hour,23,CAST(@today AS datetime)),74.50,0),
            (NEWID(),@booking,@today,999,1), (NEWID(),@booking,DATEADD(day,-1,@today),300,0),
            (NEWID(),@booking,DATEADD(day,1,@today),400,0), (NEWID(),@foreign,@today,700,0);
        """, new { secondCustomer, foreignCustomer, future, foreign, booking, today });

    var billedService = Guid.NewGuid();
    await db.ExecuteAsync("""
        INSERT dbo.HairCut_Item_List(HairCut_Item_ID,HairCut_Item_Name,IsDeleted) VALUES(@billedService,'Fixture haircut',0);
        INSERT dbo.HairCut_Item_Customer_Bill
        SELECT UniqueBillID,@billedService,BillIDTotalAmount,1,CreatedDate,IsDeleted
        FROM dbo.HairCut_Item_Bill_Master;
        """, new { billedService });

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:dbconnection"] = settings.ConnectionString });
    builder.Services.AddControllers().AddApplicationPart(typeof(AdminController).Assembly);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ISalonOwnerAuthenticationService, SalonOwnerAuthenticationService>();
    builder.Services.AddScoped<SalonOwnerDashboardService>();
    builder.Services.AddScoped<BookingQrService>();
    builder.Services.AddScoped<OwnerAppointmentsService>();
    builder.Services.AddScoped<SalonOwnerPersistentSignIn>();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession();
    app = builder.Build();
    app.UseRouting();
    app.UseSession();
    app.MapControllers();
    // Test fixture sign-in exists only on this loopback harness, never in the API.
    app.MapPost("/test/sign-in", (HttpContext context) =>
    {
        context.Session.SetString("SalonOwnerID", owner.ToString());
        context.Session.SetString("SalonMasterID", salon.ToString());
        return Results.NoContent();
    });
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(address) };
    string Reference(Guid id, Guid? time = null) => $"infinityhairarts:booking:v1:{id}:{time ?? slot}";
    async Task<(HttpStatusCode Status, JsonElement Body)> Complete(Guid id, Guid? time = null)
    {
        using var response = await http.PostAsJsonAsync("/api/Admin/complete-booking-qr", new { qrReference = Reference(id, time) });
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }
    async Task<string> Status(Guid id) => (await db.ExecuteScalarAsync<string>("SELECT CompletionStatus FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@id", new { id }))!;
    async Task<int> Completions(Guid id) => await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.BookingQrCompletion WHERE SeatBookingDetailsID=@id", new { id });
    async Task<JsonElement> Summary(DateTime date)
    {
        using var response = await http.GetAsync("/api/Admin/dashboard?date=" + date.ToString("yyyy-MM-dd"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("summary");
    }

    Check((await Complete(booking)).Status == HttpStatusCode.Unauthorized, "Anonymous completion is rejected");
    await http.PostAsync("/test/sign-in", null);
    var initialSummary = await Summary(today);
    Check(initialSummary.GetProperty("todayBookings").GetInt32() == 1
        && initialSummary.GetProperty("pendingBookings").GetInt32() == 1
        && initialSummary.GetProperty("completedBookings").GetInt32() == 0,
        "Selected-date counts read Pending and Completed from confirmed bookings for this salon only");
    Check(initialSummary.GetProperty("totalBookings").GetInt32() == 1
        && initialSummary.GetProperty("totalPendingBookings").GetInt32() == 1
        && initialSummary.GetProperty("totalCompletedBookings").GetInt32() == 0,
        "Every booking total uses the selected date, excluding other dates, salons and unconfirmed selections");
    Check(initialSummary.GetProperty("todaySeats").GetInt32() == 1
        && initialSummary.GetProperty("todayRevenue").GetDecimal() == 200m
        && initialSummary.GetProperty("totalCustomers").GetInt32() == 1
        && initialSummary.GetProperty("activeServices").GetInt32() == 2,
        "All six card values are date filtered, including distinct customers and active services at both India-day boundaries");
    using (var response = await http.GetAsync("/api/Admin/dashboard?date=" + today.ToString("yyyy-MM-dd")))
    {
        Check(response.Headers.CacheControl?.NoStore == true,
            "Dashboard counts cannot be cached between database updates");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Check(body.GetProperty("salon").GetProperty("salonMasterID").GetGuid() == salon
            && body.GetProperty("services").GetArrayLength() == 5
            && body.GetProperty("date").GetString() == today.ToString("yyyy-MM-dd"),
            "Dashboard returns the salon, service collection and date required to render the summary cards");
    }
    var priorSummary = await Summary(today.AddDays(-1));
    var futureSummary = await Summary(today.AddDays(1));
    Check(priorSummary.GetProperty("todayRevenue").GetDecimal() == 300m
        && futureSummary.GetProperty("todayRevenue").GetDecimal() == 400m
        && futureSummary.GetProperty("todayBookings").GetInt32() == 1
        && futureSummary.GetProperty("pendingBookings").GetInt32() == 1
        && futureSummary.GetProperty("totalCustomers").GetInt32() == 1
        && futureSummary.GetProperty("activeServices").GetInt32() == 1,
        "Changing date reloads customers and services too, including services created exactly at the next India-day boundary");
    using (var rangeResponse = await http.GetAsync($"/api/Admin/dashboard?fromDate={today.AddDays(-1):yyyy-MM-dd}&toDate={today:yyyy-MM-dd}"))
    {
        rangeResponse.EnsureSuccessStatusCode();
        var range = await rangeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var summary = range.GetProperty("summary");
        Check(summary.GetProperty("todayBookings").GetInt32() == 5
            && summary.GetProperty("pendingBookings").GetInt32() == 5
            && summary.GetProperty("completedBookings").GetInt32() == 0
            && summary.GetProperty("todaySeats").GetInt32() == 5
            && summary.GetProperty("todayRevenue").GetDecimal() == 500m,
            "Date range includes bookings and nondeleted bills on both boundary dates and excludes the following day and other salons");
        Check(range.GetProperty("fromDate").GetString() == today.AddDays(-1).ToString("yyyy-MM-dd")
            && range.GetProperty("toDate").GetString() == today.ToString("yyyy-MM-dd")
            && range.GetProperty("upcomingAppointments").GetArrayLength() == 5
            && summary.GetProperty("totalCustomers").GetInt32() == 1
            && summary.GetProperty("activeServices").GetInt32() == 3
            && summary.GetProperty("totalBookings").GetInt32() == 5
            && summary.GetProperty("totalPendingBookings").GetInt32() == 5,
            "Range response filters customers, services and all booking totals while preserving the complete service catalogue");
        var days = range.GetProperty("weeklyBookings").EnumerateArray().ToArray();
        Check(days.Length == 2 && days[0].GetProperty("bookingDate").GetDateTime() == today.AddDays(-1)
            && days[^1].GetProperty("bookingDate").GetDateTime() == today
            && days.Sum(day => day.GetProperty("bookingCount").GetInt32()) == 5,
            "Activity includes exactly the selected dates and agrees with the booking card");
        var popular = range.GetProperty("popularServices").EnumerateArray().Single();
        Check(popular.GetProperty("bookingCount").GetInt32() == 3
            && popular.GetProperty("revenue").GetDecimal() == 500m,
            "Popular services use the selected range and exclude deleted bills and other salons");
        var appointmentDates = range.GetProperty("upcomingAppointments").EnumerateArray()
            .Select(row => row.GetProperty("bookingDate").GetDateTime()).ToArray();
        Check(appointmentDates.SequenceEqual(appointmentDates.Order()), "Range appointments are sorted by date before appointment time");
    }
    using (var singleDay = await http.GetAsync($"/api/Admin/dashboard?fromDate={today:yyyy-MM-dd}&toDate={today:yyyy-MM-dd}"))
    {
        var body = await singleDay.Content.ReadFromJsonAsync<JsonElement>();
        Check(body.GetProperty("summary").GetRawText() == initialSummary.GetRawText(),
            "Equal From and To dates preserve single-date totals and legacy date requests");
    }
    foreach (var query in new[] {
        $"fromDate={today:yyyy-MM-dd}&toDate={today.AddDays(-1):yyyy-MM-dd}",
        "fromDate=1999-12-31&toDate=2026-10-27", "fromDate=2026-10-27&toDate=2101-01-01",
        "fromDate=not-a-date&toDate=2026-10-27" })
    {
        using var invalidRange = await http.GetAsync("/api/Admin/dashboard?" + query);
        Check(invalidRange.StatusCode == HttpStatusCode.BadRequest, "Reversed, out-of-bounds and malformed date ranges return 400");
    }
    using (var wide = await http.GetAsync($"/api/Admin/dashboard?fromDate={today.AddDays(-120):yyyy-MM-dd}&toDate={today.AddDays(1):yyyy-MM-dd}"))
    {
        wide.EnsureSuccessStatusCode();
        var data = await wide.Content.ReadFromJsonAsync<JsonElement>();
        Check(data.GetProperty("weeklyBookings").GetArrayLength() == 122
            && data.GetProperty("summary").GetProperty("totalCustomers").GetInt32() == 2
            && data.GetProperty("summary").GetProperty("activeServices").GetInt32() == 4,
            "Ranges longer than 100 days include every day, distinct customers and active services without recursion errors");
    }
    await DeployRange();
    Check((await Summary(today)).GetProperty("todayBookings").GetInt32() == 1,
        "Date-range migration can be rerun without changing existing bookings");
    using (var invalid = await http.PostAsJsonAsync("/api/Admin/complete-booking-qr", new { qrReference = "invalid" }))
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Malformed QR is rejected");
    foreach (var invalid in new[] { (future, HttpStatusCode.Conflict), (foreign, HttpStatusCode.NotFound),
        (unconfirmed, HttpStatusCode.NotFound) })
    {
        Check((await Complete(invalid.Item1)).Status == invalid.Item2, "Future, other-salon and unconfirmed bookings are rejected");
        Check(await Completions(invalid.Item1) == 0 && await Status(invalid.Item1) == "Pending", "Rejected QR leaves the database unchanged");
    }
    Check((await Complete(booking, Guid.NewGuid())).Status == HttpStatusCode.NotFound && await Completions(booking) == 0,
        "A slot not belonging to the booking cannot complete it");

    var completed = await Complete(booking);
    Check(completed.Status == HttpStatusCode.OK && completed.Body.GetProperty("bookingStatus").GetString() == "Completed", "Completion endpoint returns the committed status");
    Check(await Status(booking) == "Completed" && await Completions(booking) == 1, "Completed status and QR audit row are stored in SQL");
    var completedUtc = await db.ExecuteScalarAsync<DateTime>("SELECT CompletedUtc FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@booking", new { booking });
    // DATETIME2(0) rounds to the nearest second, including up to the next second.
    Check(completedUtc > DateTime.UtcNow.AddMinutes(-2) && completedUtc <= DateTime.UtcNow.AddSeconds(1), "Booking saves a UTC completion timestamp");
    var repeated = await Complete(booking);
    Check(repeated.Status == HttpStatusCode.OK && repeated.Body.GetProperty("wasAlreadyCompleted").GetBoolean()
        && await Completions(booking) == 1, "Repeating the scan succeeds without duplicate records");
    Check(await db.ExecuteScalarAsync<DateTime>("SELECT CompletedUtc FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@booking", new { booking }) == completedUtc,
        "Repeated completion preserves the original timestamp");
    Check(await db.ExecuteScalarAsync<int>("SELECT BookingStatusMasterID FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@booking", new { booking }) == 2,
        "Completion preserves reservation confirmation and slot occupancy");

    var partial = await Complete(multiple);
    Check(partial.Body.GetProperty("bookingStatus").GetString() == "Pending" && await Status(multiple) == "Pending", "One appointment cannot complete a multi-time booking");
    var partialSummary = await Summary(today.AddDays(-1));
    Check(partialSummary.GetProperty("todayBookings").GetInt32() == 4
        && partialSummary.GetProperty("pendingBookings").GetInt32() == 4
        && partialSummary.GetProperty("completedBookings").GetInt32() == 0,
        "A partially completed multi-time booking is counted once as Pending");
    Check(await db.ExecuteScalarAsync<DateTime?>("SELECT CompletedUtc FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@multiple", new { multiple }) is null,
        "Partly completed booking has no booking completion timestamp");
    Check((await Complete(multiple, secondSlot)).Body.GetProperty("bookingStatus").GetString() == "Completed" && await Status(multiple) == "Completed",
        "The final appointment completes the entire booking");
    var concurrent = await Task.WhenAll(Complete(simultaneous), Complete(simultaneous, secondSlot), Complete(simultaneous));
    Check(concurrent.All(result => result.Status == HttpStatusCode.OK) && await Status(simultaneous) == "Completed" && await Completions(simultaneous) == 2,
        "Concurrent scans serialize without duplicates or lost completion status");

    // Inject a write failure after the QR insert to prove the entire save rolls back.
    await db.ExecuteAsync($"""
        CREATE TRIGGER dbo.TestCompletionFailure ON dbo.SeatBookingDetails AFTER UPDATE AS
        IF EXISTS(SELECT 1 FROM inserted WHERE SeatBookingDetailsID='{rollback}' AND CompletionStatus='Completed')
            THROW 51099,'Injected completion write failure',1;
        """);
    using (var failed = await http.PostAsJsonAsync("/api/Admin/complete-booking-qr", new { qrReference = Reference(rollback) }))
        Check(failed.StatusCode == HttpStatusCode.InternalServerError && await Status(rollback) == "Pending" && await Completions(rollback) == 0,
            "Failed status update rolls back the QR audit insert");
    await db.ExecuteAsync("DROP TRIGGER dbo.TestCompletionFailure");
    Check((await Complete(rollback)).Status == HttpStatusCode.OK, "A rolled-back completion can be retried");

    using (var dashboard = await http.GetAsync("/api/Admin/dashboard?date=" + today.ToString("yyyy-MM-dd")))
    {
        var body = await dashboard.Content.ReadFromJsonAsync<JsonElement>();
        Check(dashboard.StatusCode == HttpStatusCode.OK && body.GetProperty("summary").GetProperty("completedBookings").GetInt32() == 1,
            "Dashboard reload reads completed count from the database for the selected date and salon");
        var rows = body.GetProperty("upcomingAppointments").EnumerateArray().ToArray();
        Check(rows.Length == 1 && rows[0].GetProperty("bookingStatus").GetString() == "Completed", "Dashboard appointment displays the persisted Completed status");
    }
    var savedSummary = await Summary(today);
    Check(savedSummary.GetProperty("pendingBookings").GetInt32() == 0
        && savedSummary.GetProperty("totalBookings").GetInt32() == 1
        && savedSummary.GetProperty("totalPendingBookings").GetInt32() == 0
        && savedSummary.GetProperty("totalCompletedBookings").GetInt32() == 1,
        "QR completion moves bookings from Pending to Completed in database-backed totals without duplicates");
    var emptyDateSummary = await Summary(today.AddDays(-30));
    Check(emptyDateSummary.GetProperty("todayBookings").GetInt32() == 0
        && emptyDateSummary.GetProperty("pendingBookings").GetInt32() == 0
        && emptyDateSummary.GetProperty("completedBookings").GetInt32() == 0
        && emptyDateSummary.GetProperty("todaySeats").GetInt32() == 0
        && emptyDateSummary.GetProperty("totalBookings").GetInt32() == 0
        && emptyDateSummary.GetProperty("totalCustomers").GetInt32() == 0
        && emptyDateSummary.GetProperty("activeServices").GetInt32() == 0,
        "An empty date returns zero for every count, including customers, services and legacy total fields");
    for (var i = 0; i < 35; i++) await Booking(today);
    using (var dashboard = await http.GetAsync("/api/Admin/dashboard?date=" + today.ToString("yyyy-MM-dd")))
    {
        var body = await dashboard.Content.ReadFromJsonAsync<JsonElement>();
        var summary = body.GetProperty("summary");
        Check(body.GetProperty("upcomingAppointments").GetArrayLength() == 30
            && summary.GetProperty("todayBookings").GetInt32() == 36
            && summary.GetProperty("pendingBookings").GetInt32() == 35
            && summary.GetProperty("completedBookings").GetInt32() == 1
            && summary.GetProperty("totalBookings").GetInt32() == 36
            && summary.GetProperty("totalPendingBookings").GetInt32() == 35
            && summary.GetProperty("totalCompletedBookings").GetInt32() == 1
            && summary.GetProperty("upcomingBookings").GetInt32() == 35,
            "Database counts include every booking even when the appointment preview is limited to 30 rows");
    }
    var dashboards = new SalonOwnerDashboardService(builder.Configuration);
    var otherSummary = (await dashboards.GetDashboardAsync(otherOwner, otherSalon, today, default)).Summary;
    Check(otherSummary.TotalBookings == 1 && otherSummary.TotalPendingBookings == 1 && otherSummary.TotalCompletedBookings == 0,
        "Date-filtered booking counts stay isolated to the signed-in salon");
    Check(otherSummary.TodayRevenue == 700m && otherSummary.TotalCustomers == 1 && otherSummary.ActiveServices == 1,
        "Billed amount, customers and active services stay isolated to the signed-in salon");
    var emptySalon = Guid.NewGuid();
    var emptyOwner = Guid.NewGuid();
    await db.ExecuteAsync("INSERT dbo.SalonMaster(SalonMasterID,IsActive,LocationMasterID) VALUES(@emptySalon,1,@location); INSERT dbo.SalonOwner(SalonOwnerID,SalonMasterID,IsActive) VALUES(@emptyOwner,@emptySalon,1)", new { emptySalon, emptyOwner, location });
    var emptySummary = (await dashboards.GetDashboardAsync(emptyOwner, emptySalon, today, default)).Summary;
    Check(emptySummary.TodayBookings == 0 && emptySummary.PendingBookings == 0 && emptySummary.CompletedBookings == 0
        && emptySummary.TotalBookings == 0 && emptySummary.TotalPendingBookings == 0 && emptySummary.TotalCompletedBookings == 0,
        "A salon without bookings receives explicit zero counts");
    Check(emptySummary.TodayRevenue == 0 && emptySummary.TotalCustomers == 0 && emptySummary.ActiveServices == 0,
        "A salon without bills, customers or services receives real zero totals");
    await DeployCounts();
    Check((await Summary(today)).GetProperty("totalBookings").GetInt32() == 36,
        "Standalone count migration can be rerun without changing booking data");
    await db.ExecuteAsync("INSERT dbo.BookingQrCompletion VALUES(@legacy,@slot,@owner,DATEADD(MINUTE,-5,SYSUTCDATETIME()))", new { legacy, slot, owner });
    await Deploy();
    Check(await Status(legacy) == "Completed", "Rerunning the migration backfills historical QR completions");
    Check(await Status(booking) == "Completed" && await Completions(booking) == 1, "Rerunning database scripts preserves existing completions");
    async Task DeployStatusTabs()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configurationPath)!, "SQL_Update_BookingStatusTabs_2026-10-31.sql"));
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
        sql = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configurationPath)!, "SQL_Update_SalonServices_2026-11-03.sql"));
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
    }
    await DeployStatusTabs();
    await DeployStatusTabs();
    var menuDashboard = await dashboards.GetDashboardAsync(owner, salon, today, default);
    Check(menuDashboard.Summary.ActiveServices == 2 && menuDashboard.Services.Count == 5
        && menuDashboard.Services.Any(item => item.ServiceName == "Haircut" && item.CanEdit),
        "The dashboard reads migrated salon haircut items and retains India date-filtered service counts");
    var tabDate = today.AddDays(-7);
    var pendingIDs = new List<Guid>();
    for (var i=0;i<35;i++) pendingIDs.Add(await Booking(tabDate));
    var startedBooking = await Booking(tabDate);
    var finishedBooking = await Booking(tabDate);
    var multiStartedBooking = await Booking(tabDate, multiple:true);
    using var anonymous = new HttpClient { BaseAddress = new Uri(address) };
    string TabUrl(string status, int page=1) => $"/api/Admin/appointments?fromDate={tabDate:yyyy-MM-dd}&toDate={tabDate:yyyy-MM-dd}&status={status}&page={page}";
    async Task<JsonElement> Tab(string status, int page=1) {
        using var response = await http.GetAsync(TabUrl(status,page)); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    async Task<HttpResponseMessage> Start(Guid id) => await http.PostAsJsonAsync($"/api/Admin/appointments/{id}/start",new {});
    Check((await anonymous.GetAsync(TabUrl("Pending"))).StatusCode==HttpStatusCode.Unauthorized,
        "Status tabs require an authenticated owner");
    Check((await anonymous.PostAsJsonAsync($"/api/Admin/appointments/{startedBooking}/start",new {})).StatusCode==HttpStatusCode.Unauthorized,
        "Starting service requires an authenticated owner");
    Check((await Start(foreign)).StatusCode==HttpStatusCode.NotFound && (await Start(unconfirmed)).StatusCode==HttpStatusCode.NotFound,
        "Owners cannot start other salons' bookings or unconfirmed slot selections");
    Check((await Start(future)).StatusCode==HttpStatusCode.Conflict && await Status(future)=="Pending",
        "Future bookings cannot be started before the booked India time");
    var concurrentStarts = await Task.WhenAll(Start(startedBooking),Start(startedBooking));
    var startBodies = await Task.WhenAll(concurrentStarts.Select(response=>response.Content.ReadFromJsonAsync<JsonElement>()));
    Check(concurrentStarts.All(response=>response.IsSuccessStatusCode) && startBodies.Select(body=>body.GetProperty("startedUtc").GetString()).Distinct().Count()==1,
        "Concurrent Start service requests persist one original start time");
    Check(startBodies.Count(body=>body.GetProperty("wasAlreadyStarted").GetBoolean())==1 && await Status(startedBooking)=="InProgress",
        "Repeated Start service is idempotent and saves InProgress");
    Check((await Complete(finishedBooking)).Body.GetProperty("bookingStatus").GetString()=="Completed",
        "Existing direct QR completion remains supported");
    Check((await Start(multiStartedBooking)).IsSuccessStatusCode,
        "A multi-slot booking can be started");
    Check((await Complete(multiStartedBooking)).Body.GetProperty("bookingStatus").GetString()=="InProgress",
        "Completing one QR preserves InProgress until all selected appointments are completed");
    var pendingTab=await Tab("Pending");
    var progressTab=await Tab("InProgress");
    var completedTab=await Tab("Completed");
    Check(pendingTab.GetProperty("counts").GetProperty("pending").GetInt32()==35
        && pendingTab.GetProperty("counts").GetProperty("inProgress").GetInt32()==2
        && pendingTab.GetProperty("counts").GetProperty("completed").GetInt32()==1,
        "Tab counts cover every confirmed booking in the selected date range");
    var firstPage=pendingTab.GetProperty("items").EnumerateArray().Select(item=>item.GetProperty("seatBookingDetailsID").GetGuid()).ToArray();
    var secondPage=(await Tab("Pending",2)).GetProperty("items").EnumerateArray().Select(item=>item.GetProperty("seatBookingDetailsID").GetGuid()).ToArray();
    var thirdPage=(await Tab("Pending",3)).GetProperty("items").EnumerateArray().Select(item=>item.GetProperty("seatBookingDetailsID").GetGuid()).ToArray();
    var fourthPage=(await Tab("Pending",4)).GetProperty("items").EnumerateArray().Select(item=>item.GetProperty("seatBookingDetailsID").GetGuid()).ToArray();
    var allPages=firstPage.Concat(secondPage).Concat(thirdPage).Concat(fourthPage).ToArray();
    Check(firstPage.Length==10 && secondPage.Length==10 && thirdPage.Length==10 && fourthPage.Length==5 && allPages.Distinct().Count()==35
        && allPages.All(pendingIDs.Contains),
        "Ten-row status pages return all 35 matching bookings without duplicates or the overview preview limit");
    Check(progressTab.GetProperty("items").GetArrayLength()==2 && completedTab.GetProperty("items").GetArrayLength()==1,
        "InProgress and Completed tabs return only matching bookings");
    Check((await http.GetAsync(TabUrl("invalid"))).StatusCode==HttpStatusCode.BadRequest
        && (await http.GetAsync(TabUrl("Pending",0))).StatusCode==HttpStatusCode.BadRequest,
        "Invalid tab status and page are rejected");
    var tabSummary=await Summary(tabDate);
    Check(tabSummary.GetProperty("pendingBookings").GetInt32()==35 && tabSummary.GetProperty("inProgressBookings").GetInt32()==2
        && tabSummary.GetProperty("completedBookings").GetInt32()==1,
        "Dashboard summary counts reflect saved Pending, InProgress and Completed statuses");
    Check((await Complete(multiStartedBooking,secondSlot)).Body.GetProperty("bookingStatus").GetString()=="Completed",
        "The final QR moves a started booking from InProgress to Completed");
    Check((await Start(multiStartedBooking)).StatusCode==HttpStatusCode.Conflict && await Status(multiStartedBooking)=="Completed",
        "Completed bookings cannot be restarted");
    var startedUtc=await db.ExecuteScalarAsync<DateTime>("SELECT StartedUtc FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@startedBooking",new {startedBooking});
    await DeployStatusTabs();
    Check(await Status(startedBooking)=="InProgress" && await db.ExecuteScalarAsync<DateTime>("SELECT StartedUtc FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@startedBooking",new {startedBooking})==startedUtc,
        "Rerunning the status migration preserves service starts and timestamps");
    var otherTabs=await new OwnerAppointmentsService(builder.Configuration).GetAsync(otherOwner,otherSalon,tabDate,tabDate,"Pending",1,default);
    Check(otherTabs.Items.Count==0 && otherTabs.Counts.Pending==0 && otherTabs.Counts.InProgress==0 && otherTabs.Counts.Completed==0,
        "All three status tabs stay isolated to the signed-in salon");
    for (var i=0;i<12;i++) {
        var past=today.AddDays(-40+i);
        await Booking(past);
        var inProgress=await Booking(past);
        (await Start(inProgress)).EnsureSuccessStatusCode();
        var recentCompletedBooking=await Booking(past);
        if ((await Complete(recentCompletedBooking)).Status!=HttpStatusCode.OK) throw new Exception("Recent-view fixture QR completion failed.");
    }
    foreach(var status in new[]{"Pending","InProgress","Completed"}) {
        using var response=await http.GetAsync($"/api/Admin/appointments?status={status}");
        response.EnsureSuccessStatusCode();
        var recent=await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows=recent.GetProperty("items").EnumerateArray().ToArray();
        Check(recent.GetProperty("pageSize").GetInt32()==10 && rows.Length==10 && recent.GetProperty("totalCount").GetInt32()>10,
            $"{status} loads the latest 10 appointments without requiring any dates");
        var dates=rows.Select(row=>row.GetProperty("bookingDate").GetDateTime()).ToArray();
        Check(dates.SequenceEqual(dates.OrderByDescending(date=>date)) && rows.All(row=>row.GetProperty("bookingStatus").GetString()==status),
            $"Unfiltered {status} appointments are ordered by newest appointment date first");
    }
    Check((await http.GetAsync($"/api/Admin/appointments?fromDate={tabDate:yyyy-MM-dd}")).StatusCode==HttpStatusCode.BadRequest
        && (await http.GetAsync($"/api/Admin/appointments?toDate={tabDate:yyyy-MM-dd}")).StatusCode==HttpStatusCode.BadRequest,
        "Partially supplied dates are rejected rather than silently broadening the appointment search");
    var emptyOtherDates=await new OwnerAppointmentsService(builder.Configuration).GetAsync(otherOwner,otherSalon,null,null,"Pending",1,default);
    Check(emptyOtherDates.TotalCount==1 && emptyOtherDates.Items.Single().SeatBookingDetailsID==foreign,
        "Unfiltered recent appointments and counts remain isolated to the signed-in salon");
    var amountDate=today.AddDays(-90);
    var amountBooking=await Booking(amountDate,multiple:true);
    var amountPending=await Booking(amountDate);
    var amountInProgress=await Booking(amountDate);
    var amountZero=await Booking(amountDate);
    var amountMissing=await Booking(amountDate);
    var amountForeign=await Booking(amountDate,salonId:otherSalon);
    var amountOutside=await Booking(amountDate.AddDays(-1));
    await db.ExecuteAsync("""
        INSERT dbo.HairCut_Item_Bill_Master VALUES
            (NEWID(),@amountBooking,DATEADD(day,-30,@amountDate),125.50,0),
            (NEWID(),@amountBooking,DATEADD(day,30,@amountDate),74.50,0),
            (NEWID(),@amountBooking,@amountDate,999,1),
            (NEWID(),@amountPending,@amountDate,1000,0),
            (NEWID(),@amountInProgress,@amountDate,2000,0),
            (NEWID(),@amountZero,@amountDate,0,0),
            (NEWID(),@amountForeign,@amountDate,700,0),
            (NEWID(),@amountOutside,@amountDate,500,0);
        """,new {amountBooking,amountPending,amountInProgress,amountZero,amountForeign,amountOutside,amountDate});
    (await Start(amountBooking)).EnsureSuccessStatusCode();
    (await Start(amountInProgress)).EnsureSuccessStatusCode();
    await Complete(amountZero); await Complete(amountMissing); await Complete(amountOutside);
    await db.ExecuteAsync("UPDATE dbo.SeatBookingDetails SET CompletionStatus='Completed' WHERE SeatBookingDetailsID=@amountForeign",new {amountForeign});
    Check((await Summary(amountDate)).GetProperty("completedRevenue").GetDecimal()==0,
        "Completed amount excludes pending, in-progress, other-salon and out-of-range booking bills");
    await Complete(amountBooking);
    Check((await Summary(amountDate)).GetProperty("completedRevenue").GetDecimal()==0,
        "A partly completed multi-slot booking does not contribute to the completed amount");
    await Complete(amountBooking,secondSlot);
    Check((await Summary(amountDate)).GetProperty("completedRevenue").GetDecimal()==200m,
        "Final completion totals nondeleted booking bills even when bill creation dates differ from the appointment date");
    using(var amountResponse=await http.GetAsync($"/api/Admin/appointments?fromDate={amountDate:yyyy-MM-dd}&toDate={amountDate:yyyy-MM-dd}&status=Completed")) {
        amountResponse.EnsureSuccessStatusCode();
        var amountItems=(await amountResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToArray();
        Check(amountItems.Single(item=>item.GetProperty("seatBookingDetailsID").GetGuid()==amountBooking).GetProperty("billAmount").GetDecimal()==200m,
            "Completed appointment amounts sum multiple bills once without multiplying by appointment slots");
        Check(amountItems.Single(item=>item.GetProperty("seatBookingDetailsID").GetGuid()==amountZero).GetProperty("billAmount").GetDecimal()==0m
            && amountItems.Single(item=>item.GetProperty("seatBookingDetailsID").GetGuid()==amountMissing).GetProperty("billAmount").ValueKind==JsonValueKind.Null,
            "Completed appointments distinguish an actual zero bill from no bill");
    }
    using(var recentAmounts=await http.GetAsync("/api/Admin/appointments?status=Completed")) {
        recentAmounts.EnsureSuccessStatusCode();
        var amountItems=(await recentAmounts.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray();
        Check(amountItems.Single(item=>item.GetProperty("seatBookingDetailsID").GetGuid()==booking).GetProperty("billAmount").GetDecimal()==900m,
            "Latest completed appointments include their linked bill amount without date filtering");
    }
    await Complete(amountBooking,secondSlot);
    Check((await Summary(amountDate)).GetProperty("completedRevenue").GetDecimal()==200m,
        "Repeated QR completion does not increase the completed amount");
    using(var amountDashboard=await http.GetAsync($"/api/Admin/dashboard?date={amountDate:yyyy-MM-dd}")) {
        amountDashboard.EnsureSuccessStatusCode();
        var amountBody=await amountDashboard.Content.ReadFromJsonAsync<JsonElement>();
        Check(amountBody.GetProperty("upcomingAppointments").EnumerateArray()
            .Single(item=>item.GetProperty("seatBookingDetailsID").GetGuid()==amountBooking).GetProperty("billAmount").GetDecimal()==200m,
            "The overview appointment data includes the completed booking amount");
    }
    await DeployStatusTabs();
    Check((await Summary(amountDate)).GetProperty("completedRevenue").GetDecimal()==200m,
        "Rerunning the migration preserves completed booking amounts");
    var legacyAmountBooking=await Booking(amountDate);
    var legacyAmountBill=Guid.NewGuid();
    var deletedItemsBill=Guid.NewGuid();
    await db.ExecuteAsync("""
        INSERT dbo.HairCut_Item_Bill_Master VALUES
            (@legacyAmountBill,@legacyAmountBooking,@amountDate,0,0),
            (@deletedItemsBill,@legacyAmountBooking,@amountDate,500,0);
        INSERT dbo.HairCut_Item_Customer_Bill VALUES
            (@legacyAmountBill,@billedService,110.25,1,@amountDate,0),
            (@legacyAmountBill,@billedService,89.75,1,@amountDate,0),
            (@legacyAmountBill,@billedService,900,1,@amountDate,1),
            (@deletedItemsBill,@billedService,500,1,@amountDate,1);
        """,new {legacyAmountBooking,legacyAmountBill,deletedItemsBill,amountDate,billedService});
    await Complete(legacyAmountBooking);
    var legacyAmountPage=await new OwnerAppointmentsService(builder.Configuration).GetAsync(owner,salon,amountDate,amountDate,"Completed",1,default);
    Check(legacyAmountPage.Items.Single(item=>item.SeatBookingDetailsID==legacyAmountBooking).BillAmount==200m,
        "Completed amounts use recorded service lines when legacy bill headers are zero");
    Check((await Summary(amountDate)).GetProperty("completedRevenue").GetDecimal()==400m,
        "Completed totals include priced legacy services while excluding deleted service lines");
    await db.ExecuteAsync("UPDATE dbo.HairCut_Item_Bill_Master SET BillIDTotalAmount=7777 WHERE UniqueBillID=@legacyAmountBill",new {legacyAmountBill});
    Check((await Summary(amountDate)).GetProperty("completedRevenue").GetDecimal()==400m,
        "Service-line-backed amounts stay aligned with customer booking history instead of stale bill-header totals");
    Check(await db.ExecuteScalarAsync<int>("SELECT @@TRANCOUNT") == 0, "Checks leave no open transaction");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    SqlConnection.ClearAllPools();
    // This generated database name is the only drop target; application DB is untouched.
    await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
}
Console.WriteLine($"PASS: {checks} QR persistence checks; disposable database removed.");
