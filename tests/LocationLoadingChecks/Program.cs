using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

// Deterministic cache checks: no SQL or wall-clock waits for expiration.
var clock = new FixtureClock();
using (var memory = new MemoryCache(new MemoryCacheOptions { Clock = clock }))
using (var cache = new LocationCatalogCache(memory))
{
    IReadOnlyList<LocationMasterModal> items = [new() { LocationMasterID = Guid.NewGuid(), LocationName = "Fixture town" }];
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var loads = 0;
    async Task<IReadOnlyList<LocationMasterModal>> Load(CancellationToken token)
    {
        Interlocked.Increment(ref loads);
        await release.Task.WaitAsync(token);
        return items;
    }
    var requests = Enumerable.Range(0, 12).Select(_ => cache.GetAsync(Load)).ToArray();
    Check(loads == 1, "Concurrent cache misses start only one fetch");
    using var cancelledWaiter = new CancellationTokenSource();
    var cancelled = cache.GetAsync(Load, cancelledWaiter.Token);
    cancelledWaiter.Cancel();
    try { await cancelled; throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check(loads == 1, "Cancelling a waiting caller leaves the active fetch running"); }
    release.SetResult();
    var results = await Task.WhenAll(requests);
    Check(results.Count(item => !item.CacheHit) == 1 && loads == 1, "All waiting callers reuse the one successful snapshot");
    clock.UtcNow += TimeSpan.FromSeconds(119);
    Check((await cache.GetAsync(Load)).CacheHit, "The snapshot stays fresh for less than two minutes");
    clock.UtcNow += TimeSpan.FromSeconds(2);
    Check(!(await cache.GetAsync(Load)).CacheHit && loads == 2, "The snapshot expires after two minutes");
    clock.UtcNow += TimeSpan.FromMinutes(3);
    try
    {
        await cache.GetAsync(_ => throw new InvalidOperationException("Fixture fetch failed"));
        throw new Exception("Expected fetch failure");
    }
    catch (InvalidOperationException) { }
    Check(!(await cache.GetAsync(Load)).CacheHit && loads == 3, "Failures release the gate and are never cached");
    clock.UtcNow += TimeSpan.FromMinutes(3);
    using var activeCancellation = new CancellationTokenSource();
    var active = cache.GetAsync(async token =>
    {
        await Task.Delay(Timeout.Infinite, token);
        return items;
    }, activeCancellation.Token);
    activeCancellation.Cancel();
    try { await active; throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
    Check(!(await cache.GetAsync(Load)).CacheHit && loads == 4, "A cancelled fetch is not cached and a later caller can retry");
    clock.UtcNow += TimeSpan.FromMinutes(3);
    await cache.GetAsync(_ => Task.FromResult<IReadOnlyList<LocationMasterModal>>([]));
    Check((await cache.GetAsync(Load)).Items.Count == 0 && loads == 4, "An empty successful location list is cached");
}

// Only a disposable local database is modified. Production settings and data
// are never changed; no background workers, login, SMS or push services run.
var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
    root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")).Build();
var settings = new SqlConnectionStringBuilder(configuration.GetConnectionString("dbconnection"));
if (!new[] { ".", "localhost", "127.0.0.1", "(local)", "(localdb)", Environment.MachineName }
    .Contains(settings.DataSource.Split('\\', ',')[0], StringComparer.OrdinalIgnoreCase))
    throw new Exception("Location checks only connect to local SQL Server.");
settings.Encrypt = SqlConnectionEncryptOption.Optional;
settings.ConnectTimeout = 5;
settings.InitialCatalog = "master";
await using var master = new SqlConnection(settings.ConnectionString);
await master.OpenAsync();
var database = "SalonexaLocationChecks_" + Guid.NewGuid().ToString("N");
await master.ExecuteAsync($"CREATE DATABASE [{database}]");
settings.InitialCatalog = database;
await using var db = new SqlConnection(settings.ConnectionString);
WebApplication? app = null;
try
{
    await db.OpenAsync();
    var migration = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "LocationMaster.sql"));
    foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
    await db.ExecuteAsync("""
        INSERT dbo.LocationMaster(LocationName, IsActive, DisplayOrder)
        VALUES(N'Private fixture active town',1,2), (N'Private fixture inactive town',0,3);
        CREATE TABLE dbo.FetchCount (Value int NOT NULL);
        INSERT dbo.FetchCount VALUES(0);
        """);
    var sqlUpdate = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "SQL_Update_LocationLoading_Performance_2026-11-07.sql"));
    var originalProcedure = await db.ExecuteScalarAsync<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.getActiveLocations'))");
    var originalLocations = (await db.QueryAsync<string>("SELECT CONVERT(nvarchar(36),LocationMasterID) + '|' + LocationName + '|' + CONVERT(nvarchar(1),IsActive) + '|' + CONVERT(nvarchar(12),DisplayOrder) FROM dbo.LocationMaster ORDER BY LocationMasterID")).ToArray();
    await db.ExecuteAsync(sqlUpdate);
    await db.ExecuteAsync(sqlUpdate);
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.LocationMaster') AND name='IX_LocationMaster_Active_DisplayOrder_Name'") == 0,
        "The repeatable SQL update defaults to verification without creating an index");
    var applyIndex = sqlUpdate.Replace("DECLARE @ApplyOptionalIndex bit = 0;", "DECLARE @ApplyOptionalIndex bit = 1;");
    await db.ExecuteAsync(applyIndex);
    await db.ExecuteAsync(applyIndex);
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.LocationMaster') AND name='IX_LocationMaster_Active_DisplayOrder_Name' AND is_disabled=0") == 1,
        "Opting in creates exactly one enabled location index across repeated runs");
    await db.ExecuteAsync("EXEC sys.sp_rename 'dbo.LocationMaster.IX_LocationMaster_Active_DisplayOrder_Name', 'IX_FixtureEquivalentLocationIndex', 'INDEX'");
    await db.ExecuteAsync(applyIndex);
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.LocationMaster') AND name='IX_LocationMaster_Active_DisplayOrder_Name'") == 0,
        "An equivalent supporting index with another name prevents a duplicate index");
    Check(await db.ExecuteScalarAsync<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.getActiveLocations'))") == originalProcedure
        && originalLocations.SequenceEqual(await db.QueryAsync<string>("SELECT CONVERT(nvarchar(36),LocationMasterID) + '|' + LocationName + '|' + CONVERT(nvarchar(1),IsActive) + '|' + CONVERT(nvarchar(12),DisplayOrder) FROM dbo.LocationMaster ORDER BY LocationMasterID")),
        "The SQL file preserves location records and the existing stored procedure");
    // Instrument only the disposable fixture's procedure to count real SQL calls.
    await db.ExecuteAsync("""
        ALTER PROCEDURE dbo.getActiveLocations AS
        BEGIN
            SET NOCOUNT ON;
            UPDATE dbo.FetchCount SET Value=Value+1;
            WAITFOR DELAY '00:00:00.050';
            SELECT LocationMasterID,LocationName FROM dbo.LocationMaster
            WHERE IsActive=1 ORDER BY DisplayOrder,LocationName;
        END;
        """);
    var logs = new CapturedLogs();
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(logs);
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:dbconnection"] = settings.ConnectionString });
    builder.Services.AddControllers().AddApplicationPart(typeof(LocationController).Assembly);
    builder.Services.AddLocationCatalog();
    app = builder.Build();
    app.MapControllers();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(12) };
    var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => http.GetAsync("/api/Location/getLocations")));
    try
    {
        Check(responses.All(response => response.StatusCode == HttpStatusCode.OK), "Concurrent anonymous location requests succeed");
        Check(await db.ExecuteScalarAsync<int>("SELECT Value FROM dbo.FetchCount") == 1, "Concurrent HTTP requests execute the SQL procedure once");
        var items = await responses[0].Content.ReadFromJsonAsync<LocationMasterModal[]>();
        Check(items!.Length == 2 && items[0].LocationName == "Rasampalayam"
            && items[1].LocationName == "Private fixture active town", "The existing array contract, active filter and ordering are preserved");
        var timings = responses.Select(response => response.Headers.GetValues("Server-Timing").Single()).ToArray();
        Check(timings.Count(value => value.Contains("desc=\"miss\"")) == 1
            && timings.Count(value => value.Contains("desc=\"hit\"")) == 11, "Server-Timing reports duration and cache status per request");
    }
    finally { foreach (var response in responses) response.Dispose(); }
    using (var signup = await http.GetAsync("/api/Admin/signup-locations"))
        Check(signup.IsSuccessStatusCode && signup.Headers.GetValues("Server-Timing").Single().Contains("desc=\"hit\""),
            "Owner signup uses the same public snapshot");
    // Evict the fixture cache to verify SQL failures and recovery through HTTP.
    var memory = (MemoryCache)app.Services.GetRequiredService<IMemoryCache>();
    memory.Compact(1);
    await db.ExecuteAsync("ALTER PROCEDURE dbo.getActiveLocations AS THROW 51000, 'Private fixture SQL failure', 1;");
    using (var failure = await http.GetAsync("/api/Location/getLocations"))
        Check(failure.StatusCode == HttpStatusCode.ServiceUnavailable
            && !(await failure.Content.ReadAsStringAsync()).Contains("Private fixture"), "SQL failures return a safe retryable 503");
    await db.ExecuteAsync("""
        ALTER PROCEDURE dbo.getActiveLocations AS
        SELECT LocationMasterID,LocationName FROM dbo.LocationMaster
        WHERE IsActive=1 ORDER BY DisplayOrder,LocationName;
        """);
    using (var recovery = await http.GetAsync("/api/Location/getLocations"))
        Check(recovery.IsSuccessStatusCode && recovery.Headers.GetValues("Server-Timing").Single().Contains("desc=\"miss\""),
            "A SQL failure does not poison the cache and explicit retry succeeds");
    memory.Compact(1);
    await using (var blocker = new SqlConnection(settings.ConnectionString))
    {
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.LocationMaster WITH (TABLOCKX,HOLDLOCK)", transaction: transaction);
        var timer = Stopwatch.StartNew();
        using var timeout = await http.GetAsync("/api/Location/getLocations");
        Check(timeout.StatusCode == HttpStatusCode.ServiceUnavailable && timer.Elapsed < TimeSpan.FromSeconds(9),
            "Blocked SQL returns 503 within the bounded request budget");
        await transaction.RollbackAsync();
    }
    using (var retry = await http.GetAsync("/api/Location/getLocations"))
        Check(retry.IsSuccessStatusCode, "Location loading recovers after SQL blocking clears");
    Check(logs.Messages.Any(message => message.Contains("Location SQL timing:") && message.Contains("OpenMs=") && message.Contains("QueryMs="))
        && logs.Messages.Any(message => message.Contains("Cache=hit"))
        && logs.Messages.Any(message => message.Contains("Outcome=sql-timeout")), "Logs distinguish connection/query time, cache hits and SQL timeouts");
    Check(!logs.Messages.Any(message => message.Contains("Private fixture")
        || (!string.IsNullOrEmpty(settings.Password) && message.Contains(settings.Password))),
        "Timing logs omit location names, SQL messages and connection credentials");
    Console.WriteLine($"Passed {checks} location loading checks.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    await db.CloseAsync();
    SqlConnection.ClearAllPools();
    await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
}

sealed class FixtureClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CapturedLogger(
        categoryName == typeof(LocationController).FullName || categoryName == typeof(LocationService).FullName ? Messages : null);
    public void Dispose() { }
    private sealed class CapturedLogger(ConcurrentQueue<string>? messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => messages is not null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages?.Enqueue(formatter(state, exception));
    }
}
