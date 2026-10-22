using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using static InfinityHairartsAPI.Modals.AppoinmentModal;

// Only explicitly guarded localhost fixtures are changed. No hosted notification/SMS services run.
var configurationPath = args.Single();
var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(configurationPath)).Build();
var connectionString = config.GetConnectionString("dbconnection")!;
var guard = new SqlConnectionStringBuilder(connectionString);
if (!new[] { "localhost", ".", "(local)" }.Contains(guard.DataSource.ToLowerInvariant())
    || guard.InitialCatalog != "InfinityHairArtsAdmin")
    throw new InvalidOperationException("Only the named local test database is allowed.");

using var db = new SqlConnection(connectionString);
db.Open();
var customer = Guid.NewGuid();
var other = Guid.NewGuid();
var salon = Guid.NewGuid();
var location = Guid.NewGuid();
var slot = Guid.NewGuid();
var otherBooking = Guid.NewGuid();
var bill = Guid.NewGuid();
var date = CustomerBookingRules.Today.AddDays(1);
while (!CustomerBookingRules.IsAvailableDate(date, CustomerBookingRules.Today)) date = date.AddDays(1);
string mobile;
do { mobile = "999" + RandomNumberGenerator.GetInt32(1000000, 9999999); }
while (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.CustomerRegistration WHERE MobileNo=@mobile", new { mobile }) > 0);
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS: " + description);
    checks++;
}

