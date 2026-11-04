using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration["ConnectionStrings:dbconnection"] = "Server=unused;Database=unused";
builder.Services.AddControllers().AddApplicationPart(typeof(LocationController).Assembly);
builder.Services.AddScoped<LocationService>();
var directory = new FixtureDirectory();
builder.Services.AddSingleton<ICustomerSalonDirectory>(directory);
await using var app = builder.Build();
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient {
    BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
};
var checks = 0;
void Check(bool condition, string label) {
    if (!condition) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS HTTP: " + label);
}
var first = await client.GetAsync("/api/Location/salons?pageSize=20&search=");
Check(first.StatusCode == HttpStatusCode.OK, "Public customer directory accepts empty searches without authentication");
var page = (await first.Content.ReadFromJsonAsync<CustomerSalonPage>())!;
Check(page.Items.Count == 1 && page.Items[0].LocationName == "Fixture city", "Cards include their location in one response");
var raw = await first.Content.ReadAsStringAsync();
Check(!raw.Contains("displayOrder", StringComparison.OrdinalIgnoreCase), "Internal cursor sort metadata is not returned as a card property");
Check(directory.Query!.PageSize == 20, "Requested page size reaches the directory");
var cursor = new CustomerSalonCursor(0, "Fixture salon", page.Items[0].SalonMasterID, CustomerSalonCursor.Fingerprint("Style")).Encode();
var next = await client.GetAsync("/api/Location/salons?search=Style&cursor=" + cursor);
Check(next.StatusCode == HttpStatusCode.OK && directory.After?.SalonMasterID == page.Items[0].SalonMasterID,
    "Cursor is decoded and passed to the directory");
var beforeInvalid = directory.Calls;
foreach (var query in new[] {
    "pageSize=0", "pageSize=51", "pageSize=not-a-number", "search=" + new string('x', 101),
    "cursor=%%%", "cursor=" + new string('x', 2049), "search=Different&cursor=" + cursor
}) Check((await client.GetAsync("/api/Location/salons?" + query)).StatusCode == HttpStatusCode.BadRequest,
    "Invalid page/search/cursor rejected: " + query.Split('=')[0]);
Check(directory.Calls == beforeInvalid, "Invalid requests never reach database service");
var multilingualSearch = new string('க', 100);
var multilingual = new CustomerSalonCursor(1, string.Concat(Enumerable.Repeat("தமிழ்", 40)), Guid.NewGuid(), CustomerSalonCursor.Fingerprint(multilingualSearch));
var encoded = multilingual.Encode();
Check(("pageSize=20&search=" + Uri.EscapeDataString(multilingualSearch) + "&cursor=" + encoded).Length < 2048
    && new CustomerSalonQuery { Search = multilingualSearch, Cursor = encoded }.TryReadCursor(out var decoded)
    && decoded == multilingual, "Long multilingual names round-trip within query-string limits");
Check(!new CustomerSalonQuery { Cursor = new CustomerSalonCursor(0, "", Guid.Empty, "").Encode() }.TryReadCursor(out _),
    "Empty cursor identity is rejected");
await app.StopAsync();
Console.WriteLine($"{checks} HTTP checks passed.");
if (args.Contains("--database")) await DatabaseChecks.Run();

sealed class FixtureDirectory : ICustomerSalonDirectory
{
    private readonly Guid id = Guid.NewGuid();
    public int Calls { get; private set; }
    public CustomerSalonQuery? Query { get; private set; }
    public CustomerSalonCursor? After { get; private set; }
    public Task<CustomerSalonPage> GetAsync(CustomerSalonQuery query, CustomerSalonCursor? after, CancellationToken cancellationToken)
    {
        Calls++;
        Query = query;
        After = after;
        return Task.FromResult(new CustomerSalonPage([new CustomerSalonCard {
            SalonMasterID = id, LocationMasterID = Guid.NewGuid(), SalonName = "Fixture salon", LocationName = "Fixture city"
        }], null));
    }
}
