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

// Apply the directory migration only to the named localhost fixture database.
// Insert unique fixtures and remove them in finally; no external services run.
var configPath = Path.GetFullPath(args.Single());
var config = new ConfigurationBuilder().AddJsonFile(configPath).Build();
var connectionString = config.GetConnectionString("dbconnection")!;
var guard = new SqlConnectionStringBuilder(connectionString);
if (!new[] { "localhost", ".", "(local)" }.Contains(guard.DataSource.ToLowerInvariant())
    || guard.InitialCatalog != "InfinityHairArtsAdmin")
    throw new InvalidOperationException("Only the named local test database is allowed.");
// The sandbox cannot use Schannel. This override is restricted to the local fixture.
guard.Encrypt = SqlConnectionEncryptOption.Optional;
connectionString = guard.ConnectionString;
config = new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(
    new Dictionary<string, string?> { ["ConnectionStrings:dbconnection"] = connectionString }).Build();

await using var db = new SqlConnection(connectionString);
await db.OpenAsync();
var sql = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configPath)!, "Database", "CustomerSalonDirectory.sql"));
for (var repeat = 0; repeat < 2; repeat++)
    foreach (var batch in Regex.Split(sql, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);

var token = "DirectoryFixture" + Guid.NewGuid().ToString("N");
var locations = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
var expected = new HashSet<Guid>();
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

WebApplication? app = null;
try
{
    for (var index = 0; index < locations.Length; index++)
        await db.ExecuteAsync("""
            INSERT dbo.LocationMaster(LocationMasterID,LocationName,IsActive,DisplayOrder)
            VALUES(@id,@name,@active,9999)
            """, new { id = locations[index], name = token + " Area " + index, active = index != 2 });

    async Task<Guid> Salon(int location, string name, bool active = true, bool visible = true)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.SalonMaster(SalonMasterID,LocationMasterID,SalonName,IsActive,IsVisibleToCustomers,DisplayOrder)
            VALUES(@id,@locationID,@name,@active,@visible,9999)
            """, new { id, locationID = locations[location], name = token + name, active, visible });
        return id;
    }
    for (var index = 0; index < 23; index++)
        expected.Add(await Salon(index % 2, " Salon " + index.ToString("D2")));
    var literal = await Salon(0, " %_[~");
    expected.Add(literal);
    await Salon(0, " Hidden", visible: false);
    await Salon(0, " Inactive", active: false);
    await Salon(2, " Inactive area");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddConfiguration(config);
    builder.Services.AddControllers().AddApplicationPart(typeof(LocationController).Assembly);
    builder.Services.AddScoped<LocationService>();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<CustomerSessionRegistry>();
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddSession();
    app = builder.Build();
    app.UseRouting();
    app.UseSession();
    app.UseMiddleware<CustomerApiSessionMiddleware>();
    app.MapControllers();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    const string route = "/api/Location/salons";
    async Task<SalonDirectoryPage> Page(string query)
    {
        using var response = await http.GetAsync(route + query);
        Check(response.StatusCode == HttpStatusCode.OK, "Anonymous public directory request succeeds");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Check(json.TryGetProperty("items", out _) && json.TryGetProperty("nextCursor", out _),
            "Response uses the mobile application's camel-case page contract");
        return json.Deserialize<SalonDirectoryPage>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    var page = await Page("?search=" + token);
    Check(page.Items.Count == 20 && page.NextCursor is not null, "Default first page returns 20 cards and a continuation");
    Check(page.Items.All(item => item.LocationName.StartsWith(token)), "Every card includes its location name");
    var next = await Page("?search=" + token + "&cursor=" + Uri.EscapeDataString(page.NextCursor!));
    Check(next.Items.Count == 4 && next.NextCursor is null, "Final page terminates pagination");
    var ids = page.Items.Concat(next.Items).Select(item => item.SalonMasterID).ToList();
    Check(ids.Count == ids.Distinct().Count() && expected.SetEquals(ids),
        "Pagination has no missing or duplicate salons and excludes hidden/inactive salons and areas");
    page = await Page("?pageSize=1&search=" + token);
    Check(page.Items.Count == 1 && page.NextCursor is not null, "Small page sizes are honored");
    var literalPage = await Page("?search=" + Uri.EscapeDataString(token + " %_[~"));
    Check(literalPage.Items.Count == 1 && literalPage.Items[0].SalonMasterID == literal,
        "Search treats SQL LIKE metacharacters as literal text");
    var area = await Page("?search=" + Uri.EscapeDataString(token + " Area 1"));
    Check(area.Items.Count == 11 && area.Items.All(item => item.LocationMasterID == locations[1]),
        "Search matches location names");
    var empty = await Page("?search=" + token + "missing");
    Check(empty.Items.Count == 0 && empty.NextCursor is null, "No matches returns a valid empty page");
    await Salon(0, " " + new string('ம', 145) + " A");
    await Salon(1, " " + new string('ம', 145) + " B");
    var unicodeSearch = Uri.EscapeDataString(new string('ம', 100));
    var unicode = await Page("?pageSize=1&search=" + unicodeSearch);
    Check(unicode.NextCursor is { Length: <= 2048 }, "Long Tamil names keep cursors within the mobile app's limit");
    var unicodeNext = await Page("?pageSize=1&search=" + unicodeSearch + "&cursor=" + Uri.EscapeDataString(unicode.NextCursor!));
    Check(unicodeNext.Items.Count == 1 && unicodeNext.NextCursor is null
        && unicodeNext.Items[0].SalonMasterID != unicode.Items[0].SalonMasterID, "Unicode cursors round-trip to the next salon");
    foreach (var query in new[] { "?pageSize=0", "?pageSize=21", "?search=" + new string('a', 101),
        "?cursor=invalid", "?cursor=" + new string('a', 2049),
        "?search=other&cursor=" + Uri.EscapeDataString(page.NextCursor!),
        "?cursor=" + Uri.EscapeDataString(Convert.ToBase64String("null"u8.ToArray())) })
    {
        using var response = await http.GetAsync(route + query);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid search, size or cursor returns HTTP 400");
    }
    using var legacy = await http.GetAsync($"/api/Location/{locations[0]}/salons");
    Check(legacy.StatusCode == HttpStatusCode.OK, "Existing per-location route still works");
    Console.WriteLine($"Passed {checks} customer salon directory checks.");
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    await db.ExecuteAsync("DELETE dbo.SalonMaster WHERE LocationMasterID IN @locations; DELETE dbo.LocationMaster WHERE LocationMasterID IN @locations;", new { locations });
}
