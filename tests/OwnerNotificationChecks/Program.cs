using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;

var configPath = Path.GetFullPath(args.Single());
var config = new ConfigurationBuilder().AddJsonFile(configPath).Build();
var options = new SqlConnectionStringBuilder(config.GetConnectionString("dbconnection"));
var host = options.DataSource.Split('\\', ',')[0];
if (!new[] { ".", "localhost", "127.0.0.1", "(localdb)", Environment.MachineName }.Contains(host, StringComparer.OrdinalIgnoreCase))
    throw new Exception("Only local SQL Server is allowed.");
options.Encrypt = SqlConnectionEncryptOption.Optional;
options.InitialCatalog = "master";
var database = "SalonexaOwnerNotifications_" + Guid.NewGuid().ToString("N");
await using var master = new SqlConnection(options.ConnectionString);
await master.OpenAsync();
await master.ExecuteAsync($"CREATE DATABASE [{database}]");
options.InitialCatalog = database;
WebApplication? app = null;
var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
try
{
    await using var db = new SqlConnection(options.ConnectionString);
    await db.OpenAsync();
    await db.ExecuteAsync("""
        CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, IsActive bit);
        CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier, IsActive bit);
        CREATE TABLE dbo.CustomerRegistration(CustomerID uniqueidentifier PRIMARY KEY, FirstName nvarchar(150),LastName nvarchar(150));
        CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier,
            SalonMasterID uniqueidentifier, BookingDate date, SeatCount int, BookingStatusMasterID int);
        CREATE TABLE dbo.TimeAllocation(TimeAllocationID uniqueidentifier PRIMARY KEY, FullTiming time);
        CREATE TABLE dbo.CustomerTimeSelection(SeatBookingDetailsID uniqueidentifier,TimeAllocationID uniqueidentifier);
        """);
    var owner = Guid.NewGuid(); var salon = Guid.NewGuid(); var otherSalon = Guid.NewGuid(); var secondOwner = Guid.NewGuid();
    var otherOwner = Guid.NewGuid(); var customer = Guid.NewGuid(); var slot = Guid.NewGuid(); var historical = Guid.NewGuid();
    await db.ExecuteAsync("""
        INSERT dbo.SalonMaster VALUES(@salon,1),(@otherSalon,1);
        INSERT dbo.SalonOwner VALUES(@owner,@salon,1),(@secondOwner,@salon,1),(@otherOwner,@otherSalon,1);
        INSERT dbo.CustomerRegistration VALUES(@customer,N'Test',N'Customer');
        INSERT dbo.TimeAllocation VALUES(@slot,'15:30');
        INSERT dbo.SeatBookingDetails VALUES(@historical,@customer,@salon,'2026-11-01',1,2);
        """, new { owner,salon,otherSalon,secondOwner,otherOwner,customer,slot,historical });
    var sql = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configPath)!, "Database", "OwnerBookingNotifications.sql"));
    for (var run=0;run<2;run++)
        foreach(var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline|RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingNotification")==0, "Rerunnable migration does not notify historical bookings");
    var repoConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["ConnectionStrings:dbconnection"]=options.ConnectionString }).Build();
    var repo = new OwnerBookingNotificationRepository(repoConfig);
    const string token = "fixture-owner-device-token";
    await repo.RegisterAsync(owner,salon,token,default);
    await repo.RegisterAsync(owner,salon,token,default);
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.OwnerPushDevice")==1,"Device registration is idempotent");
    async Task<Guid> Draft(Guid? salonId=null) {
        var id=Guid.NewGuid();
        await db.ExecuteAsync("INSERT dbo.SeatBookingDetails VALUES(@id,@customer,@salon,'2026-11-01',2,1); INSERT dbo.CustomerTimeSelection VALUES(@id,@slot)",
            new {id,customer,salon=salonId??salon,slot}); return id;
    }
    var booking=await Draft();
    Check((await repo.GetAsync(owner,salon,default)).UnreadCount==0,"Temporary slot holds do not notify owners");
    await db.ExecuteAsync("UPDATE dbo.SeatBookingDetails SET BookingStatusMasterID=2 WHERE SeatBookingDetailsID=@booking",new {booking});
    var inbox=await repo.GetAsync(owner,salon,default);
    Check(inbox.UnreadCount==1 && inbox.Items.Single().CustomerName=="Test Customer" && inbox.Items[0].StartTime==new TimeSpan(15,30,0),"Confirmed booking saves customer, date, time and unread alert");
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingPushDelivery")==1,"Confirmation atomically queues phone delivery");
    await db.ExecuteAsync("UPDATE dbo.SeatBookingDetails SET BookingStatusMasterID=2 WHERE SeatBookingDetailsID=@booking",new {booking});
    Check((await repo.GetAsync(owner,salon,default)).UnreadCount==1,"Repeated confirmation creates no duplicate alert");
    Check((await repo.GetAsync(otherOwner,otherSalon,default)).Items.Count==0,"Other salons cannot see booking alerts");
    var cancelled=await Draft();
    using(var tx=db.BeginTransaction()) {
        await db.ExecuteAsync("UPDATE dbo.SeatBookingDetails SET BookingStatusMasterID=2 WHERE SeatBookingDetailsID=@cancelled",new {cancelled},tx); tx.Rollback();
    }
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingNotification")==1 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingPushDelivery")==1,"Failed confirmation rolls back inbox and push outbox");
    await repo.ReadAsync(owner,salon,inbox.Items[0].NotificationID,default);
    await repo.ReadAsync(owner,salon,inbox.Items[0].NotificationID,default);
    Check((await repo.GetAsync(owner,salon,default)).UnreadCount==0,"Mark read is persisted and idempotent");
    Check((await repo.GetAsync(secondOwner,salon,default)).UnreadCount==1,"Read state belongs to each owner");
    var claimed=await repo.ClaimAsync(default);
    Check(claimed.Count==1 && await repo.IsCurrentAsync(claimed[0],default),"Worker claims an authorized delivery");
    Check((await repo.ClaimAsync(default)).Count==0,"Concurrent worker cannot reclaim an active delivery lease");
    await repo.FinishAsync(claimed[0],false,false,"Transient failure",default);
    await db.ExecuteAsync("UPDATE dbo.OwnerBookingPushDelivery SET NextAttemptUtc=DATEADD(second,-1,SYSUTCDATETIME())");
    var retry=(await repo.ClaimAsync(default)).Single();
    await repo.FinishAsync(claimed[0],true,false,null,default);
    Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.OwnerBookingPushDelivery WHERE SentUtc IS NOT NULL")==0,"Expired worker cannot complete a replacement lease");
    await repo.FinishAsync(retry,true,false,null,default);
    Check((await repo.ClaimAsync(default)).Count==0,"Successful phone deliveries are not sent again");
    var next=await Draft();
    await db.ExecuteAsync("UPDATE dbo.SeatBookingDetails SET BookingStatusMasterID=2 WHERE SeatBookingDetailsID=@next",new {next});
    Check((await repo.GetAsync(owner,salon,default)).UnreadCount==1,"Bookings arriving after mark read stay unread");
    await db.ExecuteAsync("UPDATE dbo.OwnerBookingPushDelivery SET NextAttemptUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SentUtc IS NULL");
    var rebound=(await repo.ClaimAsync(default)).Single();
    await repo.RegisterAsync(otherOwner,otherSalon,token,default);
    Check(!await repo.IsCurrentAsync(rebound,default),"Switching device owners invalidates queued deliveries from the old salon");
    await repo.RegisterAsync(owner,salon,token,default);
    Check((await repo.ClaimAsync(default)).Count==0,"Rebinding a device does not revive old delivery generations");
    await repo.UnregisterAsync(otherOwner,token,default);
    Check(await db.ExecuteScalarAsync<bool>("SELECT IsActive FROM dbo.OwnerPushDevice")==true,"Another owner cannot unregister this device");
    await repo.UnregisterAsync(owner,token,default);
    Check(!await db.ExecuteScalarAsync<bool>("SELECT IsActive FROM dbo.OwnerPushDevice"),"Sign-out deactivates the owner's device");
    var builder=WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(repo);
    builder.Services.AddDistributedMemoryCache(); builder.Services.AddSession();
    builder.Services.AddControllers().AddApplicationPart(typeof(OwnerNotificationsController).Assembly);
    app=builder.Build(); app.UseSession();
    app.Use(async (context,next) => {
        if(context.Request.Headers["X-Fixture-Owner"]=="owner") {
            context.Session.SetString("SalonOwnerID",owner.ToString()); context.Session.SetString("SalonMasterID",salon.ToString());
        } else if(context.Request.Headers["X-Fixture-Owner"]=="foreign") {
            context.Session.SetString("SalonOwnerID",otherOwner.ToString()); context.Session.SetString("SalonMasterID",salon.ToString());
        }
        await next();
    }); app.MapControllers(); await app.StartAsync();
    using var http=new HttpClient { BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    Check((await http.GetAsync("/api/Admin/notifications")).StatusCode==HttpStatusCode.Unauthorized,"Anonymous inbox requests require owner sign-in");
    http.DefaultRequestHeaders.Add("X-Fixture-Owner","foreign");
    Check((await http.GetAsync("/api/Admin/notifications")).StatusCode==HttpStatusCode.Forbidden,"Forged owner/salon session cannot read inbox");
    http.DefaultRequestHeaders.Remove("X-Fixture-Owner"); http.DefaultRequestHeaders.Add("X-Fixture-Owner","owner");
    var response=await http.GetAsync("/api/Admin/notifications");
    Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore==true,"Authenticated inbox is available with no-store caching");
    Check((await http.PostAsJsonAsync("/api/Admin/notifications/register-device",new {token="bad"})).StatusCode==HttpStatusCode.BadRequest,"Invalid device tokens are rejected");
    Check((await http.PostAsJsonAsync("/api/Admin/notifications/read",new {throughID=-1})).StatusCode==HttpStatusCode.BadRequest,"Invalid read boundaries are rejected");
    await db.ExecuteAsync("UPDATE dbo.SalonOwner SET IsActive=0 WHERE SalonOwnerID=@owner",new {owner});
    Check((await http.GetAsync("/api/Admin/notifications")).StatusCode==HttpStatusCode.Forbidden,"Disabled owners cannot access alerts");
    Console.WriteLine($"{checks} owner notification SQL/HTTP checks passed. No push messages were sent.");
}
finally {
    if(app is not null) await app.DisposeAsync();
    SqlConnection.ClearAllPools();
    await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
}
