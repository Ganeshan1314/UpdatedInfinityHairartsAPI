using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using SalonexaAdminAPI.Controllers;
using SalonexaAdminAPI.Services;

// End-to-end checks for the Salonexa Admin API routes that were added after the host split:
// signup, GET/PUT salon, owner-profile, change-username/password, appointment start, app updates.
// Everything runs against a disposable local database and temp folder, with the real controllers,
// real login procedures and the repository SQL scripts.
try { await RunAsync(); }
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }

static async Task RunAsync()
{
    var root = Directory.GetCurrentDirectory();
    while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
        root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
    var source = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "SalonexaAdminAPI", "appsettings.json")).Build();
    var settings = new SqlConnectionStringBuilder(source.GetConnectionString("dbconnection"));
    if (!new[] { ".", "localhost", "(local)", "127.0.0.1", "(localdb)", Environment.MachineName }
        .Contains(settings.DataSource.Split('\\', ',')[0], StringComparer.OrdinalIgnoreCase))
        throw new Exception("These checks require a disposable local SQL database.");
    settings.Encrypt = SqlConnectionEncryptOption.Optional;
    settings.InitialCatalog = "master";
    var database = "SalonexaOwnerFlowChecks_" + Guid.NewGuid().ToString("N");
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    var fixtureRoot = Path.Combine(tempRoot, database);
    var salonImages = Path.Combine(fixtureRoot, "SalonImages");
    Directory.CreateDirectory(salonImages);
    await using var master = new SqlConnection(settings.ConnectionString);
    await master.OpenAsync();
    await master.ExecuteAsync($"CREATE DATABASE [{database}]");
    settings.InitialCatalog = database;
    WebApplication? app = null;
    var checks = 0;
    void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        checks++;
        Console.WriteLine("PASS: " + description);
    }
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOhsAAAAASUVORK5CYII=");
    int Photos() => Directory.EnumerateFiles(salonImages, "*", SearchOption.AllDirectories).Count();
    try
    {
        await using var db = new SqlConnection(settings.ConnectionString);
        await db.OpenAsync();
        async Task RunScript(string relative)
        {
            var script = await File.ReadAllTextAsync(Path.Combine(root, "SalonexaAdminAPI", relative));
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
        }
        // Production column shapes for the tables these routes touch.
        await db.ExecuteAsync("""
            CREATE TABLE dbo.LocationMaster(LocationMasterID uniqueidentifier PRIMARY KEY, LocationName nvarchar(150) NOT NULL,
                IsActive bit NOT NULL, DisplayOrder int NOT NULL, CreatedUtc datetime2(0) NULL, UpdatedUtc datetime2(0) NULL);
            CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier NOT NULL PRIMARY KEY, LocationMasterID uniqueidentifier NOT NULL,
                SalonName nvarchar(200) NOT NULL, SalonAddress nvarchar(500) NULL, ContactNumber nvarchar(20) NULL,
                IsActive bit NOT NULL DEFAULT 1, DisplayOrder int NOT NULL DEFAULT 0, CreatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
                UpdatedUtc datetime2(0) NULL, OpeningTime time(0) NULL, ClosingTime time(0) NULL, ChairCount int NOT NULL DEFAULT 1,
                WeeklyHoliday tinyint NULL, Latitude decimal(9,6) NULL, Longitude decimal(9,6) NULL,
                IsVisibleToCustomers bit NOT NULL DEFAULT 0, City nvarchar(150) NULL, Pincode nvarchar(20) NULL, ImagePath nvarchar(200) NULL);
            CREATE TABLE dbo.TimeAllocation(TimeAllocationID uniqueidentifier PRIMARY KEY, FullTiming time(0) NOT NULL);
            CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier NOT NULL,
                BookingStatusMasterID int NOT NULL, BookingDate date NOT NULL, SeatCount int NULL,
                CompletionStatus nvarchar(16) NOT NULL CONSTRAINT DF_Completion DEFAULT N'Pending'
                    CONSTRAINT CK_SeatBookingDetails_CompletionStatus CHECK (CompletionStatus IN (N'Pending', N'Completed')));
            CREATE TABLE dbo.CustomerTimeSelection(SeatBookingDetailsID uniqueidentifier NOT NULL, TimeAllocationID uniqueidentifier NOT NULL);
            """);
        await RunScript("Database/SalonOwnerAuthentication.sql");
        foreach (var script in new[] { "Database/SalonOwnerSignup.sql", "Database/SalonOwnerSalonProfile.sql", "Database/SalonOwnerAppointmentStart.sql" })
        { await RunScript(script); await RunScript(script); }
        Check(true, "The signup, salon-profile and appointment-start scripts install and can be rerun");
        var area = Guid.NewGuid();
        var seededSalon = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.LocationMaster VALUES(@area, N'Rasampalayam', 1, 1, SYSUTCDATETIME(), NULL);
            INSERT dbo.SalonMaster(SalonMasterID, LocationMasterID, SalonName) VALUES(@seededSalon, @area, N'Existing Salon');
            """, new { area, seededSalon });

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = "Development", ContentRootPath = fixtureRoot
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ConnectionStrings:dbconnection"] = settings.ConnectionString;
        builder.Configuration["Authentication:KeyRingPath"] = Path.Combine(fixtureRoot, "keys");
        builder.Configuration["AdminAppUpdates:Android:LatestVersionCode"] = "7";
        builder.Configuration["AdminAppUpdates:Android:LatestVersion"] = "1.1.2";
        builder.Services.AddControllers().AddApplicationPart(typeof(AdminController).Assembly);
        builder.Services.AddScoped<SalonOwnerAuthenticationService>();
        builder.Services.AddScoped<ISalonOwnerAuthenticationService>(s => s.GetRequiredService<SalonOwnerAuthenticationService>());
        builder.Services.AddScoped<SalonOwnerDashboardService>();
        builder.Services.AddScoped<BookingQrService>();
        builder.Services.AddScoped<SalonOwnerAppointmentsReader>();
        builder.Services.AddScoped<SalonOwnerSalonProfileService>();
        builder.Services.AddScoped<SalonOwnerRegistrationService>();
        builder.Services.AddSingleton<SalonImageStore>();
        SalonOwnerRegistrationService.AddOwnerSignupRateLimit(builder.Services);
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration, builder.Environment);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        app = builder.Build();
        // The owner cookie is Secure; treat loopback requests as HTTPS.
        app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
        SalonImageStore.UseSalonImages(app);
        app.UseRouting();
        app.UseRateLimiter();
        app.UseSession();
        app.UseMiddleware<SalonOwnerSessionMiddleware>();
        app.MapControllers();
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        OwnerClient Client() => new(address);

        MultipartFormDataContent Signup(string user = "newowner", string salon = "Glow Studio", string email = "owner@gmail.com",
            string pincode = "620001", bool accept = true, bool image = true, byte[]? bytes = null, string mime = "image/png",
            string password = "Str0ngPass!", string city = "Rasampalayam")
        {
            var form = new MultipartFormDataContent();
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["ownerName"] = "New Owner", ["emailAddress"] = email, ["userName"] = user, ["password"] = password,
                ["salonName"] = salon, ["city"] = city, ["pincode"] = pincode, ["latitude"] = "10.8", ["longitude"] = "78.7",
                ["contactNumber"] = "9876543210", ["salonAddress"] = "12 Main Road", ["acceptPrivacyPolicy"] = accept ? "true" : "false",
                ["locationMasterID"] = area.ToString()
            }) form.Add(new StringContent(value), key);
            if (image)
            {
                var content = new ByteArrayContent(bytes ?? png);
                content.Headers.ContentType = new MediaTypeHeaderValue(mime);
                form.Add(content, "image", "../../untrusted.png");
            }
            return form;
        }

        // ---------------- signup ----------------
        using var anonymous = Client();
        using (var form = Signup())
        {
            var created = await anonymous.Send(HttpMethod.Post, "/api/Admin/signup", form);
            Check(created.StatusCode == HttpStatusCode.Created, "A valid signup creates the account (201)");
        }
        var row = await db.QuerySingleAsync("""
            SELECT m.SalonMasterID, m.IsVisibleToCustomers, m.IsActive, m.SalonAddress, m.ImagePath, m.City, o.PasswordHash, o.EmailAddress
            FROM dbo.SalonOwner o JOIN dbo.SalonMaster m ON m.SalonMasterID = o.SalonMasterID WHERE o.UserName = N'newowner'
            """);
        Check(row.IsVisibleToCustomers == false && row.IsActive == true, "A new salon is active but hidden from customers until approved");
        Check(row.PasswordHash != "Str0ngPass!" && ((string)row.PasswordHash).Length > 40, "The password is stored hashed, never as plain text");
        Check(((string)row.SalonAddress).Contains("620001") && ((string)row.SalonAddress).Contains("Rasampalayam"), "The saved address includes the city and PIN code");
        Check(Photos() == 1 && Regex.IsMatch((string)row.ImagePath, @"^/SalonImages/[a-f0-9]{32}/[a-f0-9]{32}\.png$"), "The salon photo is stored under a generated name");
        var photoUrl = (string)row.ImagePath;
        Check((await anonymous.GetBytes(photoUrl)).SequenceEqual(png), "The saved salon photo is served from /SalonImages");

        using (var form = Signup(salon: "Another Salon")) Check((await anonymous.Send(HttpMethod.Post, "/api/Admin/signup", form)).StatusCode == HttpStatusCode.Conflict, "A duplicate username is rejected (409)");
        using (var form = Signup(user: "second", salon: "  glow STUDIO ")) Check((await anonymous.Send(HttpMethod.Post, "/api/Admin/signup", form)).StatusCode == HttpStatusCode.Conflict, "A duplicate salon name is rejected, ignoring case and spaces (409)");
        Check(Photos() == 1, "Rejected duplicate signups leave no orphaned photos");
        foreach (var (label, form) in new (string, MultipartFormDataContent)[]
        {
            ("non-Gmail email", Signup(user: "u1", salon: "S1", email: "owner@example.com")),
            ("missing privacy consent", Signup(user: "u2", salon: "S2", accept: false)),
            ("bad PIN code", Signup(user: "u3", salon: "S3", pincode: "012345")),
            ("short password", Signup(user: "u4", salon: "S4", password: "short")),
            ("missing photo", Signup(user: "u5", salon: "S5", image: false)),
            ("not an image", Signup(user: "u6", salon: "S6", bytes: "this is not an image"u8.ToArray())),
            ("image type mismatch", Signup(user: "u7", salon: "S7", mime: "image/jpeg")),
        })
        {
            using (form) Check((await anonymous.Send(HttpMethod.Post, "/api/Admin/signup", form)).StatusCode == HttpStatusCode.BadRequest, $"Signup with a {label} is rejected (400)");
        }
        Check(Photos() == 1 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SalonOwner") == 1, "Rejected signups create no owners and no photos");

        // ---------------- sign-in ----------------
        using var owner = Client();
        Check((await owner.Login("newowner", "wrong-password")).StatusCode == HttpStatusCode.Unauthorized, "Wrong password cannot sign in");
        Check((await owner.Login("newowner", "Str0ngPass!")).StatusCode == HttpStatusCode.OK, "The new owner can sign in with the signup credentials");

        // ---------------- salon ----------------
        Check((await anonymous.Send(HttpMethod.Get, "/api/Admin/salon")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous salon reads need sign-in");
        object Update(string name = "Glow Studio & Spa", string open = "09:30", string close = "20:00", int chairs = 4, byte? holiday = 2,
            decimal? lat = 10.81m, decimal? lon = 78.69m, Guid? location = null) => new
        { salonName = name, locationMasterID = location ?? area, salonAddress = "14 Main Road", contactNumber = "9123456780",
          openingTime = open, closingTime = close, chairCount = chairs, weeklyHoliday = holiday, latitude = lat, longitude = lon };
        Check((await anonymous.Send(HttpMethod.Put, "/api/Admin/salon", JsonContent.Create(Update()))).StatusCode == HttpStatusCode.Unauthorized, "Anonymous salon saves need sign-in");
        var salonJson = await owner.GetJson<JsonElement>("/api/Admin/salon");
        Check(salonJson.GetProperty("salonName").GetString() == "Glow Studio" && salonJson.GetProperty("city").GetString() == "Rasampalayam"
            && salonJson.GetProperty("ownerEmailAddress").GetString() == "owner@gmail.com" && salonJson.GetProperty("isVisibleToCustomers").GetBoolean() == false,
            "GET salon returns the owner's own salon with the camelCase fields the app expects");
        var saved = await owner.Send(HttpMethod.Put, "/api/Admin/salon", JsonContent.Create(Update()));
        var savedJson = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Check(saved.StatusCode == HttpStatusCode.OK && savedJson.GetProperty("salonName").GetString() == "Glow Studio & Spa"
            && savedJson.GetProperty("openingTime").GetString() == "09:30" && savedJson.GetProperty("chairCount").GetInt32() == 4
            && savedJson.GetProperty("weeklyHoliday").GetInt32() == 2, "Saving the salon returns the updated details");
        Check(await db.ExecuteScalarAsync<string>("SELECT SalonName FROM dbo.SalonMaster WHERE SalonMasterID=@id", new { id = (Guid)row.SalonMasterID }) == "Glow Studio & Spa"
            && await db.ExecuteScalarAsync<string>("SELECT SalonName FROM dbo.SalonMaster WHERE SalonMasterID=@seededSalon", new { seededSalon }) == "Existing Salon",
            "The edit is saved to the owner's salon only");
        foreach (var (label, body) in new (string, object)[]
        {
            ("closing before opening", Update(open: "18:00", close: "09:00")), ("zero chairs", Update(chairs: 0)),
            ("a pin without longitude", Update(lon: null)), ("an unknown location", Update(location: Guid.NewGuid())),
            ("an invalid holiday", Update(holiday: 9)), ("a blank name", Update(name: " ")),
        })
            Check((await owner.Send(HttpMethod.Put, "/api/Admin/salon", JsonContent.Create(body))).StatusCode == HttpStatusCode.BadRequest, $"Saving a salon with {label} is rejected (400)");

        // ---------------- owner profile / account ----------------
        var profile = await owner.GetJson<JsonElement>("/api/Admin/owner-profile");
        Check(profile.GetProperty("userName").GetString() == "newowner" && profile.GetProperty("readOnly").GetBoolean() == false
            && profile.GetProperty("canChangeUserName").GetBoolean(), "The owner profile is editable and shows the signed-in account");
        using (var noHeader = Client())
        {
            await noHeader.Login("newowner", "Str0ngPass!");
            noHeader.AppHeader = false;
            Check((await noHeader.Send(HttpMethod.Put, "/api/Admin/owner-profile", JsonContent.Create(new { ownerName = "X", emailAddress = "x@gmail.com" }))).StatusCode == HttpStatusCode.BadRequest,
                "Account writes require the application's request header");
        }
        var profileSaved = await owner.Send(HttpMethod.Put, "/api/Admin/owner-profile", JsonContent.Create(new { ownerName = "Renamed Owner", emailAddress = "renamed@gmail.com" }));
        Check(profileSaved.StatusCode == HttpStatusCode.OK && (await profileSaved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ownerName").GetString() == "Renamed Owner", "Saving the owner profile persists the name and email");
        Check((await owner.Send(HttpMethod.Put, "/api/Admin/owner-profile", JsonContent.Create(new { ownerName = "Renamed Owner", emailAddress = "renamed@example.com" }))).StatusCode == HttpStatusCode.BadRequest, "A non-Gmail profile email is rejected");

        using var otherDevice = Client();
        await otherDevice.Login("newowner", "Str0ngPass!");
        Check((await owner.Send(HttpMethod.Post, "/api/Admin/change-password", JsonContent.Create(new { currentPassword = "nope", newPassword = "AnotherPass1!", confirmPassword = "AnotherPass1!" }))).StatusCode == HttpStatusCode.BadRequest, "Changing the password needs the correct current password");
        Check((await owner.Send(HttpMethod.Post, "/api/Admin/change-password", JsonContent.Create(new { currentPassword = "Str0ngPass!", newPassword = "AnotherPass1!", confirmPassword = "different" }))).StatusCode == HttpStatusCode.BadRequest, "A mismatched password confirmation is rejected");
        Check((await owner.Send(HttpMethod.Post, "/api/Admin/change-password", JsonContent.Create(new { currentPassword = "Str0ngPass!", newPassword = "AnotherPass1!", confirmPassword = "AnotherPass1!" }))).StatusCode == HttpStatusCode.OK, "A valid password change succeeds");
        Check((await owner.Send(HttpMethod.Get, "/api/Admin/session")).StatusCode == HttpStatusCode.Unauthorized, "The changing device is signed out after a password change");
        Check((await otherDevice.Send(HttpMethod.Get, "/api/Admin/session")).StatusCode == HttpStatusCode.Unauthorized, "Other signed-in devices are signed out after a password change");
        using var afterPassword = Client();
        Check((await afterPassword.Login("newowner", "Str0ngPass!")).StatusCode == HttpStatusCode.Unauthorized && (await afterPassword.Login("newowner", "AnotherPass1!")).StatusCode == HttpStatusCode.OK, "Only the new password signs in");

        Check((await afterPassword.Send(HttpMethod.Post, "/api/Admin/change-username", JsonContent.Create(new { currentPassword = "AnotherPass1!", newUserName = "bad name!" }))).StatusCode == HttpStatusCode.BadRequest, "An invalid new username is rejected");
        Check((await afterPassword.Send(HttpMethod.Post, "/api/Admin/change-username", JsonContent.Create(new { currentPassword = "wrong", newUserName = "renamed.owner" }))).StatusCode == HttpStatusCode.BadRequest, "Changing the username needs the correct password");
        // Seeded directly: the signup route is deliberately rate limited per address (10 attempts / 10 minutes).
        await db.ExecuteAsync("INSERT dbo.SalonOwner(SalonOwnerID, SalonMasterID, OwnerName, UserName, PasswordHash) VALUES(NEWID(), @seededSalon, N'Other', N'taken.name', N'x')", new { seededSalon });
        Check((await afterPassword.Send(HttpMethod.Post, "/api/Admin/change-username", JsonContent.Create(new { currentPassword = "AnotherPass1!", newUserName = "taken.name" }))).StatusCode == HttpStatusCode.Conflict, "A username already in use is rejected (409)");
        Check((await afterPassword.Send(HttpMethod.Post, "/api/Admin/change-username", JsonContent.Create(new { currentPassword = "AnotherPass1!", newUserName = "renamed.owner" }))).StatusCode == HttpStatusCode.OK, "A valid username change succeeds");
        using var afterRename = Client();
        Check((await afterRename.Login("newowner", "AnotherPass1!")).StatusCode == HttpStatusCode.Unauthorized && (await afterRename.Login("renamed.owner", "AnotherPass1!")).StatusCode == HttpStatusCode.OK, "Only the new username signs in, with the same password");

        // ---------------- start an appointment ----------------
        var salonId = (Guid)row.SalonMasterID;
        var morning = Guid.NewGuid();
        await db.ExecuteAsync("INSERT dbo.TimeAllocation VALUES(@morning, '10:00:00')", new { morning });
        async Task<Guid> Booking(Guid forSalon, DateTime date, string status = "Pending", int confirmed = 2, bool timed = true)
        {
            var id = Guid.NewGuid();
            await db.ExecuteAsync("INSERT dbo.SeatBookingDetails(SeatBookingDetailsID,SalonMasterID,BookingStatusMasterID,BookingDate,SeatCount,CompletionStatus) VALUES(@id,@forSalon,@confirmed,@date,1,@status)",
                new { id, forSalon, confirmed, date, status });
            if (timed) await db.ExecuteAsync("INSERT dbo.CustomerTimeSelection VALUES(@id,@morning)", new { id, morning });
            return id;
        }
        var ist = DateTime.UtcNow.AddMinutes(330).Date;
        var past = await Booking(salonId, ist.AddDays(-1));
        var future = await Booking(salonId, ist.AddDays(2));
        var done = await Booking(salonId, ist.AddDays(-1), status: "Completed");
        var foreign = await Booking(seededSalon, ist.AddDays(-1));
        var unconfirmed = await Booking(salonId, ist.AddDays(-1), confirmed: 1);
        var untimed = await Booking(salonId, ist.AddDays(-1), timed: false);
        Check((await anonymous.Send(HttpMethod.Post, $"/api/Admin/appointments/{past}/start")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous users cannot start appointments");
        var started = await afterRename.Send(HttpMethod.Post, $"/api/Admin/appointments/{past}/start");
        var startedJson = await started.Content.ReadFromJsonAsync<JsonElement>();
        Check(started.StatusCode == HttpStatusCode.OK && startedJson.GetProperty("bookingStatus").GetString() == "InProgress" && startedJson.GetProperty("wasAlreadyStarted").GetBoolean() == false, "A due appointment moves to InProgress");
        Check(await db.ExecuteScalarAsync<string>("SELECT CompletionStatus FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@past", new { past }) == "InProgress"
            && await db.ExecuteScalarAsync<Guid?>("SELECT StartedByOwnerID FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@past", new { past }) is not null, "The status and who started it are saved");
        var again = await (await afterRename.Send(HttpMethod.Post, $"/api/Admin/appointments/{past}/start")).Content.ReadFromJsonAsync<JsonElement>();
        Check(again.GetProperty("wasAlreadyStarted").GetBoolean(), "Starting twice is harmless and reports it was already started");
        Check((await afterRename.Send(HttpMethod.Post, $"/api/Admin/appointments/{future}/start")).StatusCode == HttpStatusCode.Conflict, "An appointment cannot start before its booked time (409)");
        Check((await afterRename.Send(HttpMethod.Post, $"/api/Admin/appointments/{done}/start")).StatusCode == HttpStatusCode.Conflict, "A completed booking cannot be started (409)");
        Check((await afterRename.Send(HttpMethod.Post, $"/api/Admin/appointments/{foreign}/start")).StatusCode == HttpStatusCode.NotFound, "Another salon's booking cannot be started (404)");
        Check((await afterRename.Send(HttpMethod.Post, $"/api/Admin/appointments/{unconfirmed}/start")).StatusCode == HttpStatusCode.NotFound, "An unconfirmed booking cannot be started (404)");
        Check((await afterRename.Send(HttpMethod.Post, $"/api/Admin/appointments/{untimed}/start")).StatusCode == HttpStatusCode.Conflict, "A booking without a time slot cannot be started (409)");
        Check((await afterRename.Send(HttpMethod.Post, $"/api/Admin/appointments/{Guid.NewGuid()}/start")).StatusCode == HttpStatusCode.NotFound, "An unknown booking is not found (404)");
        Check(await db.ExecuteScalarAsync<string>("SELECT CompletionStatus FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@foreign", new { foreign }) == "Pending", "Rejected starts change nothing");
        await RunScript("Database/SalonOwnerAppointmentStart.sql");
        Check(await db.ExecuteScalarAsync<string>("SELECT CompletionStatus FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@past", new { past }) == "InProgress", "Rerunning the start script keeps existing statuses");

        // ---------------- mandatory-update policy ----------------
        var policy = await anonymous.GetJson<JsonElement>("/api/AppUpdates/salonexa-admin/android");
        Check(policy.GetProperty("applicationId").GetString() == "com.salonexa.admin" && policy.GetProperty("latestVersionCode").GetInt32() == 7
            && policy.GetProperty("latestVersion").GetString() == "1.1.2", "The public app-update policy is served");

        // ---------------- signup abuse limit ----------------
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 12 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using var form = Signup(user: "bulk" + i, salon: "Bulk " + i, accept: false);
            last = (await anonymous.Send(HttpMethod.Post, "/api/Admin/signup", form)).StatusCode;
            Console.WriteLine($"  attempt {i}: {(int)last}");
        }
        Check(last == HttpStatusCode.TooManyRequests, "Repeated signup attempts from one address are rate limited (429)");
        Console.WriteLine($"{checks} admin owner-flow checks passed.");
    }
    finally
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        SqlConnection.ClearAllPools();
        await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        // Only remove this generated fixture directory directly under the temporary root.
        if (Directory.GetParent(fixtureRoot)?.FullName == tempRoot && database.StartsWith("SalonexaOwnerFlowChecks_"))
            Directory.Delete(fixtureRoot, recursive: true);
    }
}

// Keeps Secure owner cookies across loopback HTTP requests, like the admin app's credentials: 'include'.
sealed class OwnerClient(Uri address) : IDisposable
{
    private static readonly Uri Origin = new("https://localhost");
    private readonly HttpClient http = new(new HttpClientHandler { UseCookies = false }) { BaseAddress = address };
    private readonly System.Net.CookieContainer cookies = new();
    public bool AppHeader { get; set; } = true;

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(Origin));
        if (AppHeader) request.Headers.Add("X-Salonexa-Request", "1");
        var response = await http.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values) cookies.SetCookies(Origin, value);
        return response;
    }

    public Task<HttpResponseMessage> Login(string userName, string password) =>
        Send(HttpMethod.Post, "/api/Admin/login", JsonContent.Create(new { userName, password }));

    public async Task<T?> GetJson<T>(string path)
    {
        using var response = await Send(HttpMethod.Get, path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public Task<byte[]> GetBytes(string path) => http.GetByteArrayAsync(path);
    public void Dispose() => http.Dispose();
}
