using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.Extensions.FileProviders;

// Exercises HTTP -> owner ticket -> controller -> stored procedures -> SQL persistence for
// GET/POST/PUT /api/Admin/services. Everything lives in a disposable database and temp folder.
try { await RunAsync(); }
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }

static async Task RunAsync()
{
    var root = Directory.GetCurrentDirectory();
    while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
        root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
    var source = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")).Build();
    var settings = new SqlConnectionStringBuilder(source.GetConnectionString("dbconnection"));
    if (!new[] { ".", "localhost", "(local)", "127.0.0.1", "(localdb)", Environment.MachineName }
        .Contains(settings.DataSource.Split('\\', ',')[0], StringComparer.OrdinalIgnoreCase))
        throw new Exception("These checks require a disposable local SQL database.");
    settings.Encrypt = SqlConnectionEncryptOption.Optional;
    settings.InitialCatalog = "master";
    var database = "SalonexaServicesChecks_" + Guid.NewGuid().ToString("N");
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    var fixtureRoot = Path.Combine(tempRoot, database);
    var imageRoot = Path.Combine(fixtureRoot, "Images", "HairCutItemListImages");
    Directory.CreateDirectory(imageRoot);
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
    MultipartFormDataContent Form(string name = "Haircut", bool image = true, bool active = true,
        string price = "250.50", string duration = "30", byte[]? bytes = null, string mime = "image/png", string category = "Hair")
    {
        // Same multipart field names as AdminDashboardService.saveService in the admin app.
        var form = new MultipartFormDataContent();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["serviceName"] = name, ["category"] = category, ["price"] = price,
            ["durationMinutes"] = duration, ["description"] = "Fixture description", ["isActive"] = active ? "true" : "false"
        }) form.Add(new StringContent(value), key);
        if (image)
        {
            var content = new ByteArrayContent(bytes ?? png);
            content.Headers.ContentType = new MediaTypeHeaderValue(mime);
            form.Add(content, "image", "../../untrusted-filename.png");
        }
        return form;
    }
    int Files() => Directory.EnumerateFiles(imageRoot, "*", SearchOption.AllDirectories).Count();
    try
    {
        await using var db = new SqlConnection(settings.ConnectionString);
        await db.OpenAsync();
        // Mirrors the production column shapes, including the legacy int price.
        await db.ExecuteAsync("""
            CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, IsActive bit NOT NULL, SalonName nvarchar(200));
            CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier, IsActive bit NOT NULL);
            CREATE TABLE dbo.HairCut_GroupMaster(HairCutItemGroupMaster_ID uniqueidentifier, HairCutItemGroup nvarchar(100), OrderID int);
            CREATE TABLE dbo.HairCut_Item_List(HairCut_Item_ID uniqueidentifier, HairCut_Item_Code int,
                HairCut_Item_Name nvarchar(max), HairCut_Item_Price int, HairCut_Item_Quantity nvarchar(max),
                HairCut_Item_CGST decimal(18,2), HairCut_Item_SGST decimal(18,2), HairCut_Item_Taxable decimal(18,2),
                HairCutItemImage nvarchar(max), HairCutItemImageSystemPath nvarchar(max), HairCutItemImageS3Path nvarchar(max),
                HairCutItemGroupMaster_ID uniqueidentifier, Created_Date datetime, Created_By uniqueidentifier, IsDeleted bit);
            """);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "SalonOwnerServices.sql"));
        async Task Deploy()
        {
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
        }
        await Deploy();
        await Deploy();
        var salon = Guid.NewGuid();
        var otherSalon = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.SalonMaster VALUES(@salon,1,N'Infinity Hair Arts'),(@otherSalon,1,N'Other salon');
            INSERT dbo.SalonOwner VALUES(@owner,@salon,1),(@otherOwner,@otherSalon,1);
            """, new { salon, otherSalon, owner, otherOwner });

        var owners = new FixtureOwners();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = "Development", ContentRootPath = fixtureRoot
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ConnectionStrings:dbconnection"] = settings.ConnectionString;
        builder.Configuration["Authentication:KeyRingPath"] = Path.Combine(fixtureRoot, "keys");
        builder.Services.AddControllers().AddApplicationPart(typeof(SalonOwnerServicesController).Assembly);
        builder.Services.AddScoped<SalonOwnerServicesReader>();
        builder.Services.AddScoped<SalonOwnerServicesWriter>();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        builder.Services.AddSingleton<ISalonOwnerAuthenticationService>(owners);
        builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration, builder.Environment);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        app = builder.Build();
        // The owner cookie is Secure; treat loopback requests as HTTPS.
        app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(imageRoot), RequestPath = "/Images/HairCutItemListImages" });
        app.UseRouting();
        app.UseSession();
        app.UseMiddleware<SalonOwnerSessionMiddleware>();
        app.MapControllers();
        // Fixture-only sign-in route: issues the same persistent owner ticket as /api/Admin/login.
        app.MapPost("/fixture/login/{ownerId:guid}/{salonId:guid}",
            async (Guid ownerId, Guid salonId, HttpContext context, SalonOwnerPersistentSignIn signIn) =>
            {
                await signIn.SignInAsync(context, owners.Set(ownerId, salonId));
                return Results.NoContent();
            });
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        OwnerClient Client() => new(address);

        using var anonymous = Client();
        Check((await anonymous.Send(HttpMethod.Get, "/api/Admin/services")).StatusCode == HttpStatusCode.Unauthorized,
            "Anonymous catalogue access requires sign-in");
        using (var form = Form()) Check((await anonymous.Send(HttpMethod.Post, "/api/Admin/services", form)).StatusCode == HttpStatusCode.Unauthorized,
            "Anonymous service creation is rejected");
        using (var form = Form()) Check((await anonymous.Send(HttpMethod.Put, $"/api/Admin/services/{Guid.NewGuid()}", form)).StatusCode == HttpStatusCode.Unauthorized,
            "Anonymous service edits are rejected");
        using var client = Client();
        await client.SignIn(owner, salon);
        var empty = await client.Send(HttpMethod.Get, "/api/Admin/services");
        Check(empty.StatusCode == HttpStatusCode.OK && (await empty.Content.ReadFromJsonAsync<SalonOwnerServiceItem[]>())!.Length == 0,
            "A genuinely empty salon returns an empty array");
        Check(empty.Headers.CacheControl?.NoStore == true, "Service lists cannot be cached between owners");
        using (var missing = Form(image: false)) Check((await client.Send(HttpMethod.Post, "/api/Admin/services", missing)).StatusCode == HttpStatusCode.BadRequest,
            "Creating a service requires an image");
        using (var missingHeader = Client())
        {
            await missingHeader.SignIn(owner, salon);
            missingHeader.AppHeader = false;
            using var form = Form();
            Check((await missingHeader.Send(HttpMethod.Post, "/api/Admin/services", form)).StatusCode == HttpStatusCode.BadRequest,
                "Multipart writes require the application's request header");
        }
        foreach (var form in new[] { Form(price: "-1"), Form(price: "250.123"), Form(duration: "4"),
            Form(name: " "), Form(bytes: "this is not an image"u8.ToArray()), Form(mime: "image/svg+xml"),
            Form(bytes: new byte[5 * 1024 * 1024 + 1]) })
        {
            using (form) Check((await client.Send(HttpMethod.Post, "/api/Admin/services", form)).StatusCode == HttpStatusCode.BadRequest,
                "Invalid service details or image content are rejected");
        }
        Check(Files() == 0, "Rejected requests leave no service images");
        foreach (var duration in new[] { "5", "29", "31", "45", "480" })
        {
            using var form = Form(duration: duration);
            Check((await client.Send(HttpMethod.Post, "/api/Admin/services", form)).StatusCode == HttpStatusCode.BadRequest,
                $"A {duration}-minute duration is rejected (fixed at 30)");
        }
        Check(Files() == 0, "Rejected duration changes do not create images");
        using var createForm = Form(name: "  Haircut  ");
        var create = await client.Send(HttpMethod.Post, "/api/Admin/services", createForm);
        Check(create.StatusCode == HttpStatusCode.OK, "Create endpoint saves the service and photo");
        var saved = (await create.Content.ReadFromJsonAsync<SalonOwnerServiceItem>())!;
        Check(saved.ServiceName == "Haircut" && saved.Price == 250.50m && saved.IsActive && saved.DurationMinutes == 30 && saved.CanEdit,
            "Persisted, editable service details are returned after saving");
        Check(saved.ImagePath.StartsWith("/Images/HairCutItemListImages/") && Files() == 1
            && (await client.GetBytes(saved.ImagePath)).SequenceEqual(png),
            "Uploaded haircut image is served under a generated filename");
        var createdUtc = await db.QuerySingleAsync<DateTime>("SELECT Created_Date FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@id", new { id = saved.SalonServiceID });
        using (var changedDuration = Form(image: false, duration: "45"))
            Check((await client.Send(HttpMethod.Put, $"/api/Admin/services/{saved.SalonServiceID}", changedDuration)).StatusCode == HttpStatusCode.BadRequest,
                "Editing a service cannot change its fixed duration");
        Check(await db.ExecuteScalarAsync<int>("SELECT DurationMinutes FROM dbo.SalonHairCutItem WHERE HairCutItemID=@id", new { id = saved.SalonServiceID }) == 30,
            "The stored duration remains 30 after a rejected edit");
        using (var duplicate = Form()) Check((await client.Send(HttpMethod.Post, "/api/Admin/services", duplicate)).StatusCode == HttpStatusCode.Conflict,
            "Duplicate service names return a useful conflict");
        Check(Files() == 1 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List") == 1,
            "Duplicate saves roll back without extra records or images");
        using (var update = Form(name: "Updated haircut", image: false, active: false, price: "300"))
        {
            var response = await client.Send(HttpMethod.Put, $"/api/Admin/services/{saved.SalonServiceID}", update);
            var updated = (await response.Content.ReadFromJsonAsync<SalonOwnerServiceItem>())!;
            Check(response.StatusCode == HttpStatusCode.OK && !updated.IsActive && updated.Price == 300
                && updated.ServiceName == "Updated haircut" && updated.ImagePath == saved.ImagePath,
                "Editing and deactivating a service retains its existing image");
        }
        Check(createdUtc == await db.QuerySingleAsync<DateTime>("SELECT Created_Date FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@id", new { id = saved.SalonServiceID }),
            "Editing preserves the original service creation time");
        using (var replacement = Form(name: "Updated haircut", active: false))
        {
            var response = await client.Send(HttpMethod.Put, $"/api/Admin/services/{saved.SalonServiceID}", replacement);
            var updated = (await response.Content.ReadFromJsonAsync<SalonOwnerServiceItem>())!;
            Check(response.StatusCode == HttpStatusCode.OK && updated.ImagePath != saved.ImagePath
                && (await client.GetBytes(updated.ImagePath)).SequenceEqual(png)
                && (await client.GetBytes(saved.ImagePath)).SequenceEqual(png),
                "Replacing a service photo serves the new file and preserves existing image references");
        }
        using var other = Client();
        await other.SignIn(otherOwner, otherSalon);
        using (var otherForm = Form(name: "Other salon service")) Check((await other.Send(HttpMethod.Post, "/api/Admin/services", otherForm)).StatusCode == HttpStatusCode.OK,
            "Another salon saves its own service");
        var filesBeforeForeign = Files();
        using (var foreignEdit = Form()) Check((await other.Send(HttpMethod.Put, $"/api/Admin/services/{saved.SalonServiceID}", foreignEdit)).StatusCode == HttpStatusCode.NotFound,
            "A salon cannot edit another salon's service");
        Check(Files() == filesBeforeForeign, "Unauthorized record edits do not write image files");
        await db.ExecuteAsync("""
            INSERT dbo.SalonService(SalonServiceID,SalonMasterID,ServiceName,Category,Price,DurationMinutes,ImagePath,IsActive,CreatedUtc)
            VALUES(NEWID(),@salon,'Older active service','Hair',100,30,'',1,'20200101');
            """, new { salon });
        await Deploy(); // Older SalonService rows are copied once into the salon-linked haircut catalogue.
        var list = (await client.GetJson<SalonOwnerServiceItem[]>($"/api/Admin/services?salonId={otherSalon}&fromDate=2099-01-01"))!;
        Check(list.Length == 2 && list.Any(s => !s.IsActive) && list.Any(s => s.ServiceName == "Older active service")
            && list.All(s => s.ServiceName != "Other salon service" && s.CanEdit),
            "Reload returns all own services (old and inactive, all editable) without accepting salon/date overrides");
        Check(list[0].IsActive && !list[1].IsActive, "Active services are listed before inactive services");
        owners.Set(owner, otherSalon); // The ticket now resolves to a salon this owner does not belong to.
        Check((await client.Send(HttpMethod.Get, "/api/Admin/services")).StatusCode == HttpStatusCode.Forbidden,
            "Owner/salon membership is checked in the database");
        owners.Set(owner, salon);
        await db.ExecuteAsync("UPDATE dbo.SalonOwner SET IsActive=0 WHERE SalonOwnerID=@owner", new { owner });
        Check((await client.Send(HttpMethod.Get, "/api/Admin/services")).StatusCode == HttpStatusCode.Forbidden, "Inactive owners cannot read services");
        using (var form = Form(name: "Updated haircut", image: false))
            Check((await client.Send(HttpMethod.Put, $"/api/Admin/services/{saved.SalonServiceID}", form)).StatusCode == HttpStatusCode.Forbidden,
                "Inactive owners cannot edit services");
        await db.ExecuteAsync("UPDATE dbo.SalonOwner SET IsActive=1 WHERE SalonOwnerID=@owner", new { owner });
        var filesBeforeFailure = Files();
        await db.ExecuteAsync("CREATE TRIGGER dbo.fixture_service_failure ON dbo.HairCut_Item_List AFTER INSERT AS THROW 50099, 'Fixture failure', 1;");
        using (var failureForm = Form(name: "Rollback service"))
        {
            var failed = await client.Send(HttpMethod.Post, "/api/Admin/services", failureForm);
            Check(!failed.IsSuccessStatusCode, "Database write failures are not reported as successful saves");
        }
        Check(Files() == filesBeforeFailure && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List WHERE HairCut_Item_Name='Rollback service'") == 0,
            "Failed writes roll back both the service and its newly uploaded image");
        await db.ExecuteAsync("DROP TRIGGER dbo.fixture_service_failure");
        await Deploy();
        Check((await client.GetJson<SalonOwnerServiceItem[]>("/api/Admin/services"))!.Length == 2,
            "Rerunning the SQL deployment preserves saved services");

        // Existing Infinity Hair Arts menu items: these were read-only before this fix.
        var haircut = Guid.NewGuid();
        var facial = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var group = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.HairCut_GroupMaster(HairCutItemGroupMaster_ID,HairCutItemGroup,OrderID) VALUES(@group,N'Hair Cut',2);
            INSERT dbo.HairCut_Item_List(HairCut_Item_ID,HairCut_Item_Name,HairCut_Item_Price,HairCutItemImage,HairCutItemGroupMaster_ID,IsDeleted) VALUES
                (@haircut,N' Existing haircut ',130,@image,@group,0),
                (@facial,N'Facial',830,NULL,NULL,NULL),
                (@deleted,N'Deleted item',999,NULL,@group,1);
            """, new { haircut, facial, deleted, group, image = $"{haircut}.jpeg" });
        Check((await client.GetJson<SalonOwnerServiceItem[]>("/api/Admin/services"))!.Length == 2,
            "Unassigned haircut menu items are not automatically exposed to a salon");
        await db.ExecuteAsync("INSERT dbo.SalonHairCutItem(HairCutItemID,SalonMasterID) VALUES(@haircut,@salon),(@facial,@salon)", new { haircut, facial, salon });
        var linked = (await client.GetJson<SalonOwnerServiceItem[]>("/api/Admin/services"))!;
        var existing = linked.Single(item => item.SalonServiceID == haircut);
        Check(linked.Length == 4 && existing.ServiceName == "Existing haircut" && existing.Price == 130
            && existing.Category == "Hair Cut" && existing.ImagePath == $"/Images/HairCutItemListImages/{haircut}.jpeg"
            && existing.DurationMinutes is null && existing.CanEdit,
            "Existing linked menu items are now editable, with the original ID and unknown duration");
        Check(linked.Any(item => item.SalonServiceID == facial && item.Category == "Services" && item.ImagePath == "")
            && linked.All(item => item.SalonServiceID != deleted),
            "Missing photo/category values are supported and deleted menu items are excluded");
        Check((await other.GetJson<SalonOwnerServiceItem[]>("/api/Admin/services"))!
            .All(item => item.SalonServiceID != haircut && item.SalonServiceID != facial),
            "One salon's menu is hidden from other salons");
        await db.ExecuteAsync("UPDATE dbo.HairCut_Item_List SET HairCut_Item_CGST=11.70,HairCut_Item_SGST=11.70,HairCut_Item_Taxable=106.60 WHERE HairCut_Item_ID=@haircut", new { haircut });
        using (var menuEdit = Form(name: "Drimming", image: false, price: "175.75", category: "Beard"))
        {
            var edited = await client.Send(HttpMethod.Put, $"/api/Admin/services/{haircut}", menuEdit);
            Check(edited.StatusCode == HttpStatusCode.OK, "An owner can update an existing linked menu item");
            var result = (await edited.Content.ReadFromJsonAsync<SalonOwnerServiceItem>())!;
            Check(result.SalonServiceID == haircut && result.ServiceName == "Drimming" && result.Price == 175.75m
                && result.Category == "Beard" && result.DurationMinutes == 30 && result.ImagePath == existing.ImagePath,
                "The updated menu response retains its ID and photo and returns edited fields");
        }
        Check(await db.ExecuteScalarAsync<decimal>("SELECT HairCut_Item_Price FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut }) == 175.75m
            && await db.ExecuteScalarAsync<string>("SELECT HairCut_Item_Name FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut }) == "Drimming",
            "Edits persist to HairCut_Item_List rather than a separate owner-service row");
        Check(await db.ExecuteScalarAsync<decimal>("SELECT HairCut_Item_CGST FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut }) == 15.82m
            && await db.ExecuteScalarAsync<decimal>("SELECT HairCut_Item_Taxable FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut }) == 144.11m,
            "Changing price keeps the existing tax proportions consistent");
        Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_GroupMaster WHERE HairCutItemGroup=N'Beard' AND OrderID IS NOT NULL") == 1,
            "New categories have an order usable by the existing customer menu");
        Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SalonService WHERE SalonServiceID=@haircut", new { haircut }) == 0,
            "Editing a haircut item does not create a duplicate SalonService");
        Check(await db.ExecuteScalarAsync<string>("SELECT HairCutItemGroup FROM dbo.HairCut_GroupMaster WHERE HairCutItemGroupMaster_ID=@group", new { group }) == "Hair Cut",
            "Changing one item's category does not rename the shared category");
        using (var foreign = Form()) Check((await other.Send(HttpMethod.Put, $"/api/Admin/services/{haircut}", foreign)).StatusCode == HttpStatusCode.NotFound,
            "Another salon cannot update the linked haircut row");
        using (var noPhoto = Form(name: "Facial", image: false)) Check((await client.Send(HttpMethod.Put, $"/api/Admin/services/{facial}", noPhoto)).StatusCode == HttpStatusCode.OK,
            "An existing item without a photo can be edited without a replacement");
        using (var deactivate = Form(name: "Drimming", image: false, active: false))
        {
            var response = await client.Send(HttpMethod.Put, $"/api/Admin/services/{haircut}", deactivate);
            Check(response.StatusCode == HttpStatusCode.OK && !(await response.Content.ReadFromJsonAsync<SalonOwnerServiceItem>())!.IsActive,
                "Deactivating a linked menu item updates its availability");
        }
        Check(await db.ExecuteScalarAsync<bool>("SELECT IsDeleted FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut })
            && (await client.GetJson<SalonOwnerServiceItem[]>("/api/Admin/services"))!.Any(x => x.SalonServiceID == haircut && !x.IsActive),
            "Inactive menu items remain visible to the owner for reactivation");
        using (var reactivate = Form(name: "Drimming", image: false)) Check((await client.Send(HttpMethod.Put, $"/api/Admin/services/{haircut}", reactivate)).StatusCode == HttpStatusCode.OK,
            "An owner can reactivate a menu item");
        var sourceCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List");
        using (var duplicate = Form(name: "Drimming")) Check((await client.Send(HttpMethod.Post, "/api/Admin/services", duplicate)).StatusCode == HttpStatusCode.Conflict,
            "Duplicate names are checked against the salon's actual menu");
        Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List") == sourceCount,
            "Rejected duplicates do not create menu items");
        using (var sameNameOtherSalon = Form(name: "Drimming")) Check((await other.Send(HttpMethod.Post, "/api/Admin/services", sameNameOtherSalon)).StatusCode == HttpStatusCode.OK,
            "Different salons may each use the same service name");
        await Deploy();
        Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SalonHairCutItem") == 6
            && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List") == 7
            && (await client.GetJson<SalonOwnerServiceItem[]>("/api/Admin/services"))!.Length == 4,
            "Rerunning the migration preserves the menu and prevents duplicate links");
        Check((await client.GetJson<SalonOwnerServiceItem[]>("/api/Admin/services"))!.Any(item => item.SalonServiceID == haircut && item.ServiceName == "Drimming"),
            "Rerunning the migration does not overwrite edited menu names");
        await db.ExecuteAsync("DROP PROCEDURE dbo.GetSalonOwnerServices");
        Check((await client.Send(HttpMethod.Get, "/api/Admin/services")).StatusCode == HttpStatusCode.ServiceUnavailable,
            "A missing database procedure reports a deployment problem instead of an empty catalogue");
        using (var form = Form(name: "Drimming", image: false))
            Check((await client.Send(HttpMethod.Put, $"/api/Admin/services/{haircut}", form)).StatusCode == HttpStatusCode.ServiceUnavailable,
                "Edits also report a missing database update");
        Console.WriteLine($"{checks} owner service HTTP/SQL checks passed.");
    }
    finally
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        SqlConnection.ClearAllPools();
        await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        // Only remove this generated fixture directory directly under the temporary root.
        if (Directory.GetParent(fixtureRoot)?.FullName == tempRoot && database.StartsWith("SalonexaServicesChecks_"))
            Directory.Delete(fixtureRoot, recursive: true);
    }
}

