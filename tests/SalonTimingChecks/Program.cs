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
    Check(schedule.Version == 0 && schedule.Slots.Count == 4 && schedule.Slots.All(t => !t.IsAvailable)
        && schedule.Slots.Count(t => !t.IsEnabledByPlatform) == 1 && !schedule.Slots.Single(t => t.TimeAllocationID == disabled).IsEnabledByPlatform,
        "An unconfigured salon lists every time, has none bookable, and shows globally disabled slots as locked");
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
    Check((string)list.Item5[0]["FullDate"] == date.ToString("yyyy-MM-dd"),
        "A requested booking date is the first listed date, matching the returned times");
    var firstOpen = legacy.getSeatTimeAllocation(null, salon);
    var firstDay = DateTime.ParseExact((string)firstOpen.Item5[0]["FullDate"], "yyyy-MM-dd", null);
    var offered = firstOpen.Item5.Select(d => DateTime.ParseExact((string)d["FullDate"], "yyyy-MM-dd", null)).ToList();
    Check(offered.Count == CustomerBookingRules.ListedDates && offered[0] == firstDay
        && offered.All(d => d.DayOfWeek != DayOfWeek.Friday && CustomerBookingRules.IsAvailableDate(d, CustomerBookingRules.Today))
        && offered.SequenceEqual(offered.OrderBy(d => d)) && offered.Distinct().Count() == offered.Count,
        "Seven bookable dates are offered in order, none on a Friday, all inside the booking window");
    var fullBooking = Guid.NewGuid(); var fullCustomer = Guid.NewGuid();
    await db.ExecuteAsync("""
        INSERT dbo.SeatBookingDetails(SeatBookingDetailsID,CustomerID,SalonMasterID,BookingDate,SeatCount,BookingStatusMasterID) VALUES(@fullBooking,@fullCustomer,@salon,@firstDay,1,2);
        INSERT dbo.CustomerTimeSelection(TimeSelectionID,CustomerID,SeatBookingDetailsID,TimeAllocationID) VALUES(NEWID(),@fullCustomer,@fullBooking,@morning);
        """, new { fullBooking, fullCustomer, salon, firstDay, morning });
    var nextOpen = legacy.getSeatTimeAllocation(null, salon);
    var nextDay = DateTime.ParseExact((string)nextOpen.Item5[0]["FullDate"], "yyyy-MM-dd", null);
    Check(nextDay > firstDay && nextDay.DayOfWeek != DayOfWeek.Friday
        && nextOpen.Item2.Any(t => (Guid)t["TimeAllocationID"] == morning && (string)t["EnableDisable"] == "Enable"),
        "When every open time on the first day is booked, the next day with a free time is offered");
    await db.ExecuteAsync("DELETE dbo.CustomerTimeSelection WHERE SeatBookingDetailsID=@fullBooking; DELETE dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@fullBooking;", new { fullBooking });
    var otherList = legacy.getSeatTimeAllocation(date, otherSalon);
    Check(otherList.Item2.Count == 1 && (Guid)otherList.Item2[0]["TimeAllocationID"] == unselected,
        "Switching salons changes the returned bookable timing list");
    var noSalon = legacy.getSeatTimeAllocation(date);
    Check(noSalon.Item2.Count == 1 && (Guid)noSalon.Item2[0]["TimeAllocationID"] == morning, "Older clients can use their current draft's salon as the availability scope");
    await db.ExecuteAsync(migration);
    Check((await timings.GetAsync(owner, salon, default)).Slots.Single(t => t.IsAvailable).TimeAllocationID == morning,
        "Rerunning the SQL update preserves saved daily timings");

    // ----- Per-date timings: a date can have its own times (or be closed); other dates use the daily times. -----
    var dateSalon = Guid.NewGuid(); var dateOwner = Guid.NewGuid();
    await db.ExecuteAsync("INSERT dbo.SalonMaster VALUES(@dateSalon,1); INSERT dbo.SalonOwner VALUES(@dateOwner,@dateSalon,1);", new { dateSalon, dateOwner });
    var ownDay = date;                  // gets its own times
    var closedDay = date.AddDays(1);    // closed all day
    var plainDay = date.AddDays(2);     // follows the daily times
    var dated = await timings.SaveAsync(dateOwner, dateSalon, 0, [morning, afternoon], default);
    dated = await timings.SaveAsync(dateOwner, dateSalon, dated.Version, [unselected], default, ownDay);
    Check(dated.IsCustomised && dated.Date == ownDay.ToString("yyyy-MM-dd") && dated.Slots.Single(t => t.IsAvailable).TimeAllocationID == unselected,
        "A date can be given its own times, even one the daily schedule does not include");
    var plain = await timings.GetAsync(dateOwner, dateSalon, default, plainDay);
    Check(!plain.IsCustomised && plain.Slots.Count(t => t.IsAvailable) == 2, "A date without its own times follows the daily schedule");
    Check((await timings.GetAsync(dateOwner, dateSalon, default)).Slots.Count(t => t.IsAvailable) == 2, "Customising a date leaves the daily schedule unchanged");
    dated = await timings.SaveAsync(dateOwner, dateSalon, dated.Version, [], default, closedDay);
    Check(dated.IsCustomised && dated.Slots.All(t => !t.IsAvailable), "Saving no times for a date closes that date only");
    try { await timings.SaveAsync(dateOwner, dateSalon, dated.Version, [morning], default, CustomerBookingRules.Today.AddDays(-1)); throw new Exception("Expected past-date rejection"); }
    catch (ArgumentException) { Check(true, "Times cannot be set for a past date"); }
    try { await timings.SaveAsync(dateOwner, dateSalon, dated.Version, [morning], default, CustomerBookingRules.Today.AddDays(SalonTimingAvailability.MaxDaysAhead + 1)); throw new Exception("Expected far-date rejection"); }
    catch (ArgumentException) { Check(true, "Times cannot be set too far ahead"); }
    try { await timings.SaveAsync(dateOwner, dateSalon, 0, [morning], default, plainDay); throw new Exception("Expected stale rejection"); }
    catch (SalonTimingConflict) { Check(true, "A stale admin cannot overwrite a date's timing choices"); }
    try { await timings.SaveAsync(dateOwner, dateSalon, dated.Version, [disabled], default, plainDay); throw new Exception("Expected disabled rejection"); }
    catch (SalonTimingConflict) { Check(true, "A globally disabled timing cannot be added to a date"); }
    var dateCustomer = Guid.NewGuid(); var dateBooking = await Draft(dateCustomer, dateSalon);
    Check(reservations.Change(dateCustomer, Selection(dateBooking, dateSalon, morning), false).Item1 == "Invalid Time",
        "A daily time that the booking date's own times leave out cannot be reserved");
    Check(reservations.Change(dateCustomer, Selection(dateBooking, dateSalon, unselected), false).Item1 == "Success",
        "A time in the booking date's own times can be reserved");
    var ownList = legacy.getSeatTimeAllocation(ownDay, dateSalon);
    Check(ownList.Item2.Count == 1 && (Guid)ownList.Item2[0]["TimeAllocationID"] == unselected, "The customer list shows a date's own times");
    Check(legacy.getSeatTimeAllocation(closedDay, dateSalon).Item2.Count == 0, "The customer list is empty for a closed date");
    Check(legacy.getSeatTimeAllocation(plainDay, dateSalon).Item2.Count == 2, "The customer list shows the daily times for other dates");
    dated = await timings.ResetDateAsync(dateOwner, dateSalon, dated.Version, ownDay, default);
    Check(!dated.IsCustomised && dated.Slots.Count(t => t.IsAvailable) == 2
        && await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.SalonBookableTimeDate WHERE SalonMasterID=@dateSalon AND BookingDate=@ownDay", new { dateSalon, ownDay }) == 0,
        "Resetting a date returns it to the daily schedule and removes its own times");

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
    var pastDay = CustomerBookingRules.Today.AddDays(-1).ToString("yyyy-MM-dd");
    using (var response = await Send(HttpMethod.Get, route + "?date=" + ownDay.ToString("yyyy-MM-dd")))
        Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("\"isCustomised\":false"), "A single date's timings can be read over HTTP");
    using (var response = await Send(HttpMethod.Get, route + "?date=not-a-date")) Check(response.StatusCode == HttpStatusCode.BadRequest, "A malformed date is rejected");
    using (var response = await Send(HttpMethod.Get, route + "?date=" + pastDay)) Check(response.StatusCode == HttpStatusCode.BadRequest, "A past date is rejected when reading");
    using (var response = await Send(HttpMethod.Put, route, new { version = 0, timeAllocationIDs = new[] { morning }, date = pastDay })) Check(response.StatusCode == HttpStatusCode.BadRequest, "A past date is rejected when saving");
    using (var response = await Send(HttpMethod.Delete, route + "?version=0&date=" + ownDay.ToString("yyyy-MM-dd"), null, false)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Resetting a date requires the application's CSRF header");
    using (var response = await Send(HttpMethod.Delete, route + "?version=0")) Check(response.StatusCode == HttpStatusCode.BadRequest, "Resetting needs a date");
    using (var response = await Send(HttpMethod.Put, route, new { version = schedule.Version, timeAllocationIDs = new[] { morning } }, false)) Check(response.StatusCode == HttpStatusCode.Forbidden, "Saving timings requires the application's CSRF header");
    using (var response = await Send(HttpMethod.Put, route, new { timeAllocationIDs = new[] { morning } })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Timing updates require the previously loaded version");
    using (var response = await Send(HttpMethod.Put, route, new { version = 0, timeAllocationIDs = new[] { morning } })) Check(response.StatusCode == HttpStatusCode.Conflict, "The API rejects stale timing updates");
    using (var response = await Send(HttpMethod.Put, route, new { version = schedule.Version, timeAllocationIDs = new[] { afternoon }, salonMasterID = otherSalon })) {
        var saved = await response.Content.ReadFromJsonAsync<SalonTimingSchedule>();
        Check(response.IsSuccessStatusCode && saved!.SalonMasterID == salon, "Browser-supplied salon IDs cannot change another salon's timing availability");
    }
    Check((await timings.GetAsync(otherOwner, otherSalon, default)).Slots.Single(t => t.IsAvailable).TimeAllocationID == unselected, "Other salon schedules remain unchanged");

    // ----- The Timings screen's auto-save, end to end: real HTTP route + real SQL, then read the table directly. -----
    var fullDay = new List<(Guid Id, TimeSpan Time)>();
    for (var minutes = 8 * 60; minutes <= 22 * 60; minutes += 30) fullDay.Add((Guid.NewGuid(), TimeSpan.FromMinutes(minutes)));
    foreach (var (id, time) in fullDay)
        await db.ExecuteAsync("INSERT dbo.TimeAllocation VALUES(@id,@time,0,1)", new { id, time });
    var dayIds = fullDay.Select(t => t.Id).ToHashSet();
    var nine = fullDay.Single(t => t.Time == new TimeSpan(21, 0, 0)).Id;      // 9:00 PM
    var nineThirty = fullDay.Single(t => t.Time == new TimeSpan(21, 30, 0)).Id; // 9:30 PM
    var tenPm = fullDay.Single(t => t.Time == new TimeSpan(22, 0, 0)).Id;      // 10:00 PM
    async Task<HashSet<Guid>> StoredTimes() =>
        (await db.QueryAsync<Guid>("SELECT TimeAllocationID FROM dbo.SalonBookableTime WHERE SalonMasterID=@salon", new { salon })).Where(dayIds.Contains).ToHashSet();
    async Task<long> LiveVersion() {
        using var read = await Send(HttpMethod.Get, route);
        return (await read.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("version").GetInt64();
    }
    async Task<HttpResponseMessage> Autosave(IEnumerable<Guid> ids) =>
        await Send(HttpMethod.Put, route, new { version = await LiveVersion(), timeAllocationIDs = ids.ToArray() });
    using (var response = await Autosave(dayIds))
        Check(response.IsSuccessStatusCode && (await StoredTimes()).SetEquals(dayIds), "Ticking every time of the day over HTTP stores all of them in the database");
    using (var response = await Autosave(dayIds.Where(id => id != nine && id != nineThirty)))
    {
        var after = await StoredTimes();
        Check(response.IsSuccessStatusCode && after.Count == dayIds.Count - 2 && !after.Contains(nine) && !after.Contains(nineThirty) && after.Contains(tenPm),
            "Unticking 9:00 PM and 9:30 PM over HTTP removes exactly those two rows and keeps 10:00 PM");
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var shown = json.GetProperty("slots").EnumerateArray().Where(s => dayIds.Contains(s.GetProperty("timeAllocationID").GetGuid()))
            .ToDictionary(s => s.GetProperty("timeAllocationID").GetGuid(), s => s.GetProperty("isAvailable").GetBoolean());
        Check(!shown[nine] && !shown[nineThirty] && shown[tenPm], "The reply the screen adopts matches what was stored");
    }
    // Several auto-saves in a row (one per tap), each with the version returned by the one before.
    var current = dayIds.Where(id => id != nine && id != nineThirty).ToHashSet();
    foreach (var id in new[] { tenPm, fullDay[0].Id, nine })                      // untick 10 PM, untick 8 AM, tick 9 PM back
    {
        if (!current.Remove(id)) current.Add(id);
        using var response = await Autosave(current);
        Check(response.IsSuccessStatusCode && (await StoredTimes()).SetEquals(current), $"Auto-save sequence step keeps the database equal to the screen ({current.Count} times)");
    }
    using (var response = await Send(HttpMethod.Get, route))
    {
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var onScreen = json.GetProperty("slots").EnumerateArray()
            .Where(s => dayIds.Contains(s.GetProperty("timeAllocationID").GetGuid()) && s.GetProperty("isAvailable").GetBoolean())
            .Select(s => s.GetProperty("timeAllocationID").GetGuid()).ToHashSet();
        Check(onScreen.SetEquals(await StoredTimes()) && onScreen.SetEquals(current), "A fresh read after the auto-saves shows exactly the times that are in the table");
    }
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
