using System.Net;
using System.Net.Http.Json;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using static InfinityHairartsAPI.Modals.AppoinmentModal;

var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln"))) root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
var source = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")).Build();
var settings = new SqlConnectionStringBuilder(source.GetConnectionString("dbconnection"));
if (!new[] { ".", "localhost", "127.0.0.1", "(localdb)", Environment.MachineName }.Contains(settings.DataSource.Split('\\', ',')[0], StringComparer.OrdinalIgnoreCase))
    throw new Exception("Timing checks only use a disposable local SQL database.");
settings.Encrypt = SqlConnectionEncryptOption.Optional; settings.InitialCatalog = "master";
await using var master = new SqlConnection(settings.ConnectionString); await master.OpenAsync();
var name = "SalonexaTimingChecks_" + Guid.NewGuid().ToString("N");
await master.ExecuteAsync($"CREATE DATABASE [{name}]");
settings.InitialCatalog = name;
await using var db = new SqlConnection(settings.ConnectionString);
WebApplication? app = null;
try
{
    await db.OpenAsync();
    await db.ExecuteAsync("""
        CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, IsActive bit NOT NULL);
        CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier, IsActive bit);
        CREATE TABLE dbo.TimeAllocation(TimeAllocationID uniqueidentifier PRIMARY KEY, FullTiming time, IsDeleted bit DEFAULT 0, BookingAvailableStatus bit DEFAULT 1);
        CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier, SalonMasterID uniqueidentifier,
            BookingDate date, SeatCount int, BookingStatusMasterID int, ReservationVersion bigint DEFAULT 0, CreatedOn datetime DEFAULT GETDATE());
        CREATE TABLE dbo.CustomerTimeSelection(TimeSelectionID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier, SeatBookingDetailsID uniqueidentifier,
            TimeAllocationID uniqueidentifier, CreatedOn datetime DEFAULT GETDATE());
        CREATE TABLE dbo.HairCut_Item_Bill_Master(UniqueBillID uniqueidentifier PRIMARY KEY, SeatBookingDetailsID uniqueidentifier, CreatedBy uniqueidentifier, IsDeleted bit DEFAULT 0);
        CREATE TABLE dbo.HairCut_Item_Bill_Master_Primary(UniqueBillID uniqueidentifier PRIMARY KEY, SeatBookingDetailsID uniqueidentifier, CreatedBy uniqueidentifier, CreatedDate datetime DEFAULT GETDATE());
        CREATE TABLE dbo.HairCut_Item_Customer_Bill_Primary(UniqueBillID uniqueidentifier, Created_By uniqueidentifier);
        """);
    await db.ExecuteAsync("""
        CREATE PROCEDURE dbo.insertCustomerTimeSelection @TimeSelectionID uniqueidentifier,@SeatBookingDetailsID uniqueidentifier,@CustomerID uniqueidentifier,@TimeAllocationID uniqueidentifier
        AS INSERT dbo.CustomerTimeSelection(TimeSelectionID,SeatBookingDetailsID,CustomerID,TimeAllocationID) VALUES(@TimeSelectionID,@SeatBookingDetailsID,@CustomerID,@TimeAllocationID);
        """);
    await db.ExecuteAsync("""
        CREATE PROCEDURE dbo.SelectInsertPrimaryToConstant @UniqueBillID uniqueidentifier AS
        INSERT dbo.HairCut_Item_Bill_Master(UniqueBillID,SeatBookingDetailsID,CreatedBy) SELECT UniqueBillID,SeatBookingDetailsID,CreatedBy FROM dbo.HairCut_Item_Bill_Master_Primary WHERE UniqueBillID=@UniqueBillID;
        """);
    await db.ExecuteAsync("""
        CREATE PROCEDURE dbo.updateBookingStatus @SeatBookingDetailsID uniqueidentifier,@CustomerID uniqueidentifier,@BookingStatusMasterID int AS
        UPDATE dbo.SeatBookingDetails SET BookingStatusMasterID=@BookingStatusMasterID WHERE SeatBookingDetailsID=@SeatBookingDetailsID AND CustomerID=@CustomerID;
        """);
    await db.ExecuteAsync("CREATE PROCEDURE dbo.deleteClientSideCartItemsByBill @UniqueBillID uniqueidentifier,@CustomerID uniqueidentifier AS DELETE dbo.HairCut_Item_Customer_Bill_Primary WHERE UniqueBillID=@UniqueBillID AND Created_By=@CustomerID;");
    await db.ExecuteAsync("CREATE PROCEDURE dbo.getSeatTimeAllocation AS SELECT * FROM dbo.TimeAllocation WHERE ISNULL(IsDeleted,0)=0 ORDER BY FullTiming;");
    await db.ExecuteAsync("CREATE PROCEDURE dbo.checkcustomerIDSeatBookingDetails @CustomerID uniqueidentifier AS SELECT TOP(1) * FROM dbo.SeatBookingDetails WHERE CustomerID=@CustomerID ORDER BY CreatedOn DESC,SeatBookingDetailsID;");
    var salon = Guid.NewGuid(); var otherSalon = Guid.NewGuid(); var owner = Guid.NewGuid(); var otherOwner = Guid.NewGuid();
    var morning = Guid.NewGuid(); var afternoon = Guid.NewGuid(); var unselected = Guid.NewGuid(); var disabled = Guid.NewGuid();
    await db.ExecuteAsync("""
        INSERT dbo.SalonMaster VALUES(@salon,1),(@otherSalon,1);
        INSERT dbo.SalonOwner VALUES(@owner,@salon,1),(@otherOwner,@otherSalon,1);
        INSERT dbo.TimeAllocation VALUES(@morning,'09:00',0,1),(@afternoon,'15:00',0,1),(@unselected,'12:00',0,1),(@disabled,'21:00',0,0);
        """, new { salon, otherSalon, owner, otherOwner, morning, afternoon, unselected, disabled });
    var migration = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "SalonTimingAvailability.sql"));
    await db.ExecuteAsync(migration); await db.ExecuteAsync(migration);
    var legacySettings = new System.Data.SqlClient.SqlConnectionStringBuilder {
        DataSource = settings.DataSource, InitialCatalog = name, UserID = settings.UserID, Password = settings.Password,
        IntegratedSecurity = settings.IntegratedSecurity, Encrypt = false, TrustServerCertificate = true
    };
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:dbconnection"] = legacySettings.ConnectionString,
        ["Authentication:KeyRingPath"] = Path.Combine(AppContext.BaseDirectory, "fixture-keys")
    }).Build();
    var timings = new SalonTimingAvailability(config); var reservations = new CustomerReservationStore(config);
    var checks = 0;
    void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
    var schedule = await timings.GetAsync(owner, salon, default);
    Check(schedule.Version == 0 && schedule.Slots.Count == 3 && schedule.Slots.All(t => !t.IsAvailable), "An unconfigured salon has no bookable times and cannot enable globally disabled slots");
    schedule = await timings.SaveAsync(owner, salon, 0, [morning, afternoon], default);
    Check(schedule.Version == 1 && schedule.Slots.Count(t => t.IsAvailable) == 2, "The admin's daily selection is persisted with a new version");
    Check((await timings.GetAsync(otherOwner, otherSalon, default)).Slots.All(t => !t.IsAvailable), "One salon's selected timings do not enable another salon");
    try { await timings.GetAsync(otherOwner, salon, default); throw new Exception("Expected isolation rejection"); }
    catch (UnauthorizedAccessException) { Check(true, "An owner cannot read another salon's schedule"); }
    try { await timings.SaveAsync(owner, salon, 0, [morning], default); throw new Exception("Expected stale version rejection"); }
    catch (SalonTimingConflict) { Check((await timings.GetAsync(owner, salon, default)).Slots.Count(t => t.IsAvailable) == 2, "A stale admin cannot overwrite newer timing choices"); }
    try { await timings.SaveAsync(owner, salon, 1, [disabled], default); throw new Exception("Expected disabled-slot rejection"); }
    catch (SalonTimingConflict) { Check(true, "A global unavailable timing cannot be made bookable by a salon"); }
    try { await timings.SaveAsync(owner, salon, 1, [morning, morning], default); throw new Exception("Expected duplicate-slot rejection"); }
    catch (ArgumentException) { Check(true, "Duplicate or invalid slot IDs cannot be saved"); }
    var date = CustomerBookingRules.Today.AddDays(1);
    if (date.DayOfWeek == DayOfWeek.Friday) date = date.AddDays(1);
    async Task<Guid> Draft(Guid customerId, Guid salonId) {
        var id = Guid.NewGuid();
        await db.ExecuteAsync("INSERT dbo.SeatBookingDetails(SeatBookingDetailsID,CustomerID,SalonMasterID,BookingDate,SeatCount,BookingStatusMasterID) VALUES(@id,@customerId,@salonId,@date,1,1)", new { id, customerId, salonId, date });
        return id;
    }
    insertCustomerTimeSelection Selection(Guid id, Guid salonId, Guid timeId, long version = 0) => new() {
        SeatBookingDetailsID = id, SalonMasterID = salonId, TimeAllocationID = timeId, ExpectedVersion = version, BookingDate = date.ToString("yyyy-MM-dd")
    };
    ReviewTimeSelectionRequest Review(ReservationDraft draft) => new() { SeatBookingDetailsID = draft.SeatBookingDetailsID,
        Version = draft.Version, BookingDate = draft.BookingDate, TimeAllocationIDs = draft.Selections.Select(t => t.Id).ToArray() };
    var customer = Guid.NewGuid(); var booking = await Draft(customer, salon);
    var rejected = reservations.Change(customer, Selection(booking, salon, unselected), false);
    Check(rejected.Item1 == "Invalid Time" && rejected.Draft!.Selections.Count == 0, "A direct customer request cannot reserve an unselected timing");
    var held = reservations.Change(customer, Selection(booking, salon, morning), false);
    Check(held.Item1 == "Success" && held.Draft!.Selections.Single().Id == morning, "Customers can reserve an admin-selected time");
    var request = Review(held.Draft!);
    Check(reservations.Review(customer, request).Item1 == "Success", "An enabled selection can pass the booking review");
    schedule = await timings.SaveAsync(owner, salon, 1, [afternoon], default);
    Check(reservations.Review(customer, request).Item1 == "Time Slot Unavailable", "A time disabled during booking is rejected at review");
    var pendingBill = Guid.NewGuid();
    await db.ExecuteAsync("INSERT dbo.HairCut_Item_Bill_Master_Primary(UniqueBillID,SeatBookingDetailsID,CreatedBy) VALUES(@pendingBill,@booking,@customer); INSERT dbo.HairCut_Item_Customer_Bill_Primary VALUES(@pendingBill,@customer);", new { pendingBill, booking, customer });
    Check(reservations.Checkout(customer, pendingBill, request).Item1 == "Time Slot Unavailable"
        && await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_Bill_Master") == 0,
        "Checkout revalidates timing availability before billing or confirming a held seat");
    Check(reservations.Change(customer, Selection(booking, salon, morning, held.Draft!.Version), true).Item1 == "Success", "Customers can remove an old held selection after it is disabled");
    var otherCustomer = Guid.NewGuid(); var otherBooking = await Draft(otherCustomer, otherSalon);
    Check(reservations.Change(otherCustomer, Selection(otherBooking, otherSalon, afternoon), false).Item1 == "Invalid Time", "A timing enabled for another salon cannot be reserved");
    await timings.SaveAsync(otherOwner, otherSalon, 0, [unselected], default);
    var completingCustomer = Guid.NewGuid(); var completingBooking = await Draft(completingCustomer, salon);
    var completing = reservations.Change(completingCustomer, Selection(completingBooking, salon, afternoon), false);
    var completingRequest = Review(completing.Draft!); var bill = Guid.NewGuid();
    await db.ExecuteAsync("INSERT dbo.HairCut_Item_Bill_Master_Primary(UniqueBillID,SeatBookingDetailsID,CreatedBy) VALUES(@bill,@completingBooking,@completingCustomer); INSERT dbo.HairCut_Item_Customer_Bill_Primary VALUES(@bill,@completingCustomer);", new { bill, completingBooking, completingCustomer });
    Check(reservations.Checkout(completingCustomer, bill, completingRequest).Item1 == "Success", "An available timing completes the unchanged checkout flow");
    schedule = await timings.SaveAsync(owner, salon, schedule.Version, [], default);
    Check(schedule.Slots.All(t => !t.IsAvailable) && schedule.Version == 3, "Saving an empty daily selection closes new bookings");
    Check(reservations.GetDraft(completingCustomer)!.Confirmed
        && reservations.Checkout(completingCustomer, bill, completingRequest).Item1 == "Success",
        "Changing availability preserves confirmed appointments and idempotent checkout retries");
    schedule = await timings.SaveAsync(owner, salon, schedule.Version, [morning], default);
    var context = new DefaultHttpContext { Session = new FixtureSession() }; context.Session.SetString("CustomerID", customer.ToString());
    var legacy = new AppoinmentService(new HttpContextAccessor { HttpContext = context }, config);
    var list = legacy.getSeatTimeAllocation(date, salon);
    Check(list.Item1 == "Success" && list.Item2.Count == 1 && (Guid)list.Item2[0]["TimeAllocationID"] == morning,
        "The customer availability response lists only the chosen salon's selected timings");
    var otherList = legacy.getSeatTimeAllocation(date, otherSalon);
    Check(otherList.Item2.Count == 1 && (Guid)otherList.Item2[0]["TimeAllocationID"] == unselected,
        "Switching salons changes the returned bookable timing list");
    var noSalon = legacy.getSeatTimeAllocation(date);
    Check(noSalon.Item2.Count == 1 && (Guid)noSalon.Item2[0]["TimeAllocationID"] == morning, "Older clients can use their current draft's salon as the availability scope");
    await db.ExecuteAsync(migration);
    Check((await timings.GetAsync(owner, salon, default)).Slots.Single(t => t.IsAvailable).TimeAllocationID == morning,
        "Rerunning the SQL update preserves saved daily timings");

    var owners = new FixtureOwners(owner, salon);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Configuration.AddConfiguration(config);
    builder.Services.AddDistributedMemoryCache(); builder.Services.AddSession(); builder.Services.AddSingleton<ISalonOwnerAuthenticationService>(owners);
    builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration, builder.Environment);
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider(); builder.Services.AddSingleton(timings);
    builder.Services.AddControllers().AddApplicationPart(typeof(SalonTimingAvailabilityController).Assembly);
    app = builder.Build(); app.Use(async (ctx, next) => { ctx.Request.Scheme = "https"; await next(ctx); });
    app.UseSession(); app.UseMiddleware<SalonOwnerSessionMiddleware>(); app.MapControllers();
    app.MapPost("/fixture/login", async (HttpContext ctx, SalonOwnerPersistentSignIn signIn) => { await signIn.SignInAsync(ctx, owners.Owner); return Results.NoContent(); });
    await app.StartAsync();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    var cookies = new CookieContainer(); var origin = new Uri("https://localhost");
    async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, bool header = true) {
        using var message = new HttpRequestMessage(method, path); message.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(origin));
        if (header) message.Headers.Add("X-Salonexa-Request", "1"); if (body is not null) message.Content = JsonContent.Create(body);
        var response = await client.SendAsync(message);
        if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values) cookies.SetCookies(origin, value);
        return response;
    }
    const string route = "/api/Admin/availability";
    using (var response = await Send(HttpMethod.Get, route)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous users cannot manage salon timings");
    using (var response = await Send(HttpMethod.Post, "/fixture/login")) Check(response.IsSuccessStatusCode, "The fixture owner can sign in with a protected owner ticket");
    using (var response = await Send(HttpMethod.Get, route)) Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true, "Authenticated timing reads are protected from caching");
    using (var response = await Send(HttpMethod.Put, route, new { version = schedule.Version, timeAllocationIDs = new[] { morning } }, false)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Saving timings requires the application's CSRF header");
    using (var response = await Send(HttpMethod.Put, route, new { timeAllocationIDs = new[] { morning } })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Timing updates require the previously loaded version");
    using (var response = await Send(HttpMethod.Put, route, new { version = 0, timeAllocationIDs = new[] { morning } })) Check(response.StatusCode == HttpStatusCode.Conflict, "The API rejects stale timing updates");
    using (var response = await Send(HttpMethod.Put, route, new { version = schedule.Version, timeAllocationIDs = new[] { afternoon }, salonMasterID = otherSalon })) {
        var saved = await response.Content.ReadFromJsonAsync<SalonTimingSchedule>();
        Check(response.IsSuccessStatusCode && saved!.SalonMasterID == salon, "Browser-supplied salon IDs cannot change another salon's timing availability");
    }
    Check((await timings.GetAsync(otherOwner, otherSalon, default)).Slots.Single(t => t.IsAvailable).TimeAllocationID == unselected, "Other salon schedules remain unchanged");
    Console.WriteLine($"Passed {checks} timing availability SQL, customer reservation and HTTP checks. Only the disposable fixture database was changed.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    await db.CloseAsync(); SqlConnection.ClearPool(db);
    await master.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
}
sealed class FixtureSession : ISession
{
    private readonly Dictionary<string, byte[]> values = new();
    public bool IsAvailable => true; public string Id => "fixture-session"; public IEnumerable<string> Keys => values.Keys;
    public void Clear() => values.Clear(); public void Remove(string key) => values.Remove(key); public void Set(string key, byte[] value) => values[key] = value;
    public bool TryGetValue(string key, out byte[] value) => values.TryGetValue(key, out value!);
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask; public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
sealed class FixtureOwners(Guid owner, Guid salon) : ISalonOwnerAuthenticationService
{
    public SalonOwnerLoginResponse Owner { get; } = new() { SalonOwnerID = owner, SalonMasterID = salon, OwnerName = "Timing fixture", UserName = "fixture-owner", SalonName = "Fixture salon", AuthenticationStamp = "fixture-password-version" };
    public Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(id == owner ? Owner : null);
    public Task<SalonOwnerLoginResponse?> AuthenticateAsync(SalonOwnerLoginRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SalonOwnerLoginResponse> InitializeAsync(InitializeSalonOwnerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
