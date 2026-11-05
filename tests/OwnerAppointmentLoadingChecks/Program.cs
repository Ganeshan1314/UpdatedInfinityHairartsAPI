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
    while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln"))) root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
    var source = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "InfinityHairartsAPI", "appsettings.json")).Build();
    var settings = new SqlConnectionStringBuilder(source.GetConnectionString("dbconnection"));
    if (!new[] { ".", "localhost", "127.0.0.1", "(localdb)", Environment.MachineName }.Contains(settings.DataSource.Split('\\', ',')[0], StringComparer.OrdinalIgnoreCase))
        throw new Exception("Appointment checks only use a disposable local SQL database.");
    settings.Encrypt = SqlConnectionEncryptOption.Optional; settings.InitialCatalog = "master";
    await using var master = new SqlConnection(settings.ConnectionString); await master.OpenAsync();
    var database = "SalonexaAppointmentChecks_" + Guid.NewGuid().ToString("N");
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
            CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier,
                SalonMasterID uniqueidentifier, BookingDate date, SeatCount int, BookingStatusMasterID int, CompletionStatus nvarchar(16));
            CREATE TABLE dbo.CustomerRegistration(CustomerID uniqueidentifier PRIMARY KEY, FirstName nvarchar(80),LastName nvarchar(80),MobileNo nvarchar(20));
            CREATE TABLE dbo.TimeAllocation(TimeAllocationID uniqueidentifier PRIMARY KEY,FullTiming time);
            CREATE TABLE dbo.CustomerTimeSelection(SeatBookingDetailsID uniqueidentifier,CustomerID uniqueidentifier,TimeAllocationID uniqueidentifier);
            CREATE TABLE dbo.HairCut_Item_Bill_Master(UniqueBillID uniqueidentifier PRIMARY KEY,SeatBookingDetailsID uniqueidentifier,BillIDTotalAmount decimal(10,2),IsDeleted bit);
            """);
        var migration = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "SalonOwnerAppointmentsRead.sql"));
        async Task Migrate() { foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch); }
        await Migrate(); await Migrate();
        var salon = Guid.NewGuid(); var otherSalon = Guid.NewGuid(); var emptySalon = Guid.NewGuid();
        var owner = Guid.NewGuid(); var otherOwner = Guid.NewGuid(); var emptyOwner = Guid.NewGuid();
        var customer = Guid.NewGuid(); var morning = Guid.NewGuid(); var later = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.SalonMaster VALUES(@salon,1),(@otherSalon,1),(@emptySalon,1);
            INSERT dbo.SalonOwner VALUES(@owner,@salon,1),(@otherOwner,@otherSalon,1),(@emptyOwner,@emptySalon,1);
            INSERT dbo.CustomerRegistration VALUES(@customer,' Test ','Customer ','0000000000');
            INSERT dbo.TimeAllocation VALUES(@morning,'09:00'),(@later,'10:00');
            """, new { salon, otherSalon, emptySalon, owner, otherOwner, emptyOwner, customer, morning, later });
        async Task<Guid> Booking(Guid targetSalon, string status, DateTime date, int confirmed = 2, Guid? targetCustomer = null, bool times = true) {
            var id = Guid.NewGuid(); var person = targetCustomer ?? customer;
            await db.ExecuteAsync("INSERT dbo.SeatBookingDetails VALUES(@id,@person,@targetSalon,@date,2,@confirmed,@status)", new { id, person, targetSalon, date, confirmed, status });
            if (times) await db.ExecuteAsync("INSERT dbo.CustomerTimeSelection VALUES(@id,@person,@morning),(@id,@person,@later)", new { id, person, morning, later });
            return id;
        }
        for (var day = 1; day <= 12; day++) await Booking(salon, "Pending", new DateTime(2026, 10, day));
        for (var i = 0; i < 2; i++) await Booking(salon, "InProgress", new DateTime(2026, 10, 5));
        var paid = await Booking(salon, "Completed", new DateTime(2026, 10, 5));
        var free = await Booking(salon, "Completed", new DateTime(2026, 10, 5));
        var notBilled = await Booking(salon, "Completed", new DateTime(2026, 10, 5), targetCustomer: Guid.NewGuid(), times: false);
        var otherBooking = await Booking(otherSalon, "Pending", new DateTime(2026, 11, 1));
        await Booking(salon, "Pending", new DateTime(2026, 11, 2), confirmed: 1);
        await Booking(salon, "Pending", new DateTime(2026, 11, 3), confirmed: 3);
        await db.ExecuteAsync("""
            INSERT dbo.HairCut_Item_Bill_Master VALUES(NEWID(),@paid,100.50,0),(NEWID(),@paid,25,0),(NEWID(),@paid,900,1),(NEWID(),@free,0,0);
            """, new { paid, free });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["ConnectionStrings:dbconnection"] = settings.ConnectionString,
            ["Authentication:KeyRingPath"] = Path.Combine(AppContext.BaseDirectory,"fixture-keys")
        }).Build();
        var reader = new SalonOwnerAppointmentsReader(config); var checks = 0;
        void Check(bool value,string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
        Task<SalonOwnerAppointmentPage> Read(string status = "Pending", int page = 1, DateTime? from = null, DateTime? to = null) => reader.GetAsync(owner,salon,from,to,status,page,default);
        var all = await Read();
        Check(all.Items.Count == 10 && all.TotalCount == 12 && all.PageSize == 10 && all.Page == 1, "Latest appointments load ten confirmed bookings per page across all history");
        Check(all.Counts.Pending == 12 && all.Counts.InProgress == 2 && all.Counts.Completed == 3, "Status counts cover the full confirmed history without other salons, drafts or cancelled bookings");
        Check(all.Items.Select(b => b.BookingDate).SequenceEqual(Enumerable.Range(3,10).Reverse().Select(day => new DateTime(2026,10,day))), "Unfiltered appointments show newest dates first");
        Check(all.Items.All(b => b.SeatBookingDetailsID != otherBooking && b.SeatCount == 2 && b.StartTime == TimeSpan.FromHours(9)), "Multiple time selections keep a single booking with the earliest appointment time");
        Check(all.Items.All(b => b.CustomerName.StartsWith("Test") && b.MobileNo == "0000000000"), "Customer contact details are returned in the existing frontend format");
        var second = await Read(page: 2);
        Check(second.Items.Count == 2 && !second.Items.Select(b => b.SeatBookingDetailsID).Intersect(all.Items.Select(b => b.SeatBookingDetailsID)).Any(), "Pagination does not repeat appointments");
        var beyond = await Read(page: int.MaxValue);
        Check(beyond.Items.Count == 0 && beyond.TotalCount == 12, "A large out-of-range page does not overflow or lose totals");
        var range = await Read(from: new(2026,10,3), to: new(2026,10,5));
        Check(range.Items.Count == 3 && range.TotalCount == 3 && range.Counts.InProgress == 2 && range.Counts.Completed == 3, "Both date boundaries are included and all status counts share the selected range");
        Check(range.Items.Select(b => b.BookingDate.Day).SequenceEqual([3,4,5]), "Filtered appointments follow chronological date order");
        Check((await Read("InProgress")).Items.Count == 2, "In-progress appointments use their saved status");
        var completed = await Read("Completed");
        Check(completed.Items.Single(b => b.SeatBookingDetailsID == paid).BillAmount == 125.50m, "Completed amounts sum non-deleted bills without multiplying by time selections");
        Check(completed.Items.Single(b => b.SeatBookingDetailsID == free).BillAmount == 0 && completed.Items.Single(b => b.SeatBookingDetailsID == notBilled).BillAmount is null, "A zero-amount bill remains distinct from a missing bill");
        Check(completed.Items.Single(b => b.SeatBookingDetailsID == notBilled).CustomerName == "Guest customer" && completed.Items.Single(b => b.SeatBookingDetailsID == notBilled).StartTime is null, "Missing customer/time details do not break appointment loading");
        var empty = await Read(from: new(2026,11,1), to: new(2026,11,3));
        Check(empty.Items.Count == 0 && empty.Counts.Pending == 0 && empty.TotalCount == 0, "An empty range returns zero counts rather than a missing endpoint");
        try { await reader.GetAsync(otherOwner,salon,null,null,"Pending",1,default); throw new Exception("Expected isolation failure"); }
        catch (SqlException error) when (error.Number == 50013) { Check(true,"Database ownership checks prevent cross-salon reads"); }
        foreach (var invalid in new Func<Task>[] { () => Read("Unknown"), () => Read(page: 0), () => Read(from: new(2026,10,1)), () => Read(from: new(2026,10,5),to: new(2026,10,1)), () => Read(from: new(1999,1,1),to: new(2026,10,1)) }) {
            try { await invalid(); throw new Exception("Expected validation failure"); } catch (ArgumentException) { Check(true,"Invalid filter/status/page is rejected before SQL"); }
        }
        await Migrate();
        Check((await Read()).TotalCount == 12 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeatBookingDetails") == 20, "Rerunning the migration does not modify bookings or saved statuses");

        var owners = new FixtureOwners([(owner,salon),(otherOwner,otherSalon),(emptyOwner,emptySalon)]);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Configuration.AddConfiguration(config);
        builder.Services.AddDistributedMemoryCache(); builder.Services.AddSession(); builder.Services.AddSingleton<ISalonOwnerAuthenticationService>(owners);
        builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration,builder.Environment); builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton(reader); builder.Services.AddControllers().AddApplicationPart(typeof(SalonOwnerAppointmentsController).Assembly);
        app = builder.Build(); app.Use(async (ctx,next) => { ctx.Request.Scheme="https"; await next(ctx); });
        app.UseSession(); app.UseMiddleware<SalonOwnerSessionMiddleware>(); app.MapControllers();
        app.MapPost("/fixture/login/{index:int}",async (int index,HttpContext ctx,SalonOwnerPersistentSignIn signIn) => { await signIn.SignInAsync(ctx,owners.Items[index]); return Results.NoContent(); });
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { UseCookies=false }) { BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var cookies=new CookieContainer(); var origin=new Uri("https://localhost");
        async Task<HttpResponseMessage> Send(HttpMethod method,string path) {
            using var request=new HttpRequestMessage(method,path); request.Headers.TryAddWithoutValidation("Cookie",cookies.GetCookieHeader(origin));
            var response=await client.SendAsync(request);
            if(response.Headers.TryGetValues("Set-Cookie",out var values)) foreach(var value in values) cookies.SetCookies(origin,value);
            return response;
        }
        const string route="/api/Admin/appointments";
        using(var response=await Send(HttpMethod.Get,route)) Check(response.StatusCode==HttpStatusCode.Unauthorized,"The restored appointment route returns 401 to anonymous users");
        using(var response=await Send(HttpMethod.Post,"/fixture/login/0")) Check(response.IsSuccessStatusCode,"The fixture owner can sign in with a protected ticket");
        using(var response=await Send(HttpMethod.Get,route)) {
            var result=await response.Content.ReadFromJsonAsync<SalonOwnerAppointmentPage>();
            Check(response.StatusCode==HttpStatusCode.OK && result?.Items.Count==10 && result.Counts.Pending==12,"The actual HTTP endpoint returns the appointment page expected by the app");
            Check(response.Headers.CacheControl?.NoStore==true,"Owner-specific appointment responses cannot be cached");
        }
        using(var response=await Send(HttpMethod.Get,route+"?status=Completed&fromDate=2026-10-05&toDate=2026-10-05&page=1")) Check(response.StatusCode==HttpStatusCode.OK && (await response.Content.ReadFromJsonAsync<SalonOwnerAppointmentPage>())?.Items.Count==3,"HTTP status and date filters bind correctly");
        using(var response=await Send(HttpMethod.Get,route+"?salonMasterID="+otherSalon+"&pageSize=1000")) {
            var result=await response.Content.ReadFromJsonAsync<SalonOwnerAppointmentPage>();
            Check(result?.TotalCount==12 && result.PageSize==10,"Client salon/page-size overrides cannot expose other owners or change pagination");
        }
        foreach(var query in new[] { "?page=0","?status=Unknown","?fromDate=2026-10-01","?fromDate=2026-10-05&toDate=2026-10-01","?fromDate=invalid&toDate=2026-10-01" })
            using(var response=await Send(HttpMethod.Get,route+query)) Check(response.StatusCode==HttpStatusCode.BadRequest,"Invalid HTTP query parameters return 400");
        await db.ExecuteAsync("UPDATE dbo.SalonMaster SET IsActive=0 WHERE SalonMasterID=@salon",new { salon });
        using(var response=await Send(HttpMethod.Get,route)) Check(response.StatusCode==HttpStatusCode.Forbidden,"Database checks reject a salon disabled after authentication");
        using(var response=await Send(HttpMethod.Post,"/fixture/login/1")) Check(response.IsSuccessStatusCode,"Another salon owner can sign in");
        using(var response=await Send(HttpMethod.Get,route)) Check((await response.Content.ReadFromJsonAsync<SalonOwnerAppointmentPage>())?.Items.Single().SeatBookingDetailsID==otherBooking,"Changing owners changes the appointment scope");
        using(var response=await Send(HttpMethod.Post,"/fixture/login/2")) Check(response.IsSuccessStatusCode,"An owner of an empty salon can sign in");
        using(var response=await Send(HttpMethod.Get,route)) Check(response.StatusCode==HttpStatusCode.OK && (await response.Content.ReadFromJsonAsync<SalonOwnerAppointmentPage>())?.Items.Count==0,"An empty salon receives a successful empty page");
        await db.ExecuteAsync("DROP PROCEDURE dbo.GetSalonOwnerAppointments");
        using(var response=await Send(HttpMethod.Get,route)) Check(response.StatusCode==HttpStatusCode.ServiceUnavailable,"A missing database migration reports 503 rather than false empty appointments");
        Console.WriteLine($"{checks} appointment-loading checks passed.");
    }
    finally {
        if(app is not null) await app.DisposeAsync(); await db.CloseAsync(); SqlConnection.ClearAllPools();
        await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    }
}

sealed class FixtureOwners((Guid Owner,Guid Salon)[] owners):ISalonOwnerAuthenticationService
{
    public SalonOwnerLoginResponse[] Items { get; }=owners.Select(pair=>new SalonOwnerLoginResponse { SalonOwnerID=pair.Owner,SalonMasterID=pair.Salon,OwnerName="Appointment fixture",UserName="fixture",SalonName="Fixture salon",AuthenticationStamp="fixture-version" }).ToArray();
    public Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(Guid id,CancellationToken cancellationToken)=>Task.FromResult(Items.SingleOrDefault(owner=>owner.SalonOwnerID==id));
    public Task<SalonOwnerLoginResponse?> AuthenticateAsync(SalonOwnerLoginRequest request,CancellationToken cancellationToken)=>throw new NotSupportedException();
    public Task<SalonOwnerLoginResponse> InitializeAsync(InitializeSalonOwnerRequest request,CancellationToken cancellationToken)=>throw new NotSupportedException();
}
