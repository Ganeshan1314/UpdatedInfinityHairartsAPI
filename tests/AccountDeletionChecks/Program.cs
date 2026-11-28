using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;

// Runs only against a disposable LocalDB database; no application settings are read.
var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
    root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
var settings = new SqlConnectionStringBuilder(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30")
{
    InitialCatalog = "master"
};
await using var master = new SqlConnection(settings.ConnectionString);
await master.OpenAsync();
var database = "SalonexaDeletionChecks_" + Guid.NewGuid().ToString("N");
await master.ExecuteAsync($"CREATE DATABASE [{database}]");
settings.InitialCatalog = database;
await using var db = new SqlConnection(settings.ConnectionString);
var content = Directory.CreateTempSubdirectory("salonexa-deletion-").FullName;
var photos = Directory.CreateDirectory(Path.Combine(content, "CustomerImages")).FullName;
WebApplication? app = null;
var checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception("FAIL: " + label);
    checks++;
    Console.WriteLine("PASS: " + label);
}
try
{
    await db.OpenAsync();
    await db.ExecuteAsync("""
        CREATE TABLE dbo.CustomerRegistration(
            CustomerID uniqueidentifier NULL, MobileNo nvarchar(25), FirstName nvarchar(100), LastName nvarchar(100),
            MiddleName nvarchar(100), EmailAddress nvarchar(254), [password] nvarchar(200), UserTypeID int,
            ImageName nvarchar(300), PINCode nvarchar(20), GeneralAddress nvarchar(max), UserOTP nvarchar(20),
            CreatedOn datetime, CreatedBy uniqueidentifier, UpdatedOn datetime, UpdatedBy uniqueidentifier, IsDeleted bit);
        CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier,
            BookingDate date, BookingStatusMasterID int, CompletionStatus nvarchar(16) NOT NULL);
        CREATE TABLE dbo.CustomerTimeSelection(SeatBookingDetailsID uniqueidentifier, CustomerID uniqueidentifier);
        CREATE TABLE dbo.HairCut_Item_Bill_Master(SeatBookingDetailsID uniqueidentifier, IsDeleted bit NULL);
        CREATE TABLE dbo.OwnerBookingNotification(SeatBookingDetailsID uniqueidentifier, CustomerName nvarchar(300) NOT NULL);
        CREATE TABLE dbo.CustomerPushDevice(CustomerID uniqueidentifier, DeviceToken nvarchar(512));
        """);
    await db.ExecuteAsync("""
        CREATE PROCEDURE dbo.customerLoginButtomClick
            @CustomerID uniqueidentifier, @MobileNo nvarchar(25), @GeneralAddress nvarchar(max)
        AS INSERT dbo.CustomerRegistration(CustomerID,MobileNo,GeneralAddress) VALUES(@CustomerID,@MobileNo,@GeneralAddress);
        """);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development", ContentRootPath = content });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:dbconnection"] = settings.ConnectionString });
    builder.Services.AddControllers().AddApplicationPart(typeof(LoginController).Assembly);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<LoginService>();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<CustomerSessionRegistry>();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddSession(options => options.Cookie.Name = ".DeletionChecks");
    app = builder.Build();
    app.UseRouting();
    app.UseSession();
    app.UseMiddleware<CustomerApiSessionMiddleware>();
    app.MapControllers();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    HttpClient Device(bool applicationHeader = true)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(address) };
        if (applicationHeader) client.DefaultRequestHeaders.Add("X-Salonexa-Request", "1");
        return client;
    }
    async Task<bool> SignIn(HttpClient client, string mobile)
    {
        using var response = await client.PostAsJsonAsync("/api/Login/customerLoginButtomClick", new { MobileNo = mobile });
        return response.IsSuccessStatusCode;
    }
    async Task<bool> SignedIn(HttpClient client)
    {
        using var response = await client.GetAsync("/api/Login/session");
        return response.StatusCode == HttpStatusCode.OK;
    }
    Task<HttpResponseMessage> Request(HttpClient client, object? body = null) =>
        client.PostAsJsonAsync("/api/Login/requestAccountDeletion", body ?? new { });
    Task<HttpResponseMessage> Cancel(HttpClient client) =>
        client.PostAsJsonAsync("/api/Login/cancelAccountDeletion", new { });
    Task<HttpResponseMessage> Status(HttpClient client) => client.GetAsync("/api/Login/accountDeletion");
    async Task<string?> DueDate(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("deleteAfterUtc", out var value) ? value.GetString() : null;
    }
    async Task<string?> Pending(HttpClient client)
    {
        using var response = await Status(client);
        response.EnsureSuccessStatusCode();
        return await DueDate(response);
    }
    async Task<string?> RestoreSignIn(HttpClient client, string mobile)
    {
        using var response = await client.PostAsJsonAsync("/api/Login/customerLoginButtomClick",
            new { MobileNo = mobile, RestoreOnly = true });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("item1").GetString();
    }
    var worker = new CustomerAccountDeletionWorker(app.Services.GetRequiredService<IServiceScopeFactory>(),
        app.Services.GetRequiredService<CustomerSessionRegistry>(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<CustomerAccountDeletionWorker>.Instance);
    Task<int> Requests() => db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CustomerAccountDeletionRequest");
    Task<int> MakeDue() => db.ExecuteAsync(
        "UPDATE dbo.CustomerAccountDeletionRequest SET DeleteAfterUtc=DATEADD(MINUTE,-1,SYSUTCDATETIME())");

    using var phone = Device();
    using var tablet = Device();
    using var other = Device();
    Check(await SignIn(phone, "9876543210") && await SignIn(tablet, "9876543210") && await SignIn(other, "9123456780"),
        "Two customers sign in, one of them on two devices");
    var id = await db.ExecuteScalarAsync<Guid>("SELECT CustomerID FROM dbo.CustomerRegistration WHERE MobileNo='9876543210'");
    var otherId = await db.ExecuteScalarAsync<Guid>("SELECT CustomerID FROM dbo.CustomerRegistration WHERE MobileNo='9123456780'");
    var olderId = Guid.NewGuid();
    await db.ExecuteAsync("""
        UPDATE dbo.CustomerRegistration SET FirstName='Test', LastName='Person', MiddleName='Middle',
            EmailAddress='test@gmail.com', GeneralAddress='1 Test Street', ImageName='photo.jpg',
            [password]='secret', PINCode='620001', UserOTP='123456', UserTypeID=1;
        INSERT dbo.CustomerRegistration(CustomerID,MobileNo,FirstName,IsDeleted) VALUES(@olderId,'9876543210','Old copy',1);
        INSERT dbo.CustomerRegistration(CustomerID,MobileNo,FirstName,PINCode) VALUES(NULL,'9876543210','No ID copy','620001');
        """, new { olderId });

    var today = DateTime.UtcNow.AddMinutes(330).Date;
    async Task<Guid> Booking(Guid customer, DateTime date, string completion, int status = 2)
    {
        var booking = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.SeatBookingDetails VALUES(@booking,@customer,@date,@status,@completion);
            INSERT dbo.CustomerTimeSelection VALUES(@booking,@customer);
            INSERT dbo.HairCut_Item_Bill_Master VALUES(@booking,0);
            INSERT dbo.OwnerBookingNotification VALUES(@booking,N'Test Person');
            """, new { booking, customer, date, status, completion });
        return booking;
    }
    var upcoming = await Booking(id, today.AddDays(2), "Pending");
    var todayBooking = await Booking(id, today, "Pending");
    var inProgress = await Booking(id, today, "InProgress");
    var past = await Booking(id, today.AddDays(-5), "Completed");
    var othersUpcoming = await Booking(otherId, today.AddDays(2), "Pending");
    await db.ExecuteAsync("INSERT dbo.CustomerPushDevice VALUES(@id,'mine'),(@otherId,'theirs')", new { id, otherId });
    var myPhoto = Path.Combine(photos, $"{id}.jpg");
    var otherPhoto = Path.Combine(photos, $"{otherId}.png");
    await File.WriteAllTextAsync(myPhoto, "photo");
    await File.WriteAllTextAsync(otherPhoto, "photo");

    async Task<int> Slots(Guid booking) => await db.ExecuteScalarAsync<int>(
        "SELECT COUNT(*) FROM dbo.CustomerTimeSelection WHERE SeatBookingDetailsID=@booking", new { booking });
    async Task<string?> Mobile() => await db.ExecuteScalarAsync<string>(
        "SELECT MobileNo FROM dbo.CustomerRegistration WHERE CustomerID=@id", new { id });

    // Before the SQL script is deployed, requests must fail without changing anything.
    using (var response = await Request(phone))
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && await Mobile() == "9876543210" && await SignedIn(phone),
            "Without the SQL script a request fails and keeps the account and session");
    Check(await worker.ProcessDueAsync() == 0, "Without the SQL script the scheduled task fails safely");

    var script = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "DeleteCustomerAccount.sql"));
    for (var repeat = 0; repeat < 2; repeat++)
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
    Check(await Mobile() == "9876543210" && await Requests() == 0, "The SQL script is repeatable and changes no data on installation");

    using (var anonymous = Device())
    {
        using var read = await Status(anonymous);
        using var ask = await Request(anonymous);
        using var stop = await Cancel(anonymous);
        Check(new[] { read, ask, stop }.All(r => r.StatusCode == HttpStatusCode.Unauthorized), "Anonymous deletion requests are rejected");
    }
    using (var plain = Device(applicationHeader: false))
    {
        using var ask = await Request(plain);
        using var stop = await Cancel(plain);
        Check(ask.StatusCode == HttpStatusCode.BadRequest && stop.StatusCode == HttpStatusCode.BadRequest,
            "The application request header is required");
    }
    Check(await Pending(phone) is null, "No deletion is pending to begin with");

    // A customer ID supplied by the caller is ignored; only the session identity is used.
    string? due;
    using (var response = await Request(phone, new { CustomerID = otherId }))
    {
        due = await DueDate(response);
        var wait = DateTimeOffset.Parse(due!) - DateTimeOffset.UtcNow;
        Check(response.IsSuccessStatusCode && due!.EndsWith('Z') && wait > TimeSpan.FromDays(6.99) && wait <= TimeSpan.FromDays(7.01),
            "A request is dated 7 days ahead, in UTC");
    }
    Check(await db.ExecuteScalarAsync<Guid>("SELECT CustomerID FROM dbo.CustomerAccountDeletionRequest") == id,
        "The request belongs to the signed-in customer only");
    using (var response = await Request(tablet))
        Check(await DueDate(response) == due && await Requests() == 1 && await Pending(tablet) == due && await Pending(other) is null,
            "Asking again keeps the date, which the customer sees on every device");
    Check(await worker.ProcessDueAsync() == 0 && await Mobile() == "9876543210" && await Slots(upcoming) == 1
        && File.Exists(myPhoto) && await SignedIn(phone), "Nothing is erased during the waiting period");
    Check(!(await db.QueryAsync<Guid>("EXEC dbo.DeleteCustomerAccount @id", new { id })).Any()
        && !(await db.QueryAsync<Guid>("EXEC dbo.DeleteCustomerAccount @otherId", new { otherId })).Any()
        && await Mobile() == "9876543210" && await Slots(othersUpcoming) == 1,
        "The delete procedure refuses an account that is not due");

    using (var response = await Cancel(tablet))
        Check(response.IsSuccessStatusCode && await Requests() == 0 && await Pending(phone) is null, "The customer can cancel");
    using (var response = await Request(phone)) response.EnsureSuccessStatusCode();
    await MakeDue();
    using (var response = await Cancel(phone))
        Check(response.StatusCode == HttpStatusCode.Conflict && await Requests() == 1, "Cancelling is refused once the deletion is due");

    using (new FileStream(myPhoto, FileMode.Open, FileAccess.Read, FileShare.None))
        Check(await worker.ProcessDueAsync() == 0, "A photo that cannot be removed fails the deletion");
    Check(await Mobile() == "9876543210" && await Slots(upcoming) == 1 && await Requests() == 1
        && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CustomerPushDevice") == 2,
        "A failed deletion rolls back every database change and stays queued");

    Check(await worker.ProcessDueAsync() == 1 && await Requests() == 0 && await worker.ProcessDueAsync() == 0,
        "The scheduled task completes a due deletion once");
    Check(await db.ExecuteScalarAsync<int>("""
        SELECT COUNT(*) FROM dbo.CustomerRegistration WHERE CustomerID IN (@id,@olderId) AND IsDeleted=1
          AND CONCAT(MobileNo,FirstName,LastName,MiddleName,EmailAddress,GeneralAddress,ImageName,PINCode,[password],UserOTP)=''
        """, new { id, olderId }) == 2, "Personal details are erased, including older rows with the same number");
    Check(await db.ExecuteScalarAsync<int>("""
        SELECT COUNT(*) FROM dbo.CustomerRegistration WHERE CustomerID IS NULL AND IsDeleted=1
          AND CONCAT(MobileNo,FirstName,PINCode)=''
        """) == 1, "A row without a customer ID is erased too");
    Check(await Slots(upcoming) == 0 && await Slots(todayBooking) == 0, "Upcoming appointments release their times");
    Check(await db.ExecuteScalarAsync<int>("""
        SELECT COUNT(*) FROM dbo.SeatBookingDetails s JOIN dbo.HairCut_Item_Bill_Master b ON b.SeatBookingDetailsID=s.SeatBookingDetailsID
        WHERE s.SeatBookingDetailsID IN (@upcoming,@todayBooking) AND s.BookingStatusMasterID=1 AND b.IsDeleted=1
        """, new { upcoming, todayBooking }) == 2, "Cancelled appointments are no longer confirmed or billed");
    Check(await Slots(past) == 1 && await Slots(inProgress) == 1 && await db.ExecuteScalarAsync<int>(
        "SELECT COUNT(*) FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID IN (@past,@inProgress) AND BookingStatusMasterID=2",
        new { past, inProgress }) == 2, "Completed and in-progress visits stay as salon records");
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingNotification WHERE CustomerName=N'Deleted customer'") == 4,
        "The name is removed from the customer's salon notifications");
    Check(await db.ExecuteScalarAsync<string>("SELECT STRING_AGG(DeviceToken, ',') FROM dbo.CustomerPushDevice") == "theirs",
        "Only the deleted customer's push tokens are removed");
    Check(!File.Exists(myPhoto) && File.Exists(otherPhoto), "Only the deleted customer's photo is removed");
    Check(await Slots(othersUpcoming) == 1 && await db.ExecuteScalarAsync<string>(
        "SELECT FirstName FROM dbo.CustomerRegistration WHERE CustomerID=@otherId AND PINCode='620001' AND UserOTP='123456'", new { otherId }) == "Test"
        && await SignedIn(other), "Another customer's data and session are unaffected");
    Check(!await SignedIn(phone) && !await SignedIn(tablet), "Sessions end on every device");
    Check(await RestoreSignIn(tablet, "9876543210") == "Account Not Found" && !await SignedIn(tablet)
        && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CustomerRegistration WHERE MobileNo='9876543210'") == 0,
        "Automatic sign-in does not recreate a deleted account");
    Check(await RestoreSignIn(other, "9123456780") == "Success" && await SignedIn(other),
        "Automatic sign-in still works for an existing account");
    Check(await SignIn(phone, "9876543210") && await db.ExecuteScalarAsync<Guid>(
        "SELECT CustomerID FROM dbo.CustomerRegistration WHERE MobileNo='9876543210' AND ISNULL(IsDeleted,0)=0") != id,
        "Signing in later creates a new, empty account");
    Console.WriteLine($"PASS: {checks} account deletion checks.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    await db.CloseAsync();
    SqlConnection.ClearAllPools();
    await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    Directory.Delete(content, true);
}
