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

try { await RunAsync(); }
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }

static async Task RunAsync()
{
    var root = Directory.GetCurrentDirectory();
    while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
        root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
    var source = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")).Build();
    var settings = new SqlConnectionStringBuilder(source.GetConnectionString("dbconnection"));
    if (!new[] { ".", "localhost", "127.0.0.1", "(localdb)", Environment.MachineName }
        .Contains(settings.DataSource.Split('\\', ',')[0], StringComparer.OrdinalIgnoreCase))
        throw new Exception("These checks require a disposable local SQL database.");
    settings.Encrypt = SqlConnectionEncryptOption.Optional;
    settings.InitialCatalog = "master";
    await using var master = new SqlConnection(settings.ConnectionString);
    await master.OpenAsync();
    var database = "SalonexaServiceLoadingChecks_" + Guid.NewGuid().ToString("N");
    await master.ExecuteAsync($"CREATE DATABASE [{database}]");
    settings.InitialCatalog = database;
    await using var db = new SqlConnection(settings.ConnectionString);
    WebApplication? app = null;
    try
    {
        await db.OpenAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, IsActive bit);
            CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier, IsActive bit);
            CREATE TABLE dbo.SalonService(SalonServiceID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier,
                ServiceName nvarchar(150), Category nvarchar(80), Price decimal(10,2), DurationMinutes int,
                Description nvarchar(1000), ImagePath nvarchar(200), IsActive bit, CreatedUtc datetime2);
            CREATE TABLE dbo.HairCut_GroupMaster(HairCutItemGroupMaster_ID uniqueidentifier PRIMARY KEY, HairCutItemGroup nvarchar(80));
            CREATE TABLE dbo.HairCut_Item_List(HairCut_Item_ID uniqueidentifier PRIMARY KEY, HairCut_Item_Name nvarchar(150),
                HairCut_Item_Price int, HairCutItemImage nvarchar(200), HairCutItemGroupMaster_ID uniqueidentifier, IsDeleted bit);
            CREATE TABLE dbo.SalonHairCutItem(HairCutItemID uniqueidentifier, SalonMasterID uniqueidentifier,
                PRIMARY KEY(HairCutItemID,SalonMasterID));
            """);
        var migration = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "SalonOwnerServicesRead.sql"));
        async Task Migrate() {
            foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
        }
        await Migrate(); await Migrate();
        var salon = Guid.NewGuid(); var otherSalon = Guid.NewGuid(); var emptySalon = Guid.NewGuid();
        var owner = Guid.NewGuid(); var otherOwner = Guid.NewGuid(); var emptyOwner = Guid.NewGuid();
        var active = Guid.NewGuid(); var inactive = Guid.NewGuid(); var linked = Guid.NewGuid();
        var deleted = Guid.NewGuid(); var hidden = Guid.NewGuid(); var group = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.SalonMaster VALUES(@salon,1),(@otherSalon,1),(@emptySalon,1);
            INSERT dbo.SalonOwner VALUES(@owner,@salon,1),(@otherOwner,@otherSalon,1),(@emptyOwner,@emptySalon,1);
            INSERT dbo.SalonService VALUES
                (@active,@salon,'Original service','Hair',200,30,NULL,NULL,1,'2020-01-01'),
                (@inactive,@salon,'Inactive service','Hair',100,30,'Description','/image.png',0,'2020-01-01');
            INSERT dbo.HairCut_GroupMaster VALUES(@group,'  Haircuts  ');
            INSERT dbo.HairCut_Item_List VALUES
                (@active,'Duplicate original',200,'original.jpg',@group,0),
                (@linked,'  Linked haircut  ',120,'  haircut.jpg  ',@group,0),
                (@deleted,'Deleted haircut',50,NULL,@group,1),
                (@hidden,'Other salon only',10,NULL,@group,0);
            INSERT dbo.SalonHairCutItem VALUES(@active,@salon),(@linked,@salon),(@deleted,@salon),(@hidden,@otherSalon);
            """, new { salon, otherSalon, emptySalon, owner, otherOwner, emptyOwner, active, inactive, linked, deleted, hidden, group });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:dbconnection"] = settings.ConnectionString,
            ["Authentication:KeyRingPath"] = Path.Combine(AppContext.BaseDirectory, "fixture-keys")
        }).Build();
        var reader = new SalonOwnerServicesReader(config);
        var checks = 0;
        void Check(bool condition, string label) {
            if (!condition) throw new Exception(label);
            checks++; Console.WriteLine("PASS: " + label);
        }
        var rows = await reader.GetAsync(owner, salon, default);
        Check(rows.Count == 3 && rows.Select(r => r.SalonServiceID).ToHashSet().SetEquals([active, inactive, linked]),
            "All creation dates and both active/inactive services load for the owner's salon");
        Check(rows.Count(r => r.SalonServiceID == active) == 1, "Linked and legacy copies of the same service are not duplicated");
        Check(rows.All(r => r.SalonServiceID != hidden && r.SalonServiceID != deleted), "Deleted and other-salon haircuts are excluded");
        var haircut = rows.Single(r => r.SalonServiceID == linked);
        Check(haircut.ServiceName == "Linked haircut" && haircut.Category == "Haircuts" && haircut.Price == 120,
            "Linked haircut values use the existing catalogue contract");
        Check(haircut.DurationMinutes is null && !haircut.CanEdit && haircut.ImagePath == "/Images/HairCutItemListImages/haircut.jpg",
            "Linked haircuts preserve nullable duration, read-only metadata and image path");
        Check(rows.Single(r => r.SalonServiceID == active).Description == "" && rows.Single(r => r.SalonServiceID == active).ImagePath == "",
            "Nullable service descriptions and images remain safe empty strings");
        Check(rows.Select(r => r.SalonServiceID).SequenceEqual([linked, active, inactive]), "Services sort by activity and numeric price");
        Check((await reader.GetAsync(emptyOwner, emptySalon, default)).Count == 0, "A salon without services receives an empty collection");
        try { await reader.GetAsync(otherOwner, salon, default); throw new Exception("Cross-salon read was accepted"); }
        catch (SqlException error) when (error.Number == 50013) { Check(true, "Database ownership checks reject cross-salon access"); }
        await Migrate();
        Check((await reader.GetAsync(owner, salon, default)).Count == 3, "Rerunning the read-only migration preserves catalogue data");

        var owners = new FixtureOwners([(owner, salon), (otherOwner, otherSalon), (emptyOwner, emptySalon)]);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Configuration.AddConfiguration(config);
        builder.Services.AddDistributedMemoryCache(); builder.Services.AddSession();
        builder.Services.AddSingleton<ISalonOwnerAuthenticationService>(owners);
        builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration, builder.Environment);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider(); builder.Services.AddSingleton(reader);
        builder.Services.AddControllers().AddApplicationPart(typeof(SalonOwnerServicesController).Assembly);
        app = builder.Build(); app.Use(async (ctx, next) => { ctx.Request.Scheme = "https"; await next(ctx); });
        app.UseSession(); app.UseMiddleware<SalonOwnerSessionMiddleware>(); app.MapControllers();
        app.MapPost("/fixture/login/{index:int}", async (int index, HttpContext context, SalonOwnerPersistentSignIn signIn) => {
            await signIn.SignInAsync(context, owners.Items[index]); return Results.NoContent();
        });
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };
        var cookies = new CookieContainer(); var origin = new Uri("https://localhost");
        async Task<HttpResponseMessage> Send(HttpMethod method, string path) {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(origin));
            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values) cookies.SetCookies(origin, value);
            return response;
        }
        const string route = "/api/Admin/services";
        using (var response = await Send(HttpMethod.Get, route)) Check(response.StatusCode == HttpStatusCode.Unauthorized,
            "The formerly missing route exists and rejects anonymous users with 401");
        using (var response = await Send(HttpMethod.Post, "/fixture/login/0")) Check(response.IsSuccessStatusCode, "A protected owner ticket can sign in");
        using (var response = await Send(HttpMethod.Get, route)) {
            var result = await response.Content.ReadFromJsonAsync<List<SalonOwnerServiceItem>>();
            Check(response.StatusCode == HttpStatusCode.OK && result?.Count == 3, "The actual service endpoint loads the signed-in salon's catalogue");
            Check(response.Headers.CacheControl?.NoStore == true, "Owner-specific responses are not cached");
        }
        using (var response = await Send(HttpMethod.Get, route + "?salonMasterID=" + otherSalon + "&fromDate=2099-01-01")) {
            var result = await response.Content.ReadFromJsonAsync<List<SalonOwnerServiceItem>>();
            Check(result?.Count == 3 && result.All(r => r.SalonServiceID != hidden), "Client salon/date overrides cannot change the owner's catalogue scope");
        }
        await db.ExecuteAsync("UPDATE dbo.SalonOwner SET IsActive=0 WHERE SalonOwnerID=@owner", new { owner });
        using (var response = await Send(HttpMethod.Get, route)) Check(response.StatusCode == HttpStatusCode.Forbidden,
            "Database activity checks reject an owner disabled after sign-in");
        using (var response = await Send(HttpMethod.Post, "/fixture/login/1")) Check(response.IsSuccessStatusCode, "Another owner can sign in");
        using (var response = await Send(HttpMethod.Get, route)) {
            var result = await response.Content.ReadFromJsonAsync<List<SalonOwnerServiceItem>>();
            Check(result?.Count == 1 && result[0].SalonServiceID == hidden, "Switching owners returns only the new salon's services");
        }
        using (var response = await Send(HttpMethod.Post, "/fixture/login/2")) Check(response.IsSuccessStatusCode, "An owner of an empty salon can sign in");
        using (var response = await Send(HttpMethod.Get, route)) Check(response.StatusCode == HttpStatusCode.OK
            && (await response.Content.ReadFromJsonAsync<List<SalonOwnerServiceItem>>())?.Count == 0, "The HTTP response distinguishes an empty catalogue from a missing endpoint");
        await db.ExecuteAsync("DROP PROCEDURE dbo.GetSalonOwnerServices");
        using (var response = await Send(HttpMethod.Get, route)) Check(response.StatusCode == HttpStatusCode.ServiceUnavailable,
            "A missing database procedure reports a deployment problem instead of a false empty catalogue");
        Console.WriteLine($"{checks} service-loading checks passed.");
    }
    finally
    {
        if (app is not null) await app.DisposeAsync();
        await db.CloseAsync(); SqlConnection.ClearAllPools();
        await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    }
}

sealed class FixtureOwners((Guid Owner, Guid Salon)[] owners) : ISalonOwnerAuthenticationService
{
    public SalonOwnerLoginResponse[] Items { get; } = owners.Select(pair => new SalonOwnerLoginResponse {
        SalonOwnerID = pair.Owner, SalonMasterID = pair.Salon, OwnerName = "Service fixture", UserName = "fixture",
        SalonName = "Fixture salon", AuthenticationStamp = "fixture-version"
    }).ToArray();
    public Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(owner => owner.SalonOwnerID == id));
    public Task<SalonOwnerLoginResponse?> AuthenticateAsync(SalonOwnerLoginRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SalonOwnerLoginResponse> InitializeAsync(InitializeSalonOwnerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