WebApplication? app = null;
try
{
    db.Execute("""
        INSERT dbo.CustomerRegistration(CustomerID,MobileNo,FirstName,CreatedOn,CreatedBy,IsDeleted)
        VALUES(@customer,@mobile,'Booking compatibility fixture',GETDATE(),@customer,0);
        INSERT dbo.CustomerRegistration(CustomerID,FirstName,CreatedOn,CreatedBy,IsDeleted)
        VALUES(@other,'Booking conflict fixture',GETDATE(),@other,0);
        INSERT dbo.LocationMaster(LocationMasterID,LocationName,IsActive,DisplayOrder,CreatedUtc)
        VALUES(@location,'Booking compatibility fixture',1,9999,SYSUTCDATETIME());
        INSERT dbo.SalonMaster(SalonMasterID,LocationMasterID,SalonName,IsActive,DisplayOrder,CreatedUtc,ChairCount,IsVisibleToCustomers)
        VALUES(@salon,@location,'Booking compatibility fixture',1,9999,SYSUTCDATETIME(),1,1);
        INSERT dbo.TimeAllocation(TimeAllocationID,FullTiming,IsDeleted,CreatedDate,BookingAvailableStatus)
        VALUES(@slot,'19:00:00',0,GETDATE(),1);
        """, new { customer, mobile, other, salon, location, slot });

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:dbconnection"] = connectionString });
    builder.Services.AddControllers().AddApplicationPart(typeof(LoginController).Assembly);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<LoginService>();
    builder.Services.AddScoped<LocationService>();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<CustomerSessionRegistry>();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddSession(options => options.Cookie.Name = ".BookingCompatibilityChecks");
    app = builder.Build();
    app.UseRouting();
    app.UseSession();
    app.UseMiddleware<CustomerApiSessionMiddleware>();
    app.MapControllers();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
    using var http = new HttpClient(handler) { BaseAddress = new Uri(address) };
    http.DefaultRequestHeaders.Add("X-Salonexa-Request", "1");
    async Task<(HttpStatusCode Status, JsonElement Body)> Get(string path)
    {
        using var response = await http.GetAsync(path);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()));
    }
    async Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()));
    }
    var health = await Get("/api/Login/health");
    Check(health.Status == HttpStatusCode.OK && health.Body.GetProperty("apiContract").GetString() == "mobile-login-reservations-v1", "Deployment marker identifies the matching API");
    Check((await Get("/api/Login/session")).Status == HttpStatusCode.Unauthorized, "Session route exists and rejects anonymous access");
    Check((await Get("/api/Appoinment/time-selection")).Status == HttpStatusCode.Unauthorized, "Saved-time route exists and rejects anonymous access");
    Check((await Post("/api/Login/customerLoginButtomClick", new { MobileNo = "123", Address = "" })).Status == HttpStatusCode.BadRequest, "Invalid mobile number cannot create a session");
    using (var plain = new HttpClient { BaseAddress = new Uri(address) })
    using (var response = await plain.PostAsJsonAsync("/api/Login/customerLoginButtomClick", new { MobileNo = mobile }))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Customer mutations require the application request header");
    var login = await Post("/api/Login/customerLoginButtomClick", new { MobileNo = mobile, Address = "Local fixture" });
    Check(login.Status == HttpStatusCode.OK && login.Body.GetProperty("item1").GetString() == "Success", "Phone-number-only login succeeds against SQL without OTP");
    Check((await Get("/api/Login/session")).Status == HttpStatusCode.OK, "Login creates a restorable authenticated customer session");
    Check((await Post("/api/Login/customerLoginButtomClick", new { MobileNo = mobile })).Status == HttpStatusCode.OK,
        "Remembered-number sign-in restores the session without asking for an address");
    Check(db.ExecuteScalar<string>("SELECT GeneralAddress FROM dbo.CustomerRegistration WHERE CustomerID=@customer", new { customer }) == "Local fixture",
        "Automatic sign-in preserves the saved customer address");
    var empty = await Get("/api/Appoinment/time-selection");
    Check(empty.Status == HttpStatusCode.OK && empty.Body.GetProperty("draft").ValueKind == JsonValueKind.Null, "No saved booking is represented by an explicit null draft");
    Check((await Post("/api/Appoinment/insertupdateSeatBookingDetails", new { SeatCount = 1, BookingDate = date.ToString("yyyy-MM-dd") })).Status == HttpStatusCode.BadRequest, "Preparing a booking requires a salon");
    var prepare = await Post("/api/Appoinment/insertupdateSeatBookingDetails", new { SeatCount = 1, BookingDate = date.ToString("yyyy-MM-dd"), SalonMasterID = salon });
    Check(prepare.Status == HttpStatusCode.OK && prepare.Body.GetProperty("item1").GetString() == "Success", "Booking preparation succeeds using this project's SQL procedures");
    var store = new CustomerReservationStore(config);
    var draft = store.GetDraft(customer)!;
    Check(draft != null && draft.SalonMasterID == salon && draft.Selections.Count == 0, "Booking draft records the selected salon and version");
    Check(prepare.Body.GetProperty("draft").GetProperty("seatBookingDetailsID").GetGuid() == draft!.SeatBookingDetailsID
        && prepare.Body.GetProperty("draft").GetProperty("version").GetInt64() == draft.Version,
        "Preparation returns the authoritative draft so the first tap skips an availability round trip");
    var availability = await Post("/api/Appoinment/getSeatTimeAllocation?bookingDate=" + date.ToString("yyyy-MM-dd"), new { });
    Check(availability.Status == HttpStatusCode.OK && availability.Body.GetProperty("item1").GetString() == "Success"
        && availability.Body.GetProperty("draft").GetProperty("seatBookingDetailsID").GetGuid() == draft!.SeatBookingDetailsID,
        "Appointment times include the authoritative booking draft required by app 1.2.21");
    Check(availability.Body.GetProperty("item2").EnumerateArray().Any(row => row.GetProperty("TimeAllocationID").GetGuid() == slot
        && row.GetProperty("EnableDisable").GetString() == "Enable"), "Requested future day exposes an available fixture time");
    Check((await Post("/api/Appoinment/getSeatTimeAllocation?bookingDate=" + date.AddDays(-20).ToString("yyyy-MM-dd"), new { })).Status == HttpStatusCode.BadRequest,
        "Expired booking dates are rejected");

    if (CustomerBookingRules.IsAvailableDate(CustomerBookingRules.Today, CustomerBookingRules.Today))
    {
        db.Execute("UPDATE dbo.SeatBookingDetails SET BookingDate=@today WHERE SeatBookingDetailsID=@id; UPDATE dbo.TimeAllocation SET FullTiming='00:00:00' WHERE TimeAllocationID=@slot;",
            new { today = CustomerBookingRules.Today, id = draft!.SeatBookingDetailsID, slot });
        var cutoffDraft = store.GetDraft(customer)!;
        var expired = new insertCustomerTimeSelection { TimeAllocationID = slot, SeatBookingDetailsID = cutoffDraft.SeatBookingDetailsID,
            ExpectedVersion = cutoffDraft.Version, BookingDate = cutoffDraft.BookingDate, SalonMasterID = cutoffDraft.SalonMasterID };
        var cutoff = await Post("/api/Appoinment/insertCustomerTimeSelection", expired);
        Check(cutoff.Body.GetProperty("item1").GetString() == "Invalid Time"
            && store.GetDraft(customer)!.Selections.Count == 0, "Saving rechecks the time cutoff without relying on a preceding availability read");
        var tooSoon = CustomerBookingRules.Now.TimeOfDay.Add(TimeSpan.FromMinutes(30));
        if (tooSoon < TimeSpan.FromDays(1))
        {
            db.Execute("UPDATE dbo.TimeAllocation SET FullTiming=@tooSoon WHERE TimeAllocationID=@slot;", new { tooSoon, slot });
            expired.ExpectedVersion = store.GetDraft(customer)!.Version;
            Check((await Post("/api/Appoinment/insertCustomerTimeSelection", expired)).Body.GetProperty("item1").GetString() == "Invalid Time",
                "Saving preserves the 90-minute advance-booking cutoff even when displayed availability has aged");
        }
        db.Execute("UPDATE dbo.SeatBookingDetails SET BookingDate=@date WHERE SeatBookingDetailsID=@id; UPDATE dbo.TimeAllocation SET FullTiming='19:00:00' WHERE TimeAllocationID=@slot;",
            new { date, id = draft.SeatBookingDetailsID, slot });
        draft = store.GetDraft(customer)!;
    }
    var change = new insertCustomerTimeSelection { TimeAllocationID = slot, SeatBookingDetailsID = draft!.SeatBookingDetailsID,
        ExpectedVersion = draft.Version, BookingDate = draft.BookingDate, SalonMasterID = salon };
    var selected = await Post("/api/Appoinment/insertCustomerTimeSelection", change);
    Check(selected.Status == HttpStatusCode.OK && selected.Body.GetProperty("item1").GetString() == "Success", "A time selection is saved through the app endpoint");
    var selectedDraft = store.GetDraft(customer)!;
    Check(selectedDraft.Selections.Single().Id == slot && selectedDraft.Version > draft.Version, "Saving a time increments the reservation version");
    var replay = await Post("/api/Appoinment/insertCustomerTimeSelection", change);
    Check(replay.Body.GetProperty("item1").GetString() == "Success" && store.GetDraft(customer)!.Version == selectedDraft.Version,
        "Repeating the same save does not duplicate or change the selection");
    var review = new ReviewTimeSelectionRequest { SeatBookingDetailsID = draft.SeatBookingDetailsID, BookingDate = draft.BookingDate,
        Version = selectedDraft.Version, TimeAllocationIDs = [slot] };
    var stale = new ReviewTimeSelectionRequest { SeatBookingDetailsID = draft.SeatBookingDetailsID, BookingDate = draft.BookingDate,
        Version = draft.Version, TimeAllocationIDs = [slot] };
    Check((await Post("/api/Appoinment/goNextSeatSelection", stale)).Body.GetProperty("item1").GetString() == "Selection Changed",
        "Review rejects a stale reservation version");
    Check((await Post("/api/Appoinment/goNextSeatSelection", review)).Body.GetProperty("item1").GetString() == "Success",
        "Review accepts the exact current date, slot and version");

    db.Execute("""
        INSERT dbo.SeatBookingDetails(SeatBookingDetailsID,CustomerID,SeatCount,BookingStatusMasterID,BookingDate,CreatedOn,SalonMasterID)
        VALUES(@otherBooking,@other,1,1,@date,GETDATE(),@salon);
        """, new { otherBooking, other, date, salon });
    var otherDraft = store.GetDraft(other)!;
    Check(store.Change(other, new insertCustomerTimeSelection { TimeAllocationID = slot, SeatBookingDetailsID = otherBooking,
        ExpectedVersion = otherDraft.Version, BookingDate = draft.BookingDate, SalonMasterID = salon }, false).Item1 == "Booked",
        "Another customer cannot take the held appointment time");

    db.Execute("""
        INSERT dbo.HairCut_Item_Bill_Master_Primary(UniqueBillID,SeatBookingDetailsID,CustomerBillID,CreatedDate,CreatedBy)
        VALUES(@bill,@booking,'compatibility-fixture',GETDATE(),@customer);
        INSERT dbo.HairCut_Item_Customer_Bill_Primary(HairCut_Item_Customer_Unique_ID,UniqueBillID,Created_Date,Created_By)
        VALUES(NEWID(),@bill,GETDATE(),@customer);
        """, new { bill, booking = draft.SeatBookingDetailsID, customer });
    Check((await Post("/api/Appoinment/SendOTPCustomer", review)).Body.GetProperty("item1").GetString() == "Success",
        "Legacy-named checkout endpoint confirms the versioned booking without sending OTP");
    Check(store.GetDraft(customer)!.Confirmed, "Confirmed booking is persisted");
    Check(db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_Customer_Bill_Primary WHERE UniqueBillID=@bill", new { bill }) == 0,
        "Successful checkout clears this project's customer cart");
    Check((await Post("/api/Appoinment/SendOTPCustomer", review)).Body.GetProperty("item1").GetString() == "Success"
        && db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_Bill_Master WHERE UniqueBillID=@bill", new { bill }) == 1,
        "Repeated checkout does not duplicate the bill");
    var cookiesBeforeLogout = handler.CookieContainer.GetCookieHeader(new Uri(address));
    Check((await Post("/api/Login/logout", new { })).Status == HttpStatusCode.OK, "Logout succeeds");
    Check((await Get("/api/Login/session")).Status == HttpStatusCode.Unauthorized, "Logout clears the customer session");
    using (var replayClient = new HttpClient { BaseAddress = new Uri(address) })
    {
        replayClient.DefaultRequestHeaders.Add("Cookie", cookiesBeforeLogout);
        Check((await replayClient.GetAsync("/api/Login/session")).StatusCode == HttpStatusCode.Unauthorized,
            "The logged-out session cookie cannot restore authentication");
    }
}
finally
{
    if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
    db.Execute("""
        DELETE dbo.HairCut_Item_Customer_Bill WHERE UniqueBillID=@bill;
        DELETE dbo.HairCut_Item_Bill_Master WHERE UniqueBillID=@bill;
        DELETE dbo.HairCut_Item_Customer_Bill_Primary WHERE UniqueBillID=@bill;
        DELETE dbo.HairCut_Item_Bill_Master_Primary WHERE UniqueBillID=@bill;
        DELETE dbo.CustomerTimeSelection WHERE CustomerID IN (@customer,@other);
        DELETE dbo.SeatBookingDetails WHERE CustomerID IN (@customer,@other);
        DELETE dbo.CustomerRegistration WHERE CustomerID IN (@customer,@other);
        DELETE dbo.TimeAllocation WHERE TimeAllocationID=@slot;
        DELETE dbo.SalonMaster WHERE SalonMasterID=@salon;
        DELETE dbo.LocationMaster WHERE LocationMasterID=@location;
        """, new { bill, customer, other, slot, salon, location });
    Check(db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SeatBookingDetails WHERE CustomerID IN (@customer,@other)", new { customer, other }) == 0,
        "Local booking fixtures are removed");
}
Console.WriteLine($"{checks} compatibility checks passed; no SMS or push messages were sent.");
