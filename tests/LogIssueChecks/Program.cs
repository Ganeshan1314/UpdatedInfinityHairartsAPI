using System.Collections.Concurrent;
using System.Diagnostics;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;

var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln"))) root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")).Build();
var settings = new SqlConnectionStringBuilder(config.GetConnectionString("dbconnection"));
if (!new[] { ".", "localhost", "127.0.0.1", "(localdb)", Environment.MachineName }.Contains(settings.DataSource.Split('\\', ',')[0], StringComparer.OrdinalIgnoreCase))
    throw new Exception("These checks only connect to local SQL Server.");
settings.Encrypt = SqlConnectionEncryptOption.Optional;
if (args.Contains("--audit"))
{
    // This mode only reads procedure metadata in the configured local database.
    await using var audit = new SqlConnection(settings.ConnectionString);
    var rows = await audit.QueryAsync(await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Verify_LoggedIssues_2026-11-05.sql")));
    foreach (var row in rows)
    {
        Console.WriteLine($"{row.Check}: {row.Status}");
        if ((string)row.Status != "READY") Environment.ExitCode = 1;
    }
    return;
}
settings.InitialCatalog = "master";
await using var master = new SqlConnection(settings.ConnectionString);
await master.OpenAsync();
var database = "SalonexaLogIssueChecks_" + Guid.NewGuid().ToString("N");
await master.ExecuteAsync($"CREATE DATABASE [{database}]");
settings.InitialCatalog = database;
await using var db = new SqlConnection(settings.ConnectionString);
WebApplication? app = null;
try
{
    await db.OpenAsync();
    async Task Migrate(string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", file));
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
    }
    await Migrate("LocationMaster.sql");
    await db.ExecuteAsync("""
        CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, SalonName nvarchar(200), IsActive bit,
            LocationMasterID uniqueidentifier, SalonAddress nvarchar(500), ContactNumber nvarchar(20), IsVisibleToCustomers bit);
        CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier,
            OwnerName nvarchar(150), UserName nvarchar(100), EmailAddress nvarchar(254), IsActive bit);
        """);
    await Migrate("SalonOwnerProfileRead.sql"); await Migrate("SalonOwnerProfileRead.sql");
    await Migrate("CustomerSalonDirectory.sql"); await Migrate("CustomerSalonDirectory.sql");
    var owner = Guid.NewGuid(); var salon = Guid.NewGuid(); var otherOwner = Guid.NewGuid();
    await db.ExecuteAsync("""
        INSERT dbo.SalonMaster SELECT @salon,N'Fixture salon',1,LocationMasterID,N'Fixture address',N'',1 FROM dbo.LocationMaster;
        INSERT dbo.SalonOwner VALUES(@owner,@salon,N'Fixture owner',N'fixture-owner',N'fixture@example.com',1),
            (@otherOwner,@salon,N'Other owner',N'other-owner',N'other@example.com',1);
        """, new { owner, salon, otherOwner });
    var checks = 0;
    void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
    var logs = new CapturedLogs();
    var imageRoot = Path.Combine(AppContext.BaseDirectory, "image-fixture");
    var imageDirectory = Path.Combine(imageRoot, "Images", "HairCutItemListImages");
    Directory.CreateDirectory(imageDirectory);
    foreach (var image in Directory.GetFiles(Path.Combine(root, "InfinityHairartsAPI", "Images", "HairCutItemListImages")))
        File.Copy(image, Path.Combine(imageDirectory, Path.GetFileName(image)), overwrite: true);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development", ContentRootPath = imageRoot });
    builder.Logging.ClearProviders(); builder.Logging.AddProvider(logs);
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:dbconnection"] = settings.ConnectionString,
        ["Authentication:KeyRingPath"] = Path.Combine(AppContext.BaseDirectory, "fixture-keys")
    });
    builder.Services.AddControllers().AddApplicationPart(typeof(ClientErrorsController).Assembly);
    builder.Services.AddClientErrorReporting();
    builder.Services.AddDistributedMemoryCache(); builder.Services.AddSession();
    builder.Services.AddSingleton<ISalonOwnerAuthenticationService>(new FixtureOwners(owner, salon));
    builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration, builder.Environment);
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddLocationCatalog();
    app = builder.Build();
    app.Use((context, next) => { context.Request.Scheme = "https"; return next(context); });
    app.UseServiceImages();
    app.UseRouting(); app.UseRateLimiter(); app.UseSession(); app.UseMiddleware<SalonOwnerSessionMiddleware>();
    app.MapControllers();
    app.MapPost("/fixture/login", async (HttpContext context, SalonOwnerPersistentSignIn signIn, ISalonOwnerAuthenticationService owners) =>
        await signIn.SignInAsync(context, (await owners.GetActiveSessionOwnerAsync(owner, default))!));
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    var directory = await client.GetFromJsonAsync<SalonDirectoryPage>("/api/Location/salons");
    Check(directory!.Items.Count == 1 && directory.Items[0].SalonMasterID == salon,
        "The logged salon directory route succeeds anonymously with the published SQL contract");
    const string imageRoute = "/Images/HairCutItemListImages/";
    var imageNames = new[] { "86712ea3-8bde-4d04-8543-45cd46dff989.jpg", "62593614-45f0-4820-a4ae-812eb453b1bd.jpg",
        "557b2117-bd6f-4a3b-a3e1-f85ee0c5f61c.jpg", "98988429-0294-4c25-9598-fd98e8d69af6.jpg" };
    foreach (var image in imageNames)
    {
        using var response = await client.GetAsync(imageRoute + image);
        Check(response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "image/jpeg"
            && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(imageDirectory, image))),
            "The service image returns the original file bytes: " + image);
    }
    using var rawClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = client.BaseAddress };
    foreach (var image in new[] { "2e84001c-3dd3-4734-94e5-11085e968f7f.jpg", "1a7548b5-7b4e-4d02-af7b-3b006602cbba.jpg" })
    {
        using var response = await rawClient.GetAsync(imageRoute + image);
        Check(response.StatusCode == HttpStatusCode.Found && response.Headers.CacheControl?.NoStore == true
            && response.Headers.Location?.ToString() == imageRoute + "service-placeholder.svg",
            "An unavailable original redirects without caching: " + image);
        using var fallback = await client.GetAsync(imageRoute + image);
        Check(fallback.IsSuccessStatusCode && fallback.Content.Headers.ContentType?.MediaType == "image/svg+xml"
            && (await fallback.Content.ReadAsStringAsync()).Contains("Service image unavailable"),
            "A missing service photo displays a neutral image fallback");
    }
    using (var head = await rawClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, imageRoute + "2e84001c-3dd3-4734-94e5-11085e968f7f.jpg")))
        Check(head.StatusCode == HttpStatusCode.Found && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "Missing image HEAD requests redirect without a response body");
    foreach (var path in new[] { imageRoute + ".env", imageRoute + "missing.json", "/appsettings.json" })
    {
        using var response = await client.GetAsync(path);
        Check(response.StatusCode == HttpStatusCode.NotFound, "Image serving does not expose non-image paths: " + path);
    }
    var restored = Guid.NewGuid().ToString("D") + ".jpg";
    var restoredPath = Path.Combine(imageDirectory, restored);
    try
    {
        using (var missing = await rawClient.GetAsync(imageRoute + restored)) Check(missing.StatusCode == HttpStatusCode.Found, "An unavailable upload initially uses the fallback");
        await File.WriteAllBytesAsync(restoredPath, await File.ReadAllBytesAsync(Path.Combine(imageDirectory, imageNames[0])));
        using var response = await rawClient.GetAsync(imageRoute + restored);
        Check(response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "image/jpeg", "Restoring an original immediately replaces the fallback");
    }
    finally { File.Delete(restoredPath); }
    using (var response = await client.GetAsync("/api/Admin/owner-profile"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "The profile route rejects anonymous requests");
    var locations = await client.GetFromJsonAsync<LocationMasterModal[]>("/api/Location/getLocations");
    var signup = await client.GetFromJsonAsync<LocationMasterModal[]>("/api/Admin/signup-locations");
    Check(locations!.Length > 0 && locations.Select(x => x.LocationMasterID).SequenceEqual(signup!.Select(x => x.LocationMasterID)),
        "Signup locations are public and match the existing active location list");
    using (var login = await client.PostAsync("/fixture/login", null))
    {
        var cookies = new CookieContainer(); var origin = new Uri("https://localhost");
        foreach (var cookie in login.Headers.GetValues("Set-Cookie")) cookies.SetCookies(origin, cookie);
        client.DefaultRequestHeaders.Add("Cookie", cookies.GetCookieHeader(origin));
    }
    using (var response = await client.GetAsync($"/api/Admin/owner-profile?ownerID={otherOwner}"))
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Check(response.IsSuccessStatusCode && body.GetProperty("emailAddress").GetString() == "fixture@example.com",
            "The profile uses the authenticated owner and ignores caller-supplied owner IDs");
        Check(response.Headers.CacheControl?.NoStore == true && body.GetProperty("readOnly").GetBoolean()
            && !body.GetProperty("canChangeUserName").GetBoolean(), "Profile responses disable caching and unsupported account edits");
    }
    Check(!(await db.QueryAsync("dbo.GetSalonOwnerProfile", new { OwnerID = owner, SalonID = Guid.NewGuid() }, commandType: CommandType.StoredProcedure)).Any(),
        "Profile SQL rejects a mismatched salon");
    await db.ExecuteAsync("UPDATE dbo.SalonOwner SET IsActive=0 WHERE SalonOwnerID=@owner", new { owner });
    Check(!(await db.QueryAsync("dbo.GetSalonOwnerProfile", new { OwnerID = owner, SalonID = salon }, commandType: CommandType.StoredProcedure)).Any(),
        "Profile SQL excludes inactive owners");

    client.DefaultRequestHeaders.Remove("Cookie");
    object Report(string endpoint = "/Admin/services", string stack = "main.js:12:34", string errorType = "TypeError") => new {
        reportId = Guid.NewGuid(), occurredAt = DateTimeOffset.UtcNow, category = "http", page = "/home",
        endpoint, method = "GET", status = 404, errorType, code = "NG04002", stack, platform = "android"
    };
    foreach (var payload in new object[] { new { reports = new[] { Report() } }, new { application = "SalonexaAdmin", reports = new[] { Report() } } })
    {
        using var response = await client.PostAsJsonAsync("/api/ClientErrors", payload);
        Check(response.StatusCode == HttpStatusCode.NoContent, "Client error batches from both apps are accepted without authentication");
    }
    var marker = "PRIVATE_FORM_VALUE";
    using (var response = await client.PostAsJsonAsync("/api/ClientErrors", new { reports = new[] { Report("/Admin/services?email=" + marker, "TypeError: " + marker + "\nmain.js:12:34", marker + "\nforged-log") } }))
        Check(response.IsSuccessStatusCode && logs.Messages.Any(x => x.Contains("main.js:12:34")) && !logs.Messages.Any(x => x.Contains(marker)),
            "Client reporting strips query strings, exception text and log injection");
    using (var response = await client.PostAsJsonAsync("/api/ClientErrors", new { reports = new[] { Report("/Admin/services\n", errorType: "TypeError\n") } }))
        Check(response.IsSuccessStatusCode && logs.Messages.Where(x => x.StartsWith("Client error ")).All(x => !x.Contains('\n') && !x.Contains('\r')),
            "Trailing newlines cannot forge server log entries");
    foreach (var payload in new object[] { new { reports = Array.Empty<object>() }, new { reports = Enumerable.Range(0, 11).Select(_ => Report()).ToArray() }, new { reports = new object?[] { null } } })
    {
        using var response = await client.PostAsJsonAsync("/api/ClientErrors", payload);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Empty, oversized and null-element report batches are rejected");
    }
    using (var response = await client.PostAsJsonAsync("/api/ClientErrors", new { reports = new[] { Report(stack: new string('x', 1600)) } }))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Oversized report fields are rejected");
    var throttled = false;
    for (var count = 0; count < 31; count++)
    {
        using var response = await client.PostAsJsonAsync("/api/ClientErrors", new { reports = new[] { Report() } });
        if (response.StatusCode == HttpStatusCode.TooManyRequests) { throttled = true; break; }
    }
    Check(throttled, "Client reporting enforces its per-IP request limit");
    var publishedIndex = Array.IndexOf(args, "--published");
    if (publishedIndex >= 0)
    {
        var publishPath = Path.GetFullPath(args[publishedIndex + 1]);
        Check(!Directory.EnumerateFiles(publishPath, "appsettings*.json").Any()
            && !Directory.EnumerateFiles(publishPath, "*firebase-adminsdk*.json").Any(), "Publishing preserves server configuration and does not ship credentials");
        foreach (var image in Directory.GetFiles(Path.Combine(root, "InfinityHairartsAPI", "Images", "HairCutItemListImages")))
            Check(File.Exists(Path.Combine(publishPath, "Images", "HairCutItemListImages", Path.GetFileName(image)))
                && (await File.ReadAllBytesAsync(image)).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(publishPath, "Images", "HairCutItemListImages", Path.GetFileName(image)))),
                "The release includes the original image bytes: " + Path.GetFileName(image));
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = "http://127.0.0.1:" + port;
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = publishPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "InfinityHairartsAPI.dll", "--urls", url, "--environment", "Development" }) start.ArgumentList.Add(argument);
        start.Environment["ConnectionStrings__dbconnection"] = settings.ConnectionString;
        start.Environment["Authentication__KeyRingPath"] = Path.Combine(AppContext.BaseDirectory, "published-test-keys");
        start.Environment["FileLogging__Enabled"] = "false";
        start.Environment["Fcm__ProjectId"] = "";
        start.Environment["Fcm__ServiceAccountJson"] = "";
        var output = new ConcurrentBag<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => { if (line.Data is not null) output.Add(line.Data); };
        process.ErrorDataReceived += (_, line) => { if (line.Data is not null) output.Add(line.Data); };
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            using var publishedClient = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(2) };
            var ready = false;
            for (var attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                try { using var response = await publishedClient.GetAsync("/api/Login/health"); ready = response.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                if (ready) break;
                await Task.Delay(100);
            }
            if (!ready) Console.Error.WriteLine(string.Join(Environment.NewLine, output));
            Check(ready, "The published API starts with the fixture database and without Firebase credentials");
            var verify = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools", "Test-LogIssueDeployment.ps1"), "-BaseUrl", url }) verify.ArgumentList.Add(argument);
            using var verification = Process.Start(verify)!;
            var stdout = verification.StandardOutput.ReadToEndAsync(); var stderr = verification.StandardError.ReadToEndAsync();
            await verification.WaitForExitAsync();
            Console.Write(await stdout); Console.Error.Write(await stderr);
            Check(verification.ExitCode == 0, "The deployment verification script passes against the actual published API");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
    Console.WriteLine($"Passed {checks} log issue SQL/HTTP checks. No application database data or live pushes were changed.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    await db.CloseAsync(); SqlConnection.ClearPool(db);
    await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
}

sealed class FixtureOwners(Guid owner, Guid salon) : ISalonOwnerAuthenticationService
{
    public Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(id == owner
        ? new SalonOwnerLoginResponse { SalonOwnerID = owner, SalonMasterID = salon, AuthenticationStamp = "fixture-stamp", OwnerName = "Fixture owner", UserName = "fixture-owner", SalonName = "Fixture salon" } : null);
    public Task<SalonOwnerLoginResponse?> AuthenticateAsync(SalonOwnerLoginRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SalonOwnerLoginResponse> InitializeAsync(InitializeSalonOwnerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentBag<string> Messages { get; } = [];
    public ILogger CreateLogger(string categoryName) => new Capture(Messages);
    public void Dispose() { }
    sealed class Capture(ConcurrentBag<string> messages) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }
}
