using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln"))) root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
var source = new ConfigurationBuilder().AddJsonFile(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")).Build();
var settings = new SqlConnectionStringBuilder(source.GetConnectionString("dbconnection"));
var host = settings.DataSource.Split('\\', ',')[0];
if (!new[] { ".", "localhost", "127.0.0.1", "(localdb)", Environment.MachineName }.Contains(host, StringComparer.OrdinalIgnoreCase))
    throw new Exception("These checks only use a disposable local SQL database.");
settings.Encrypt = SqlConnectionEncryptOption.Optional;
settings.InitialCatalog = "master";
await using var master = new SqlConnection(settings.ConnectionString);
await master.OpenAsync();
var databaseName = "SalonexaOwnerNotifications_" + Guid.NewGuid().ToString("N");
await master.ExecuteAsync($"CREATE DATABASE [{databaseName}]");
await master.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON");
if (args.Contains("--row-versioning"))
    await master.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON");
settings.InitialCatalog = databaseName;
await using var db = new SqlConnection(settings.ConnectionString);
WebApplication? app = null;
try
{
    await db.OpenAsync();
    await db.ExecuteAsync("""
        CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, IsActive bit NOT NULL);
        CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier, IsActive bit);
        CREATE TABLE dbo.CustomerRegistration(CustomerID uniqueidentifier PRIMARY KEY, FirstName nvarchar(150), LastName nvarchar(150));
        CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier,
            SalonMasterID uniqueidentifier, BookingDate date, SeatCount int, BookingStatusMasterID int);
        CREATE TABLE dbo.TimeAllocation(TimeAllocationID uniqueidentifier PRIMARY KEY, FullTiming time);
        CREATE TABLE dbo.CustomerTimeSelection(TimeSelectionID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier,
            SeatBookingDetailsID uniqueidentifier, TimeAllocationID uniqueidentifier);
        """);
    var salon = Guid.NewGuid(); var otherSalon = Guid.NewGuid(); var owner = Guid.NewGuid(); var otherOwner = Guid.NewGuid();
    var colleague = Guid.NewGuid(); var customer = Guid.NewGuid(); var slot = Guid.NewGuid();
    await db.ExecuteAsync("""
        INSERT dbo.SalonMaster VALUES(@salon,1),(@otherSalon,1);
        INSERT dbo.SalonOwner VALUES(@owner,@salon,1),(@colleague,@salon,1),(@otherOwner,@otherSalon,1);
        INSERT dbo.CustomerRegistration VALUES(@customer,N'Booking',N'Customer');
        INSERT dbo.TimeAllocation VALUES(@slot,'15:30');
        INSERT dbo.SeatBookingDetails VALUES(NEWID(),@customer,@salon,'20261106',1,2);
        """, new { salon, otherSalon, owner, colleague, otherOwner, customer, slot });
    var migration = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "OwnerBookingNotifications.sql"));
    async Task Migrate() {
        foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
    }
    await Migrate(); await Migrate();
    var checks = 0;
    void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:dbconnection"] = settings.ConnectionString,
        ["Authentication:KeyRingPath"] = Path.Combine(AppContext.BaseDirectory, "fixture-keys")
    }).Build();
    var store = new OwnerBookingNotificationStore(config);
    Check((await store.GetInboxAsync(owner, salon, default)).Items.Count == 0, "Rerunning the migration does not notify historical bookings");
    const string token = "fixture-owner-phone-token";
    const string secondToken = "fixture-second-phone-token";
    const string otherToken = "fixture-other-salon-token";
    await store.RegisterAsync(owner, token, default); await store.RegisterAsync(owner, secondToken, default);
    await store.RegisterAsync(otherOwner, otherToken, default);
    async Task<Guid> Draft() {
        var id = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.SeatBookingDetails VALUES(@id,@customer,@salon,'20261106',1,1);
            INSERT dbo.CustomerTimeSelection VALUES(NEWID(),@customer,@id,@slot);
            """, new { id, customer, salon, slot });
        return id;
    }
    async Task Confirm(Guid id) => await db.ExecuteAsync("UPDATE dbo.SeatBookingDetails SET BookingStatusMasterID=2 WHERE SeatBookingDetailsID=@id", new { id });
    var booking = await Draft();
    Check((await store.GetInboxAsync(owner, salon, default)).UnreadCount == 0, "Temporary seat and time selections do not notify the admin");
    await Confirm(booking);
    var firstInbox = await store.GetInboxAsync(owner, salon, default);
    Check(firstInbox.Items.Count == 1 && firstInbox.UnreadCount == 1 && firstInbox.Items[0].SeatBookingDetailsID == booking,
        "Booking confirmation persists a new admin inbox alert");
    Check(firstInbox.Items[0].CustomerName == "Booking Customer" && firstInbox.Items[0].StartTime == TimeSpan.FromHours(15.5),
        "The alert includes the booked customer, date, seat count and appointment time");
    Check(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingPushDelivery") == 2, "Only the matching salon's two phones receive queued pushes");
    await Confirm(booking);
    Check((await store.GetInboxAsync(owner, salon, default)).Items.Count == 1, "Repeated confirmation does not create duplicate alerts");
    var rolledBack = await Draft();
    await using (var tx = await db.BeginTransactionAsync()) {
        await db.ExecuteAsync("UPDATE dbo.SeatBookingDetails SET BookingStatusMasterID=2 WHERE SeatBookingDetailsID=@rolledBack", new { rolledBack }, tx);
        await tx.RollbackAsync();
    }
    Check((await store.GetInboxAsync(owner, salon, default)).Items.Count == 1, "A rolled-back checkout rolls back its inbox alert and push outbox");
    Check((await store.GetInboxAsync(otherOwner, otherSalon, default)).Items.Count == 0
        && (await store.GetInboxAsync(otherOwner, salon, default)).Items.Count == 0, "Other salon owners cannot read the booking notification");
    await store.ReadAsync(owner, salon, firstInbox.Items[0].NotificationID, default);
    var secondBooking = await Draft(); await Confirm(secondBooking);
    Check((await store.GetInboxAsync(owner, salon, default)).UnreadCount == 1, "Marking the displayed batch read preserves subsequent new bookings");
    Check((await store.GetInboxAsync(colleague, salon, default)).UnreadCount == 2, "Read state belongs to each owner separately");
    var beforeRefresh = await db.QuerySingleAsync<Guid>("SELECT BindingID FROM dbo.OwnerPushDevice WHERE DeviceToken=@token", new { token });
    await store.RegisterAsync(owner, token, default);
    Check(await db.QuerySingleAsync<Guid>("SELECT BindingID FROM dbo.OwnerPushDevice WHERE DeviceToken=@token", new { token }) == beforeRefresh,
        "Refreshing registration preserves the current device binding and pending deliveries");
    foreach (var isolation in new[] { IsolationLevel.ReadUncommitted, IsolationLevel.RepeatableRead, IsolationLevel.Serializable, IsolationLevel.Snapshot })
    {
        await using var transaction = (SqlTransaction)await db.BeginTransactionAsync(isolation);
        var claim = (await db.QueryAsync<OwnerBookingPushDelivery>("dbo.ClaimOwnerBookingPush",
            new { LeaseID = Guid.NewGuid() }, transaction, commandType: CommandType.StoredProcedure)).ToList();
        Check(claim.Count == 4 && claim.All(delivery => delivery.LeaseID != Guid.Empty),
            $"Claiming works with inherited {isolation} isolation (row versioning: {args.Contains("--row-versioning")})");
        await transaction.RollbackAsync();
    }
    var leased = await store.ClaimAsync(default);
    Check(leased.Count == 4 && leased.All(d => d.SalonMasterID == salon && d.DeviceToken != otherToken), "The worker leases only matching salon deliveries");
    Check((await store.ClaimAsync(default)).Count == 0, "Another worker cannot claim a live delivery lease");
    var first = leased[0]; var realLease = first.LeaseID; first.LeaseID = Guid.NewGuid();
    await store.FinishAsync(first, true, false, null, default); first.LeaseID = realLease;
    Check(await db.QuerySingleAsync<string>("SELECT Status FROM dbo.OwnerBookingPushDelivery WHERE DeliveryID=@DeliveryID", first) == "Sending",
        "A stale worker cannot acknowledge another worker's lease");
    foreach (var delivery in leased) await store.FinishAsync(delivery, true, false, null, default);
    Check(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingPushDelivery WHERE Status='Sent'") == 4, "Successful pushes are acknowledged per phone");
    var retryBooking = await Draft(); await Confirm(retryBooking);
    var sender = new FixturePushSender { Fail = true };
    var worker = new OwnerBookingPushWorker(store, sender, NullLogger<OwnerBookingPushWorker>.Instance);
    await worker.ProcessPendingAsync(default);
    Check(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingPushDelivery WHERE Status='Pending' AND AttemptCount=1") == 2,
        "Push service failures persist retryable per-device deliveries");
    await db.ExecuteAsync("UPDATE dbo.OwnerBookingPushDelivery SET NextAttemptUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE Status='Pending'");
    sender.Fail = false; await worker.ProcessPendingAsync(default);
    Check(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingPushDelivery WHERE Status='Sent'") == 6, "Queued retries succeed without losing the inbox alert");
    var reboundBooking = await Draft(); await Confirm(reboundBooking);
    await store.RegisterAsync(otherOwner, token, default);
    var rebound = await store.ClaimAsync(default);
    Check(rebound.Count == 1 && rebound.All(d => d.DeviceToken == secondToken), "Reassigning a phone cancels the previous owner's queued customer details");
    await store.FinishAsync(rebound[0], false, true, "Unregistered", default);
    Check(!await db.QuerySingleAsync<bool>("SELECT IsActive FROM dbo.OwnerPushDevice WHERE DeviceToken=@secondToken", new { secondToken }), "Invalid Firebase tokens are deactivated");
    await store.RegisterAsync(owner, secondToken, default);
    var expireBooking = await Draft(); await Confirm(expireBooking);
    var expired = (await store.ClaimAsync(default)).Single();
    await db.ExecuteAsync("UPDATE dbo.OwnerBookingPushDelivery SET LeaseUntilUtc=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE DeliveryID=@DeliveryID", expired);
    var recovered = (await store.ClaimAsync(default)).Single();
    Check(recovered.DeliveryID == expired.DeliveryID && recovered.LeaseID != expired.LeaseID, "A crashed worker's expired lease is recoverable");
    await store.FinishAsync(expired, true, false, null, default);
    Check(await db.QuerySingleAsync<string>("SELECT Status FROM dbo.OwnerBookingPushDelivery WHERE DeliveryID=@DeliveryID", expired) == "Sending", "A recovered delivery ignores the old worker's late acknowledgment");
    await store.FinishAsync(recovered, true, false, null, default);

    var owners = new FixtureOwners(owner, salon, otherOwner, otherSalon);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddConfiguration(config);
    builder.Services.AddHttpContextAccessor(); builder.Services.AddDistributedMemoryCache(); builder.Services.AddSession();
    builder.Services.AddSingleton<ISalonOwnerAuthenticationService>(owners);
    builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration, builder.Environment);
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddSingleton(store); builder.Services.AddScoped<SalonOwnerDashboardService>(); builder.Services.AddScoped<BookingQrService>();
    builder.Services.AddControllers().AddApplicationPart(typeof(OwnerBookingNotificationsController).Assembly);
    app = builder.Build();
    app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
    app.UseSession(); app.UseMiddleware<SalonOwnerSessionMiddleware>(); app.MapControllers();
    app.MapPost("/fixture/login/{id:guid}", async (Guid id, HttpContext context, SalonOwnerPersistentSignIn signIn) => {
        await signIn.SignInAsync(context, (await owners.GetActiveSessionOwnerAsync(id, default))!); return Results.NoContent();
    });
    app.MapPost("/fixture/legacy", (HttpContext context) => {
        context.Session.SetString("SalonOwnerID", owner.ToString()); context.Session.SetString("SalonMasterID", salon.ToString()); return Results.NoContent();
    });
    await app.StartAsync();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };
    var origin = new Uri("https://localhost"); var jar = new CookieContainer();
    async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, bool header = true) {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", jar.GetCookieHeader(origin));
        if (header) request.Headers.Add("X-Salonexa-Request", "1");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values) jar.SetCookies(origin, value);
        return response;
    }
    const string api = "/api/Admin/notifications";
    using (var response = await Send(HttpMethod.Get, api)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous callers cannot read owner booking alerts");
    using (var response = await Send(HttpMethod.Post, "/fixture/legacy")) Check(response.IsSuccessStatusCode, "Legacy session fixture starts");
    using (var response = await Send(HttpMethod.Get, api)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Unvalidated session IDs do not authorize notifications");
    using (var response = await Send(HttpMethod.Post, "/fixture/login/" + owner)) Check(response.IsSuccessStatusCode, "The owner can authenticate using the protected owner ticket");
    using (var response = await Send(HttpMethod.Get, api)) {
        var inbox = await response.Content.ReadFromJsonAsync<OwnerBookingInbox>();
        Check(response.IsSuccessStatusCode && inbox!.Items.All(n => n.SalonMasterID == salon), "The HTTP inbox uses the authenticated owner's salon");
        Check(response.Headers.CacheControl?.NoStore == true, "Customer details are not cacheable");
    }
    using (var response = await Send(HttpMethod.Post, api + "/register-device", new { token = "fixture-api-phone-token" }, header: false))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Notification writes require the application's CSRF header");
    using (var response = await Send(HttpMethod.Post, api + "/register-device", new { token = "bad" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid phone tokens are rejected");
    using (var response = await Send(HttpMethod.Post, api + "/register-device", new { token = "fixture-api-phone-token", ownerID = otherOwner }))
        Check(response.IsSuccessStatusCode && await db.QuerySingleAsync<Guid>("SELECT SalonOwnerID FROM dbo.OwnerPushDevice WHERE DeviceToken='fixture-api-phone-token'") == owner,
            "Browser-supplied owner IDs cannot redirect phone registration");
    using (var response = await Send(HttpMethod.Post, api + "/read", new { throughID = -1 })) Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid read cursors are rejected");
    using (var response = await Send(HttpMethod.Post, "/api/Admin/logout", new { pushToken = "fixture-api-phone-token" }))
        Check(response.IsSuccessStatusCode && !await db.QuerySingleAsync<bool>("SELECT IsActive FROM dbo.OwnerPushDevice WHERE DeviceToken='fixture-api-phone-token'"), "Online sign-out revokes the phone token");
    using (var response = await Send(HttpMethod.Get, api)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Signed-out owners cannot restore the inbox");
    Console.WriteLine($"Passed {checks} owner booking SQL/HTTP/worker checks. No live pushes were sent.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    await db.CloseAsync(); SqlConnection.ClearPool(db);
    await master.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
}

sealed class FixturePushSender : IOwnerBookingPushSender
{
    public bool IsConfigured => true;
    public bool Fail;
    public Task<PushSendResult> SendOwnerBookingAsync(OwnerBookingPushDelivery delivery, CancellationToken cancellationToken) =>
        Task.FromResult(new PushSendResult(Fail ? 0 : 1, [], Fail ? ["Fixture temporary failure"] : []));
}
sealed class FixtureOwners(Guid owner, Guid salon, Guid otherOwner, Guid otherSalon) : ISalonOwnerAuthenticationService
{
    public Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(id == owner || id == otherOwner ? new SalonOwnerLoginResponse {
            SalonOwnerID = id, SalonMasterID = id == owner ? salon : otherSalon, AuthenticationStamp = "fixture-password-version",
            OwnerName = "Fixture owner", UserName = "fixture-owner", SalonName = "Fixture salon"
        } : null);
    public Task<SalonOwnerLoginResponse?> AuthenticateAsync(SalonOwnerLoginRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SalonOwnerLoginResponse> InitializeAsync(InitializeSalonOwnerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