sealed class FixtureOwners : ISalonOwnerAuthenticationService
{
    private readonly Dictionary<Guid, SalonOwnerLoginResponse> owners = [];

    public SalonOwnerLoginResponse Set(Guid owner, Guid salon) => owners[owner] = new SalonOwnerLoginResponse
    {
        SalonOwnerID = owner, SalonMasterID = salon, OwnerName = "Service fixture", UserName = "fixture",
        SalonName = "Fixture salon", AuthenticationStamp = "fixture-version"
    };
    public Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(owners.GetValueOrDefault(id));
    public Task<SalonOwnerLoginResponse?> AuthenticateAsync(SalonOwnerLoginRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SalonOwnerLoginResponse> InitializeAsync(InitializeSalonOwnerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}

// Keeps Secure owner cookies across loopback HTTP requests, like the admin app's credentials: 'include'.
sealed class OwnerClient(Uri address) : IDisposable
{
    private static readonly Uri Origin = new("https://localhost");
    private readonly HttpClient http = new(new HttpClientHandler { UseCookies = false }) { BaseAddress = address };
    private readonly CookieContainer cookies = new();
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

    public async Task SignIn(Guid owner, Guid salon)
    {
        using var response = await Send(HttpMethod.Post, $"/fixture/login/{owner}/{salon}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<T?> GetJson<T>(string path)
    {
        using var response = await Send(HttpMethod.Get, path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public Task<byte[]> GetBytes(string path) => http.GetByteArrayAsync(path);
    public void Dispose() => http.Dispose();
}
