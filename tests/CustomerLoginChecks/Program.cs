using System.Collections.Concurrent;
using System.Diagnostics;
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

// Only a disposable database on local SQL Server is modified. No SMS/push
// workers or production API are started, and connection settings are not printed.
var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
    root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")).Build();
var settings = new SqlConnectionStringBuilder(config.GetConnectionString("dbconnection"));
if (!new[] { ".", "localhost", "127.0.0.1", "(local)", "(localdb)", Environment.MachineName }
    .Contains(settings.DataSource.Split('\\', ',')[0], StringComparer.OrdinalIgnoreCase))
    throw new Exception("Login checks only connect to local SQL Server.");
settings.InitialCatalog = "master";
settings.ConnectTimeout = 5;
await using var master = new SqlConnection(settings.ConnectionString);
await master.OpenAsync();
var database = "SalonexaLoginChecks_" + Guid.NewGuid().ToString("N");
await master.ExecuteAsync($"CREATE DATABASE [{database}]");
settings.InitialCatalog = database;
await using var db = new SqlConnection(settings.ConnectionString);
WebApplication? app = null;
var checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS: " + label);
}
try
{
    await db.OpenAsync();
    await db.ExecuteAsync("""
        CREATE TABLE dbo.CustomerRegistration(
            CustomerID uniqueidentifier NOT NULL PRIMARY KEY,
            MobileNo nvarchar(25), FirstName nvarchar(100), EmailAddress nvarchar(254),
            GeneralAddress nvarchar(max), ImageName nvarchar(300),
            CreatedOn datetime, CreatedBy uniqueidentifier, IsDeleted bit NULL);
        """);
    await db.ExecuteAsync("""
        CREATE PROCEDURE dbo.customerLoginButtomClick
            @CustomerID uniqueidentifier, @MobileNo nvarchar(25), @GeneralAddress nvarchar(max)
        AS
        INSERT dbo.CustomerRegistration(CustomerID,MobileNo,GeneralAddress,CreatedOn,CreatedBy)
        VALUES(@CustomerID,@MobileNo,@GeneralAddress,GETDATE(),@CustomerID);
        """);
    var indexScript = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "CustomerLoginIndex.sql"));
    for (var repeat = 0; repeat < 2; repeat++)
        foreach (var batch in Regex.Split(indexScript, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE name='IX_CustomerRegistration_MobileNo' AND is_disabled=0") == 1,
        "Existing index script is repeatable and enables the mobile-number index");

    var logs = new CapturedLogs();
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(logs);
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:dbconnection"] = settings.ConnectionString });
    builder.Services.AddControllers().AddApplicationPart(typeof(LoginController).Assembly);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<LoginService>();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<CustomerSessionRegistry>();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddSession(options => options.Cookie.Name = ".CustomerLoginChecks");
    app = builder.Build();
    app.UseRouting();
    app.UseSession();
    app.UseMiddleware<CustomerApiSessionMiddleware>();
    app.MapControllers();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
    using var http = new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
    http.DefaultRequestHeaders.Add("X-Salonexa-Request", "1");
    async Task<HttpResponseMessage> Login(string mobile, string? addressValue = null) =>
        await http.PostAsJsonAsync("/api/Login/customerLoginButtomClick", new { MobileNo = mobile, Address = addressValue });
    async Task<bool> SessionActive()
    {
        using var response = await http.GetAsync("/api/Login/session");
        return response.StatusCode == HttpStatusCode.OK;
    }

    Check(!await SessionActive(), "Anonymous session is rejected");
    using (var invalid = await Login("123"))
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid mobile number is rejected before SQL work");
    Check(logs.Messages.IsEmpty, "Invalid login does not open a database connection");
    using (var plain = new HttpClient { BaseAddress = new Uri(address) })
    using (var response = await plain.PostAsJsonAsync("/api/Login/customerLoginButtomClick", new { MobileNo = "9876543210" }))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Application request header remains required");

    using (var created = await Login("9876543210", "Original private address"))
    {
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Check(created.IsSuccessStatusCode && body.GetProperty("item1").GetString() == "Success"
            && body.GetProperty("item2").GetArrayLength() == 0, "New customer login preserves the existing response contract");
        Check(created.Headers.TryGetValues("Server-Timing", out var timings) && timings.Single().StartsWith("customer-login;dur="),
            "Login exposes API duration for comparison with browser request time");
    }
    Check(await SessionActive(), "New login establishes an authenticated session");
    var originalId = await db.ExecuteScalarAsync<Guid>("SELECT CustomerID FROM dbo.CustomerRegistration WHERE MobileNo='9876543210'");
    foreach (var suppliedAddress in new string?[] { null, "", "Stale cached address" })
    {
        using var response = await Login("9876543210", suppliedAddress);
        Check(response.IsSuccessStatusCode && await SessionActive(), "Existing and remembered customers can sign in");
        Check(await db.ExecuteScalarAsync<string>("SELECT GeneralAddress FROM dbo.CustomerRegistration WHERE CustomerID=@originalId", new { originalId }) == "Original private address",
            "Sign-in preserves the saved address for omitted, empty and stale address payloads");
    }
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CustomerRegistration WHERE MobileNo='9876543210'") == 1,
        "Existing customer login reuses the identity without another insert");

    await db.ExecuteAsync("UPDATE dbo.CustomerRegistration SET IsDeleted=1 WHERE CustomerID=@originalId", new { originalId });
    using (var response = await Login("9876543210"))
        Check(response.IsSuccessStatusCode, "Soft-deleted customers are excluded using the legacy lookup rule");
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CustomerRegistration WHERE MobileNo='9876543210'") == 2,
        "A deleted identity is not restored as the active customer");

    // Warm local HTTP/SQL measurements are observations, not a production speed guarantee.
    var samples = new List<double>();
    for (var attempt = 0; attempt < 5; attempt++)
    {
        var clock = Stopwatch.StartNew();
        using var response = await Login("9876543210");
        Check(response.IsSuccessStatusCode, "Repeated sign-in succeeds");
        samples.Add(clock.Elapsed.TotalMilliseconds);
    }
    Console.WriteLine($"Local warm login: min={samples.Min():F1}ms average={samples.Average():F1}ms max={samples.Max():F1}ms");

    await using (var blocker = new SqlConnection(settings.ConnectionString))
    {
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CustomerRegistration WITH (TABLOCKX,HOLDLOCK)", transaction: transaction);
        var clock = Stopwatch.StartNew();
        using var response = await Login("9876543210");
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && clock.Elapsed < TimeSpan.FromSeconds(9),
            "Blocked SQL login returns 503 within the finite login budget");
        Check(!await SessionActive(), "A failed login cannot retain an authenticated session");
        await transaction.RollbackAsync();
    }
    using (var response = await Login("9876543210"))
        Check(response.IsSuccessStatusCode && await SessionActive(), "Explicit retry succeeds after SQL blocking clears");
    using (var response = await http.PostAsJsonAsync("/api/Login/logout", new { }))
        Check(response.IsSuccessStatusCode && !await SessionActive(), "Logout still revokes the authenticated session");

    Check(logs.Messages.Any(message => message.Contains("database timing:") && message.Contains("OpenMs=") && message.Contains("LookupMs=") && message.Contains("InsertMs=")),
        "Database connection, lookup, insert and total durations are logged");
    Check(logs.Messages.Any(message => message.Contains("API timing:") && message.Contains("Outcome=sql-timeout")),
        "API timing identifies SQL timeouts");
    Check(!logs.Messages.Any(message => message.Contains("9876543210") || message.Contains("Original private address")
        || message.Contains("Stale cached address") || message.Contains(originalId.ToString())),
        "Login timing logs contain no mobile number, address or customer identity");
    Console.WriteLine($"PASS: {checks} customer login checks.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    await db.CloseAsync();
    SqlConnection.ClearAllPools();
    await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
}

sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CapturedLogger(
        categoryName == typeof(LoginController).FullName || categoryName == typeof(LoginService).FullName ? Messages : null);
    public void Dispose() { }
    private sealed class CapturedLogger(ConcurrentQueue<string>? messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => messages is not null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages?.Enqueue(formatter(state, exception));
    }
}
