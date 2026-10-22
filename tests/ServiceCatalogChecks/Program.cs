using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;

// Read-only checks against the existing local service catalog. No notification workers run.
var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(args.Single())).Build();
var connectionString = configuration.GetConnectionString("dbconnection")!;
var guard = new SqlConnectionStringBuilder(connectionString);
if (!new[] { "localhost", ".", "(local)" }.Contains(guard.DataSource.ToLowerInvariant())
    || guard.InitialCatalog != "InfinityHairArtsAdmin")
    throw new InvalidOperationException("Only the named local test database is allowed.");

using var db = new SqlConnection(connectionString);
db.Open();
var expectedIds = new HashSet<Guid>();
foreach (var groupId in new[] {
    "A557426D-B381-4B1F-BD8A-FA75D178B7C5",
    "7759077E-6A0E-4C9B-8D99-5CE83230F4F2",
    "D1DEDD53-8A3E-485C-964D-CC02DC312AE8"
})
{
    var rows = db.Query("dbo.filterHariCutItemGroupMaster",
        new { HairCutItemGroupMaster_ID = Guid.Parse(groupId) }, commandType: CommandType.StoredProcedure);
    foreach (var row in rows) expectedIds.Add((Guid)row.HairCut_Item_ID);
}
var comboCount = db.Query("dbo.getHairCut_Item_List", commandType: CommandType.StoredProcedure).Count();
if (expectedIds.Count <= comboCount)
    throw new InvalidOperationException("The local catalog needs services from multiple categories to verify this regression.");

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration.AddConfiguration(configuration);
builder.Services.AddControllers().AddApplicationPart(typeof(AdminController).Assembly);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<SalonOwnerAuthenticationService>();
builder.Services.AddScoped<ISalonOwnerAuthenticationService>(services => services.GetRequiredService<SalonOwnerAuthenticationService>());
builder.Services.AddScoped<SalonOwnerPersistentSignIn>();
builder.Services.AddScoped<SalonOwnerDashboardService>();
builder.Services.AddScoped<BookingQrService>();
await using var app = builder.Build();
app.MapControllers();
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    using var response = await http.PostAsJsonAsync("/api/Admin/GetListHairCutTpyes", "");
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Check(response.StatusCode == HttpStatusCode.OK && body.GetProperty("item1").GetString() == "Success",
        "The actual service-list endpoint succeeds against SQL");
    var items = body.GetProperty("item2").EnumerateArray().ToArray();
    var actualIds = items.Select(item => item.GetProperty("HairCut_Item_ID").GetGuid()).ToHashSet();
    Check(expectedIds.IsSubsetOf(actualIds) && items.Length > comboCount,
        "Initial list includes every service from Combo, Hair Cut and Facial");
    Check(actualIds.Count == items.Length, "Each service appears once");
    var prices = items.Select(item => item.GetProperty("HairCut_Item_Price").GetDecimal()).ToArray();
    Check(prices.SequenceEqual(prices.Order()), "Prices are sorted numerically from lowest to highest");
    Check(db.Query("dbo.getHairCut_Item_List", commandType: CommandType.StoredProcedure).Count() == comboCount,
        "The existing combo procedure is unchanged");
    Console.WriteLine($"Verified {items.Length} local services; lowest local price: {prices[0]}.");
}
finally
{
    await app.StopAsync();
}

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS: " + description);
}
