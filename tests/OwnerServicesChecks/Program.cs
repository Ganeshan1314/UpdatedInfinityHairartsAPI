using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.Extensions.FileProviders;

// Exercise actual HTTP -> controller -> stored procedures -> SQL persistence.
// All records and uploaded files live in an isolated disposable fixture; no workers run.
var configurationPath = Path.GetFullPath(args.Single());
using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(configurationPath));
var settings = new SqlConnectionStringBuilder(configuration.RootElement.GetProperty("ConnectionStrings")
    .GetProperty("dbconnection").GetString());
var host = settings.DataSource.Split('\\', ',')[0];
if (!new[] { ".", "localhost", "(local)", "127.0.0.1", Environment.MachineName }.Contains(host, StringComparer.OrdinalIgnoreCase))
    throw new InvalidOperationException("Service checks only run against a local SQL Server.");
settings.Encrypt = SqlConnectionEncryptOption.Optional;
settings.InitialCatalog = "master";
var database = "SalonexaServicesChecks_" + Guid.NewGuid().ToString("N");
var tempRoot = Path.GetFullPath(Path.GetTempPath());
var fixtureRoot = Path.GetFullPath(Path.Combine(tempRoot, database));
var imageRoot = Path.Combine(fixtureRoot, "Images", "HairCutItemListImages");
Directory.CreateDirectory(imageRoot);
await using var master = new SqlConnection(settings.ConnectionString);
await master.OpenAsync();
await master.ExecuteAsync($"CREATE DATABASE [{database}]");
settings.InitialCatalog = database;
WebApplication? app = null;
var checks = 0;
void Check(bool condition, string description) {
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS: " + description);
}
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOhsAAAAASUVORK5CYII=");
MultipartFormDataContent Form(string name = "Haircut", bool image = true, bool active = true,
    string price = "250.50", string duration = "30", byte[]? bytes = null, string mime = "image/png", string category = "Hair") {
    var form = new MultipartFormDataContent();
    foreach (var (key, value) in new Dictionary<string, string> {
        ["serviceName"] = name, ["category"] = category, ["price"] = price,
        ["durationMinutes"] = duration, ["description"] = "Fixture description", ["isActive"] = active.ToString()
    }) form.Add(new StringContent(value), key);
    if (image) {
        var content = new ByteArrayContent(bytes ?? png);
        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(content, "image", "../../untrusted-filename.png");
    }
    return form;
}
int Files() => Directory.EnumerateFiles(imageRoot, "*", SearchOption.AllDirectories).Count();
try {
    await using var db = new SqlConnection(settings.ConnectionString);
    await db.OpenAsync();
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
    var script = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configurationPath)!, "Database", "SalonOwnerServices.sql"));
    async Task Deploy() {
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

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
        Args = [], EnvironmentName = "Development", ContentRootPath = fixtureRoot
    });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration["ConnectionStrings:dbconnection"] = settings.ConnectionString;
    builder.Services.AddControllers().AddApplicationPart(typeof(OwnerServicesController).Assembly);
    builder.Services.AddScoped<SalonOwnerServicesService>();
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession();
    app = builder.Build();
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(imageRoot), RequestPath = "/Images/HairCutItemListImages" });
    app.UseRouting();
    app.UseSession();
    app.MapControllers();
    // These fixture sign-in routes only exist in this loopback test host.
    app.MapPost("/test/sign-in/{ownerId:guid}/{salonId:guid}", (HttpContext context, Guid ownerId, Guid salonId) => {
        context.Session.SetString("SalonOwnerID", ownerId.ToString());
        context.Session.SetString("SalonMasterID", salonId.ToString());
        return Results.NoContent();
    });
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    HttpClient Client() {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("X-Salonexa-Request", "1");
        return client;
    }
    using var anonymous = Client();
    Check((await anonymous.GetAsync("/api/Admin/services")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous catalogue access requires sign-in");
    using (var form = Form()) Check((await anonymous.PostAsync("/api/Admin/services", form)).StatusCode == HttpStatusCode.Unauthorized,
        "Anonymous service creation is rejected");
    using var client = Client();
    await client.PostAsync($"/test/sign-in/{owner}/{salon}", null);
    var empty = await client.GetAsync("/api/Admin/services");
    Check(empty.StatusCode == HttpStatusCode.OK && (await empty.Content.ReadFromJsonAsync<SalonOwnerDashboardCatalogueService[]>())!.Length == 0,
        "A genuinely empty salon returns an empty array");
    Check(empty.Headers.CacheControl?.NoStore == true, "Service lists cannot be cached between owners");
    using (var missing = Form(image: false)) Check((await client.PostAsync("/api/Admin/services", missing)).StatusCode == HttpStatusCode.BadRequest,
        "Creating a service requires an image");
    using (var missingHeader = Client()) {
        await missingHeader.PostAsync($"/test/sign-in/{owner}/{salon}", null);
        missingHeader.DefaultRequestHeaders.Remove("X-Salonexa-Request");
        using var form = Form();
        Check((await missingHeader.PostAsync("/api/Admin/services", form)).StatusCode == HttpStatusCode.BadRequest,
            "Multipart writes require the application's request header");
    }
    foreach (var form in new[] { Form(price: "-1"), Form(price: "250.123"), Form(duration: "4"),
        Form(name: " "), Form(bytes: "this is not an image"u8.ToArray()), Form(mime: "image/svg+xml"),
        Form(bytes: new byte[5 * 1024 * 1024 + 1]) }) {
        using (form) Check((await client.PostAsync("/api/Admin/services", form)).StatusCode == HttpStatusCode.BadRequest,
            "Invalid service details or image content are rejected");
    }
    Check(Files() == 0, "Rejected requests leave no service images");
    foreach (var duration in new[] { "5", "29", "31", "45", "480" }) {
        using var form = Form(duration: duration);
        Check((await client.PostAsync("/api/Admin/services", form)).StatusCode == HttpStatusCode.BadRequest,
            "New service durations other than 30 minutes are rejected");
    }
    Check(Files() == 0, "Rejected duration changes do not create images");
    using var createForm = Form(name: "  Haircut  ");
    var create = await client.PostAsync("/api/Admin/services", createForm);
    Check(create.StatusCode == HttpStatusCode.OK, "Create endpoint saves the service and photo");
    var saved = (await create.Content.ReadFromJsonAsync<SalonOwnerDashboardCatalogueService>())!;
    Check(saved.ServiceName == "Haircut" && saved.Price == 250.50m && saved.IsActive && saved.DurationMinutes == 30,
        "Persisted service details are returned after saving");
    Check(saved.ImagePath.StartsWith("/Images/HairCutItemListImages/") && Files() == 1
        && (await client.GetByteArrayAsync(saved.ImagePath)).SequenceEqual(png),
        "Uploaded haircut image is served under a generated filename");
    var createdUtc = await db.QuerySingleAsync<DateTime>("SELECT Created_Date FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@id", new { id = saved.SalonServiceID });
    using (var changedDuration = Form(image: false, duration: "45"))
        Check((await client.PutAsync($"/api/Admin/services/{saved.SalonServiceID}", changedDuration)).StatusCode == HttpStatusCode.BadRequest,
            "Editing a service cannot change its fixed duration");
    Check(await db.ExecuteScalarAsync<int>("SELECT DurationMinutes FROM dbo.SalonHairCutItem WHERE HairCutItemID=@id", new { id = saved.SalonServiceID }) == 30,
        "The stored duration remains 30 after a rejected edit");
    using (var duplicate = Form()) Check((await client.PostAsync("/api/Admin/services", duplicate)).StatusCode == HttpStatusCode.Conflict,
        "Duplicate service names return a useful conflict");
    Check(Files() == 1 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List") == 1,
        "Duplicate saves roll back without extra records or images");
    using (var update = Form(name: "Updated haircut", image: false, active: false, price: "300")) {
        var response = await client.PutAsync($"/api/Admin/services/{saved.SalonServiceID}", update);
        var updated = (await response.Content.ReadFromJsonAsync<SalonOwnerDashboardCatalogueService>())!;
        Check(response.StatusCode == HttpStatusCode.OK && !updated.IsActive && updated.Price == 300
            && updated.ImagePath == saved.ImagePath, "Editing and deactivating a service retains its existing image");
    }
    Check(createdUtc == await db.QuerySingleAsync<DateTime>("SELECT Created_Date FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@id", new { id = saved.SalonServiceID }),
        "Editing preserves the original service creation time");
    using (var replacement = Form(name: "Updated haircut", active: false)) {
        var response = await client.PutAsync($"/api/Admin/services/{saved.SalonServiceID}", replacement);
        var updated = (await response.Content.ReadFromJsonAsync<SalonOwnerDashboardCatalogueService>())!;
        Check(response.StatusCode == HttpStatusCode.OK && updated.ImagePath != saved.ImagePath
            && (await client.GetByteArrayAsync(updated.ImagePath)).SequenceEqual(png)
            && (await client.GetByteArrayAsync(saved.ImagePath)).SequenceEqual(png),
            "Replacing a service photo serves the new file and preserves existing image references");
    }
    using var other = Client();
    await other.PostAsync($"/test/sign-in/{otherOwner}/{otherSalon}", null);
    using (var otherForm = Form(name: "Other salon service")) Check((await other.PostAsync("/api/Admin/services", otherForm)).StatusCode == HttpStatusCode.OK,
        "Another salon saves its own service");
    var filesBeforeForeign = Files();
    using (var foreignEdit = Form()) Check((await other.PutAsync($"/api/Admin/services/{saved.SalonServiceID}", foreignEdit)).StatusCode == HttpStatusCode.NotFound,
        "A salon cannot edit another salon's service");
    Check(Files() == filesBeforeForeign, "Unauthorized record edits do not write image files");
    await db.ExecuteAsync("""
        INSERT dbo.SalonService(SalonServiceID,SalonMasterID,ServiceName,Category,Price,DurationMinutes,ImagePath,IsActive,CreatedUtc)
        VALUES(NEWID(),@salon,'Older active service','Hair',100,30,'',1,'20200101');
        """, new { salon });
    await Deploy(); // Legacy owner services are copied once into the salon-linked haircut catalogue.
    var list = (await client.GetFromJsonAsync<SalonOwnerDashboardCatalogueService[]>($"/api/Admin/services?salonId={otherSalon}&fromDate=2099-01-01"))!;
    Check(list.Length == 2 && list.Any(s => !s.IsActive) && list.Any(s => s.ServiceName == "Older active service")
        && list.All(s => s.ServiceName != "Other salon service"),
        "Reload returns all own services, including old and inactive ones, without accepting salon/date overrides");
    Check(list[0].IsActive && !list[1].IsActive, "Active services are listed before inactive services");
    using var mismatched = Client();
    await mismatched.PostAsync($"/test/sign-in/{owner}/{otherSalon}", null);
    Check((await mismatched.GetAsync("/api/Admin/services")).StatusCode == HttpStatusCode.Forbidden,
        "Owner/salon membership is checked in the database");
    await db.ExecuteAsync("UPDATE dbo.SalonOwner SET IsActive=0 WHERE SalonOwnerID=@owner", new { owner });
    Check((await client.GetAsync("/api/Admin/services")).StatusCode == HttpStatusCode.Forbidden, "Inactive owners cannot read services");
    await db.ExecuteAsync("UPDATE dbo.SalonOwner SET IsActive=1 WHERE SalonOwnerID=@owner", new { owner });
    var filesBeforeFailure = Files();
    await db.ExecuteAsync("CREATE TRIGGER dbo.fixture_service_failure ON dbo.HairCut_Item_List AFTER INSERT AS THROW 50099, 'Fixture failure', 1;");
    using (var failureForm = Form(name: "Rollback service")) Check((await client.PostAsync("/api/Admin/services", failureForm)).StatusCode == HttpStatusCode.InternalServerError,
        "Database write failures are not reported as successful saves");
    Check(Files() == filesBeforeFailure && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List WHERE HairCut_Item_Name='Rollback service'") == 0,
        "Failed writes roll back both the service and its newly uploaded image");
    await db.ExecuteAsync("DROP TRIGGER dbo.fixture_service_failure");
    await Deploy();
    Check((await client.GetFromJsonAsync<SalonOwnerDashboardCatalogueService[]>("/api/Admin/services"))!.Length == 2,
        "Rerunning the SQL deployment preserves saved services");

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
    Check((await client.GetFromJsonAsync<SalonOwnerDashboardCatalogueService[]>("/api/Admin/services"))!.Length == 2,
        "Unassigned haircut menu items are not automatically exposed to a salon");
    var linkScript = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configurationPath)!, "SQL_Link_InfinityHairArts_Services_2026-11-01.sql"));
    await db.ExecuteAsync(linkScript);
    await db.ExecuteAsync(linkScript);
    var linked = (await client.GetFromJsonAsync<SalonOwnerDashboardCatalogueService[]>("/api/Admin/services"))!;
    var existing = linked.Single(item => item.SalonServiceID == haircut);
    Check(linked.Length == 4 && existing.ServiceName == "Existing haircut" && existing.Price == 130
        && existing.Category == "Hair Cut" && existing.ImagePath == $"/Images/HairCutItemListImages/{haircut}.jpeg"
        && existing.DurationMinutes is null && existing.CanEdit,
        "Infinity Hair Arts can edit its real haircut menu with the original ID and unknown duration");
    Check(linked.Any(item => item.SalonServiceID == facial && item.Category == "Services" && item.ImagePath == "")
        && linked.All(item => item.SalonServiceID != deleted),
        "Missing photo/category values are supported and deleted menu items are excluded");
    Check((await other.GetFromJsonAsync<SalonOwnerDashboardCatalogueService[]>("/api/Admin/services"))!
        .All(item => item.SalonServiceID != haircut && item.SalonServiceID != facial),
        "The existing Infinity Hair Arts menu is hidden from other salons");
    await db.ExecuteAsync("UPDATE dbo.HairCut_Item_List SET HairCut_Item_CGST=11.70,HairCut_Item_SGST=11.70,HairCut_Item_Taxable=106.60 WHERE HairCut_Item_ID=@haircut", new { haircut });
    using (var menuEdit = Form(name: "Drimming", image: false, price: "175.75", duration: "30", category: "Beard")) {
        var edited = await client.PutAsync($"/api/Admin/services/{haircut}", menuEdit);
        Check(edited.StatusCode == HttpStatusCode.OK, "An owner can update an assigned haircut menu item");
        var result = (await edited.Content.ReadFromJsonAsync<SalonOwnerDashboardCatalogueService>())!;
        Check(result.SalonServiceID == haircut && result.ServiceName == "Drimming" && result.Price == 175.75m
            && result.Category == "Beard" && result.DurationMinutes == 30 && result.ImagePath == existing.ImagePath,
            "The updated menu response retains its ID and photo and returns edited fields");
    }
    Check(await db.ExecuteScalarAsync<decimal>("SELECT HairCut_Item_Price FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut }) == 175.75m
        && await db.ExecuteScalarAsync<string>("SELECT HairCut_Item_Name FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut }) == "Drimming",
        "Edits persist to HairCut_Item_List rather than a separate owner-service row");
    Check(await db.ExecuteScalarAsync<decimal>("SELECT HairCut_Item_CGST FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut }) == 15.82m
        && await db.ExecuteScalarAsync<decimal>("SELECT HairCut_Item_Taxable FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut }) == 144.11m,
        "Changing price keeps the existing tax proportions consistent without changing bill records");
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_GroupMaster WHERE HairCutItemGroup=N'Beard' AND OrderID IS NOT NULL") == 1,
        "New categories have an order usable by the existing customer menu");
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SalonService WHERE SalonServiceID=@haircut", new { haircut }) == 0,
        "Editing a haircut item does not create a duplicate SalonService");
    Check(await db.ExecuteScalarAsync<string>("SELECT HairCutItemGroup FROM dbo.HairCut_GroupMaster WHERE HairCutItemGroupMaster_ID=@group", new { group }) == "Hair Cut",
        "Changing one item category does not rename the shared category");
    using (var foreign = Form(image: true)) Check((await other.PutAsync($"/api/Admin/services/{haircut}", foreign)).StatusCode == HttpStatusCode.NotFound,
        "Another salon cannot update the linked haircut row");
    using (var noPhoto = Form(name: "Facial", image: false)) Check((await client.PutAsync($"/api/Admin/services/{facial}", noPhoto)).StatusCode == HttpStatusCode.OK,
        "An existing haircut item without a photo can be edited without a replacement");
    using (var deactivate = Form(name: "Drimming", image: false, active: false)) {
        var response = await client.PutAsync($"/api/Admin/services/{haircut}", deactivate);
        Check(response.StatusCode == HttpStatusCode.OK && !(await response.Content.ReadFromJsonAsync<SalonOwnerDashboardCatalogueService>())!.IsActive,
            "Deactivating an assigned haircut item updates its menu availability");
    }
    Check(await db.ExecuteScalarAsync<bool>("SELECT IsDeleted FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@haircut", new { haircut })
        && (await client.GetFromJsonAsync<SalonOwnerDashboardCatalogueService[]>("/api/Admin/services"))!.Any(x => x.SalonServiceID == haircut && !x.IsActive),
        "Inactive haircut items remain visible to the owner for reactivation");
    using (var reactivate = Form(name: "Drimming", image: false)) Check((await client.PutAsync($"/api/Admin/services/{haircut}", reactivate)).StatusCode == HttpStatusCode.OK,
        "An owner can reactivate a haircut item");
    var sourceCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List");
    using (var duplicate = Form(name: "Drimming")) Check((await client.PostAsync("/api/Admin/services", duplicate)).StatusCode == HttpStatusCode.Conflict,
        "Duplicate names are checked against the actual salon haircut menu");
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List") == sourceCount,
        "Rejected duplicates do not create haircut items");
    using (var sameNameOtherSalon = Form(name: "Drimming")) Check((await other.PostAsync("/api/Admin/services", sameNameOtherSalon)).StatusCode == HttpStatusCode.OK,
        "Different salons may each create a haircut item with the same name");
    await Deploy();
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SalonHairCutItem") == 6
        && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_List") == 7
        && (await client.GetFromJsonAsync<SalonOwnerDashboardCatalogueService[]>("/api/Admin/services"))!.Length == 4,
        "Rerunning both migrations preserves the original menu and prevents duplicate links");
    Check((await client.GetFromJsonAsync<SalonOwnerDashboardCatalogueService[]>("/api/Admin/services"))!.Any(item => item.SalonServiceID == haircut && item.ServiceName == "Drimming"),
        "Rerunning migration does not overwrite edited source menu names");
    await db.ExecuteAsync("INSERT dbo.SalonMaster VALUES(NEWID(),1,N'Infinity Hair Arts')");
    try {
        await db.ExecuteAsync(linkScript);
        Check(false, "Ambiguous salon names must require an explicit salon ID");
    }
    catch (SqlException exception) when (exception.Number == 50015) {
        Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SalonHairCutItem") == 6,
            "Ambiguous salon names stop the link migration without changing existing links");
    }
    Console.WriteLine($"{checks} owner service HTTP/SQL checks passed.");
}
finally {
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    SqlConnection.ClearAllPools();
    await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    // Only remove this generated fixture directory directly under the temporary root.
    if (Directory.GetParent(fixtureRoot)?.FullName == tempRoot.TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(fixtureRoot) == database && database.StartsWith("SalonexaServicesChecks_"))
        Directory.Delete(fixtureRoot, recursive: true);
}
