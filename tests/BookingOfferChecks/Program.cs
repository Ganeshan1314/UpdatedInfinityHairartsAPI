using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Data.SqlClient;
using Admin = SalonexaAdminAPI.Services;
using Request = InfinityHairartsAPI.Modals.AppoinmentModal.ReviewTimeSelectionRequest;

// Runs only against a disposable LocalDB database; no application settings are read.
var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
    root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run from the API repository.");
var settings = new SqlConnectionStringBuilder(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30")
{
    InitialCatalog = "master"
};
await using var master = new SqlConnection(settings.ConnectionString);
await master.OpenAsync();
var database = "SalonexaOfferChecks_" + Guid.NewGuid().ToString("N");
await master.ExecuteAsync($"CREATE DATABASE [{database}]");
settings.InitialCatalog = database;
await using var db = new SqlConnection(settings.ConnectionString);
var content = Directory.CreateTempSubdirectory("salonexa-offer-").FullName;
var apps = new List<WebApplication>();
var checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception("FAIL: " + label);
    checks++;
    Console.WriteLine("PASS: " + label);
}
static async Task<int> SqlError(Func<Task> action)
{
    try { await action(); return 0; }
    catch (SqlException error) { return error.Number; }
}
async Task RunScript(string text)
{
    foreach (var batch in Regex.Split(text, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
}
try
{
    await db.OpenAsync();
    await db.ExecuteAsync("""
        CREATE TABLE dbo.SalonMaster(SalonMasterID uniqueidentifier PRIMARY KEY, SalonName nvarchar(200) NOT NULL,
            ContactNumber nvarchar(20) NULL, IsActive bit NOT NULL);
        CREATE TABLE dbo.SalonOwner(SalonOwnerID uniqueidentifier PRIMARY KEY, SalonMasterID uniqueidentifier, IsActive bit);
        CREATE TABLE dbo.TimeAllocation(TimeAllocationID uniqueidentifier PRIMARY KEY, FullTiming time, IsDeleted bit DEFAULT 0, BookingAvailableStatus bit DEFAULT 1);
        CREATE TABLE dbo.CustomerRegistration(CustomerID uniqueidentifier NULL, MobileNo nvarchar(25), FirstName nvarchar(100),
            LastName nvarchar(100), CreatedOn datetime DEFAULT GETDATE(), IsDeleted bit);
        CREATE TABLE dbo.SeatBookingDetails(SeatBookingDetailsID uniqueidentifier PRIMARY KEY, CustomerID uniqueidentifier, SalonMasterID uniqueidentifier,
            BookingDate date, SeatCount int, BookingStatusMasterID int, ReservationVersion bigint DEFAULT 0, CreatedOn datetime DEFAULT GETDATE());
        CREATE TABLE dbo.CustomerTimeSelection(TimeSelectionID uniqueidentifier PRIMARY KEY DEFAULT NEWID(), CustomerID uniqueidentifier,
            SeatBookingDetailsID uniqueidentifier, TimeAllocationID uniqueidentifier, CreatedOn datetime DEFAULT GETDATE());
        """);
    foreach (var suffix in new[] { "", "_Primary" })
        await db.ExecuteAsync($"""
            CREATE TABLE dbo.HairCut_Item_Bill_Master{suffix}(UniqueBillID uniqueidentifier, SeatBookingDetailsID uniqueidentifier,
                CustomerBillID nvarchar(100), BillIDFormat nvarchar(100), DayBillIDCount int, PaymentType nvarchar(50), ClientName nvarchar(200),
                CreatedDate datetime DEFAULT GETDATE(), CreatedBy uniqueidentifier, IsDeleted bit NULL, BillIDTotalAmount decimal(18,2) NULL,
                IsNextBillClicked nvarchar(20) NULL, IsNextBillClickedTime datetime NULL);
            CREATE TABLE dbo.HairCut_Item_Customer_Bill{suffix}(HairCut_Item_Customer_Unique_ID uniqueidentifier DEFAULT NEWID(),
                UniqueBillID uniqueidentifier, CustomerBillID nvarchar(100), HairCut_Item_ID uniqueidentifier, HairCut_Item_Quantity int,
                HairCut_Item_Total decimal(10,2), HairCut_Item_CGST decimal(10,2), HairCut_Item_SGST decimal(10,2),
                HairCut_Item_Taxable decimal(10,2), Created_Date datetime DEFAULT GETDATE(), Created_By uniqueidentifier);
            """);

    // The two procedures exactly as the live database defines them.
    await db.ExecuteAsync("""
        CREATE procedure [dbo].[SelectInsertPrimaryToConstant]
        @UniqueBillID uniqueidentifier
        as
        begin
        update HairCut_Item_Bill_Master_Primary set IsNextBillClicked='Clicked',IsNextBillClickedTime = GETDATE()
        where UniqueBillID=@UniqueBillID;
        insert into HairCut_Item_Bill_Master
        (UniqueBillID,SeatBookingDetailsID,CustomerBillID,BillIDFormat,DayBillIDCount,PaymentType,ClientName,CreatedDate,CreatedBy)
        select UniqueBillID,SeatBookingDetailsID,CustomerBillID,BillIDFormat,DayBillIDCount,PaymentType,ClientName,CreatedDate,CreatedBy
        from HairCut_Item_Bill_Master_Primary where UniqueBillID=@UniqueBillID;
        insert into HairCut_Item_Customer_Bill
        (HairCut_Item_Customer_Unique_ID,UniqueBillID,CustomerBillID,HairCut_Item_ID,HairCut_Item_Quantity,HairCut_Item_Total,
         HairCut_Item_CGST,HairCut_Item_SGST,HairCut_Item_Taxable,Created_Date,Created_By)
        select HairCut_Item_Customer_Unique_ID,UniqueBillID,CustomerBillID,HairCut_Item_ID,HairCut_Item_Quantity,HairCut_Item_Total,
         HairCut_Item_CGST,HairCut_Item_SGST,HairCut_Item_Taxable,Created_Date,Created_By
        from HairCut_Item_Customer_Bill_Primary where UniqueBillID=@UniqueBillID;
        end
        """);
    await db.ExecuteAsync("""
        create procedure [dbo].[updateBookingStatus]
        @SeatBookingDetailsID uniqueidentifier, @CustomerID uniqueidentifier, @BookingStatusMasterID int
        as
        begin
        update SeatBookingDetails set BookingStatusMasterID = @BookingStatusMasterID
        where SeatBookingDetailsID = @SeatBookingDetailsID and CustomerID = @CustomerID
        end
        """);
    await db.ExecuteAsync("CREATE PROCEDURE dbo.deleteClientSideCartItemsByBill @UniqueBillID uniqueidentifier,@CustomerID uniqueidentifier AS DELETE dbo.HairCut_Item_Customer_Bill_Primary WHERE UniqueBillID=@UniqueBillID AND Created_By=@CustomerID;");
    await RunScript(await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "SalonTimingAvailability.sql")));

    // The real QR scan table and procedure.
    var qrScript = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "BookingQrWorkflow.sql"));
    var qrBatches = Regex.Split(qrScript, @"^\s*GO\s*$", RegexOptions.Multiline);
    await db.ExecuteAsync(qrBatches.Single(b => b.Contains("CREATE TABLE dbo.BookingQrCompletion")));
    await db.ExecuteAsync(qrBatches.Single(b => b.Contains("ADD CompletionStatus")));
    await db.ExecuteAsync("IF OBJECT_ID(N'dbo.completeBookingFromQr') IS NULL EXEC(N'CREATE PROCEDURE dbo.completeBookingFromQr AS BEGIN SET NOCOUNT ON; END');");
    await db.ExecuteAsync(qrBatches.Single(b => b.Contains("ALTER PROCEDURE dbo.completeBookingFromQr")));

    var salon = Guid.NewGuid(); var otherSalon = Guid.NewGuid(); var outsideSalon = Guid.NewGuid();
    var owner = Guid.NewGuid(); var otherOwner = Guid.NewGuid(); var outsideOwner = Guid.NewGuid();
    await db.ExecuteAsync("""
        INSERT dbo.SalonMaster VALUES(@salon,N'Joined salon','90000 00001',1),(@otherSalon,N'Second salon',NULL,1),(@outsideSalon,N'Not joined',NULL,1);
        INSERT dbo.SalonOwner VALUES(@owner,@salon,1),(@otherOwner,@otherSalon,1),(@outsideOwner,@outsideSalon,1);
        """, new { salon, otherSalon, outsideSalon, owner, otherOwner, outsideOwner });
    var slots = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()).ToArray();
    for (var i = 0; i < slots.Length; i++)
        await db.ExecuteAsync("INSERT dbo.TimeAllocation VALUES(@id,@time,0,1)", new { id = slots[i], time = TimeSpan.FromMinutes(8 * 60 + i * 15) });

    var legacy = new System.Data.SqlClient.SqlConnectionStringBuilder
    {
        DataSource = settings.DataSource, InitialCatalog = database, IntegratedSecurity = true, Encrypt = false, TrustServerCertificate = true
    };
    const string secret = "test-only-secret-0123456789-abcdefghijklmnop";
    IConfiguration Config(string? numberKey) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:dbconnection"] = legacy.ConnectionString,
        [BookingOffers.NumberKeySetting] = numberKey
    }).Build();
    var config = Config(secret);
    var reservations = new CustomerReservationStore(config);
    var adminConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:dbconnection"] = settings.ConnectionString
    }).Build();
    var admin = new Admin.BookingOfferService(adminConfig);
    var timings = new SalonTimingAvailability(config);
    foreach (var (who, where) in new[] { (owner, salon), (otherOwner, otherSalon), (outsideOwner, outsideSalon) })
        await timings.SaveAsync(who, where, 0, slots, default);

    var date = CustomerBookingRules.Today.AddDays(1);
    if (date.DayOfWeek == DayOfWeek.Friday) date = date.AddDays(1);
    var nextSlot = 0; var nextMobile = 6000000000L;

    async Task<Guid> Customer(string? mobile = null)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync("INSERT dbo.CustomerRegistration(CustomerID,MobileNo,FirstName,IsDeleted) VALUES(@id,@mobile,N'Test',0)",
            new { id, mobile = mobile ?? (++nextMobile).ToString() });
        return id;
    }
    // A booking waiting on the Cart page: the chosen time is held and the price lines are saved.
    async Task<Pending> Draft(Guid customer, Guid where, decimal price = 130)
    {
        var booking = Guid.NewGuid(); var bill = Guid.NewGuid(); var slot = slots[nextSlot++];
        await db.ExecuteAsync("""
            INSERT dbo.SeatBookingDetails(SeatBookingDetailsID,CustomerID,SalonMasterID,BookingDate,SeatCount,BookingStatusMasterID,CreatedOn)
            VALUES(@booking,@customer,@where,@date,1,1,DATEADD(millisecond,@order*10,GETDATE()));
            INSERT dbo.CustomerTimeSelection(CustomerID,SeatBookingDetailsID,TimeAllocationID) VALUES(@customer,@booking,@slot);
            INSERT dbo.HairCut_Item_Bill_Master_Primary(UniqueBillID,SeatBookingDetailsID,CreatedBy) VALUES(@bill,@booking,@customer);
            INSERT dbo.HairCut_Item_Customer_Bill_Primary(UniqueBillID,HairCut_Item_Quantity,HairCut_Item_Total,Created_By) VALUES(@bill,1,@price,@customer);
            """, new { booking, customer, where, date, bill, slot, price, order = nextSlot });
        return new(customer, booking, bill, slot, where);
    }
    Request RequestFor(Pending p, decimal? discount) => new()
    {
        SeatBookingDetailsID = p.Booking, Version = 0, BookingDate = date.ToString("yyyy-MM-dd"),
        TimeAllocationIDs = [p.Slot], OfferDiscount = discount
    };
    string Confirm(Pending p, decimal? discount, CustomerReservationStore? store = null) =>
        (store ?? reservations).Checkout(p.Customer, p.Bill, RequestFor(p, discount)).Item1;
    BookingOfferQuote Review(Pending p) => reservations.QuoteOffer(p.Customer, p.Bill, null, true);
    BookingOfferQuote General(Guid customer, Guid? where = null) => reservations.QuoteOffer(customer, Guid.Empty, where, false);
    Task<int> Status(Pending p) => db.QuerySingleAsync<int>("SELECT BookingStatusMasterID FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@Booking", p);
    Task<int> Bills(Pending p) => db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_Bill_Master WHERE UniqueBillID=@Bill", p);
    Task<string?> State(Pending p) => db.QuerySingleOrDefaultAsync<string?>(
        "SELECT State FROM dbo.BookingOfferState(SYSUTCDATETIME()) WHERE SeatBookingDetailsID=@Booking", p);
    // Moves the visit into the past, as if that day has arrived.
    Task MoveTo(Pending p, int daysAgo) => db.ExecuteAsync("""
        UPDATE dbo.SeatBookingDetails SET BookingDate=@day WHERE SeatBookingDetailsID=@Booking;
        IF OBJECT_ID(N'dbo.BookingOffer') IS NOT NULL UPDATE dbo.BookingOffer SET BookingDate=@day WHERE SeatBookingDetailsID=@Booking;
        """, new { p.Booking, day = CustomerBookingRules.Today.AddDays(-daysAgo) });
    Task Scan(Pending p) => db.ExecuteAsync("dbo.completeBookingFromQr", new
    {
        SeatBookingDetailsID = p.Booking, TimeAllocationID = p.Slot,
        SalonOwnerID = p.Salon == salon ? owner : p.Salon == otherSalon ? otherOwner : outsideOwner, SalonMasterID = p.Salon
    }, commandType: System.Data.CommandType.StoredProcedure);

    // ----- Before the offer script is run: live safety. -----
    var beforeScript = await Draft(await Customer(), salon);
    Check(Confirm(beforeScript, null) == "Success" && await Status(beforeScript) == 2 && await Bills(beforeScript) == 1,
        "Before the offer script is run, an app that sends no offer confirms bookings as usual");
    var beforeScriptNewApp = await Draft(await Customer(), salon);
    Check(Review(beforeScriptNewApp).Reason == "Off" && Confirm(beforeScriptNewApp, 50) == "Offer Changed"
        && await Status(beforeScriptNewApp) == 1 && await Bills(beforeScriptNewApp) == 0,
        "Before the script, an offer request confirms nothing and shows no offer");

    var script = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "FirstBookingOffer.sql"));
    var code = Regex.Replace(script, @"--.*?$", "", RegexOptions.Multiline);
    Check(!Regex.IsMatch(code, @"CREATE\s+OR\s+ALTER|STRING_AGG|CONCAT_WS|TRANSLATE\s*\(|(?<![LR])TRIM\s*\(", RegexOptions.IgnoreCase),
        "The offer script uses nothing newer than SQL Server 2016 without Service Pack 1");
    var existingBefore = await db.ExecuteScalarAsync<string>("SELECT CONCAT(OBJECT_DEFINITION(OBJECT_ID('dbo.SelectInsertPrimaryToConstant')),OBJECT_DEFINITION(OBJECT_ID('dbo.updateBookingStatus')),OBJECT_DEFINITION(OBJECT_ID('dbo.completeBookingFromQr')))");
    await RunScript(script);
    await RunScript(script);
    Check(existingBefore == await db.ExecuteScalarAsync<string>("SELECT CONCAT(OBJECT_DEFINITION(OBJECT_ID('dbo.SelectInsertPrimaryToConstant')),OBJECT_DEFINITION(OBJECT_ID('dbo.updateBookingStatus')),OBJECT_DEFINITION(OBJECT_ID('dbo.completeBookingFromQr')))"),
        "The script runs twice and leaves the existing booking and scan procedures unchanged");

    var offSettings = await admin.GetSettings(default);
    var offDraft = await Draft(await Customer(), salon);
    Check(!offSettings.IsEnabled && offSettings.LaunchedUtc is null && Review(offDraft).Reason == "Off"
        && Confirm(offDraft, 50) == "Offer Changed" && Confirm(offDraft, null) == "Success",
        "After the script the offer is off: no discount, and normal booking still works");

    // A customer who booked before the offer starts.
    var earlierMobile = "7000000001";
    var earlier = await Customer(earlierMobile);
    Check(Confirm(await Draft(earlier, salon), null) == "Success", "An earlier customer books before the offer starts");

    await admin.SetSalon(salon, true, "tester", default);
    await admin.SetSalon(otherSalon, true, "tester", default);
    var endDate = DateOnly.FromDateTime(CustomerBookingRules.Today.AddDays(30));
    async Task Save(bool on = true, string type = "Fixed", decimal value = 50, decimal? maximum = null, decimal minimum = 100,
        decimal budget = 100000, int limit = 1000, DateOnly? end = null)
    {
        var version = (await admin.GetSettings(default)).Version;
        await admin.SaveSettings(new(on, type, value, maximum, minimum, end ?? endDate, budget, limit, version), "tester", default);
    }
    Check(await SqlError(() => Save(value: 0)) == 50062 && await SqlError(() => Save(budget: 0)) == 50062
        && await SqlError(() => Save(limit: 0)) == 50062 && await SqlError(() => Save(type: "Percent", value: 10)) == 50062
        && await SqlError(() => Save(type: "Percent", value: 101, maximum: 50)) == 50062
        && await SqlError(() => Save(end: DateOnly.FromDateTime(CustomerBookingRules.Today.AddDays(-1)))) == 50062
        && !(await admin.GetSettings(default)).IsEnabled,
        "The offer cannot be switched on without a discount, budget, salon limit, maximum for a percentage, and future end date");
    await Save();
    var launched = await admin.GetSettings(default);
    Check(launched.IsEnabled && launched.LaunchedUtc is not null, "The offer is switched on with valid settings");
    Check(await SqlError(() => admin.SaveSettings(new(true, "Fixed", 500, null, 0, endDate, 999999, 9, launched.Version - 1), "stale", default)) == 50061
        && (await admin.GetSettings(default)).DiscountValue == 50, "An out-of-date settings screen cannot overwrite newer settings");

    // ----- Who gets it. -----
    Check(General(earlier).Reason == "NotNewCustomer", "A customer who booked before the offer started does not get it");
    var earlierCopy = await Customer(earlierMobile);
    Check(General(earlierCopy).Reason == "NotNewCustomer", "Another account with an earlier customer's number does not get it");

    var first = await Customer("7000000002");
    var firstDraft = await Draft(first, salon);
    var quote = Review(firstDraft);
    Check(General(first).IsAvailable && General(first, salon).IsAvailable && quote.IsAvailable && quote.Discount == 50,
        "A new customer is offered the discount, priced by the server for the waiting booking");
    Check(General(first, outsideSalon).Reason == "SalonNotParticipating", "A salon that has not joined shows no offer");
    Check(Confirm(firstDraft, 60) == "Offer Changed" && await Status(firstDraft) == 1 && await Bills(firstDraft) == 0
        && await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.BookingOffer") == 0,
        "A discount that does not match the server's is refused and nothing is confirmed");
    Check(Confirm(firstDraft, 50) == "Success" && await Status(firstDraft) == 2 && await Bills(firstDraft) == 1
        && await State(firstDraft) == "Held", "The booking and its discount are saved together");
    Check(await db.QuerySingleAsync<decimal>("SELECT SUM(HairCut_Item_Total) FROM dbo.HairCut_Item_Customer_Bill WHERE UniqueBillID=@Bill", firstDraft) == 130,
        "The saved service price is not changed");
    Check(Confirm(firstDraft, 50) == "Success" && await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.BookingOffer") == 1
        && await Bills(firstDraft) == 1, "Retrying a confirmed booking adds no second discount");
    var mine = reservations.BookingOffersFor(first);
    Check(mine.Count == 1 && mine[0].AmountToPay == 80 && mine[0].DiscountAmount == 50 && mine[0].State == "Held",
        "The customer sees 80 to pay at the salon");
    var forSalon = await admin.GetForSalon(owner, salon, default);
    Check(forSalon.Count == 1 && forSalon[0].AmountToCollect == 80 && (await admin.GetForSalon(otherOwner, otherSalon, default)).Count == 0,
        "Only the booked salon sees the offer, with 80 to collect, before the visit");
    Check(await SqlError(() => admin.GetForSalon(otherOwner, salon, default)) == 50013, "A salon cannot read another salon's offers");
    var second = await Draft(first, otherSalon);
    Check(Review(second).Reason == "AlreadyHeld" && Confirm(second, 50) == "Offer Changed" && Confirm(second, null) == "Success",
        "While one booking holds the offer, another books at the normal price");

    var oldApp = await Draft(await Customer(), salon);
    Check(Confirm(oldApp, null) == "Success" && await State(oldApp) is null,
        "An older app books at the normal price even when the customer qualifies");

    // ----- Visit scanned in time. -----
    await MoveTo(firstDraft, 1);
    Check(await State(firstDraft) == "Held", "The day after the appointment the offer is still waiting");
    await Scan(firstDraft);
    Check(await State(firstDraft) == "Claim" && General(first).Reason == "NotNewCustomer",
        "A scan by the end of the next day uses the offer and creates a claim");
    Check(General(await Customer("7000000099")).IsAvailable, "Another new number is unaffected");

    // ----- Missed visit (rule B). -----
    var missedCustomer = await Customer();
    var missed = await Draft(missedCustomer, salon);
    Check(Confirm(missed, 50) == "Success", "A second new customer takes the offer");
    var waitingBefore = (await admin.GetSettings(default)).BudgetUsed;
    await MoveTo(missed, 2);
    Check(await State(missed) == "Expired" && (await admin.GetSettings(default)).BudgetUsed == waitingBefore - 50
        && reservations.BookingOffersFor(missedCustomer)[0].AmountToPay == 130,
        "An unscanned discount expires, releases the budget, and shows the normal price");
    var retry = await Draft(missedCustomer, salon);
    Check(Review(retry).IsAvailable && Confirm(retry, 50) == "Success",
        "After a missed visit the same customer gets the offer again");
    await Scan(missed);
    Check(await State(missed) == "Expired" && General(missedCustomer).Reason == "NotNewCustomer",
        "A late scan creates no claim, and a completed visit ends the offer for that customer");

    var india = await db.QuerySingleAsync<DateTime>("SELECT DeadlineUtc FROM dbo.BookingOfferState(SYSUTCDATETIME()) WHERE SeatBookingDetailsID=@Booking", retry);
    Check(india == date.AddDays(2).AddMinutes(-330)
        && await db.QuerySingleAsync<string>("SELECT State FROM dbo.BookingOfferState(DATEADD(second,-1,@india)) WHERE SeatBookingDetailsID=@Booking", new { india, retry.Booking }) == "Held"
        && await db.QuerySingleAsync<string>("SELECT State FROM dbo.BookingOfferState(@india) WHERE SeatBookingDetailsID=@Booking", new { india, retry.Booking }) == "Expired",
        "The scan deadline is midnight in India at the end of the day after the appointment");

    // ----- Account deletion and the same number again. -----
    await db.ExecuteAsync("UPDATE dbo.CustomerRegistration SET MobileNo=N'', FirstName=N'', IsDeleted=1 WHERE CustomerID=@first", new { first });
    var again = await Customer("7000000002");
    Check(General(again).Reason == "NotNewCustomer", "A number that used the offer is recognised after its account is deleted");
    Check(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.BookingOffer WHERE DATALENGTH(CustomerKey)=32") >= 1
        && BookingOffers.CustomerKey(secret, "+91 70000 00002")!.SequenceEqual(BookingOffers.CustomerKey(secret, "7000000002")!)
        && !BookingOffers.CustomerKey(secret + "x", "7000000002")!.SequenceEqual(BookingOffers.CustomerKey(secret, "7000000002")!),
        "Only a keyed code of the number is stored");
    var noKey = new CustomerReservationStore(Config(null));
    var shortKey = new CustomerReservationStore(Config("too-short"));
    var keyless = await Draft(await Customer(), salon);
    Check(noKey.QuoteOffer(keyless.Customer, keyless.Bill, null, true).Reason == "Off" && Confirm(keyless, 50, shortKey) == "Offer Changed"
        && Confirm(keyless, null, noKey) == "Success", "Without a long server secret there is no offer, and booking still works");

    // ----- Amount rules. -----
    var cheap = await Draft(await Customer(), salon, 90);
    Check(Review(cheap).Reason == "BelowMinimum" && Confirm(cheap, 50) == "Offer Changed", "Below the minimum bill there is no discount");
    await Save(type: "Percent", value: 15, maximum: 40, minimum: 0);
    var percent = await Draft(await Customer(), salon, 130);
    var capped = await Draft(await Customer(), salon, 1000);
    Check(Review(percent).Discount == 19 && Review(capped).Discount == 40, "A percentage is rounded down to whole rupees and capped");
    await Save(value: 200, minimum: 0);
    var tiny = await Draft(await Customer(), salon, 120);
    Check(Review(tiny).Discount == 120, "A discount never exceeds the bill");
    await Save(minimum: 100);
    var changed = await Draft(await Customer(), salon);
    var shown = Review(changed).Discount;
    await Save(value: 30);
    Check(shown == 50 && Confirm(changed, shown) == "Offer Changed" && Review(changed).Discount == 30 && Confirm(changed, 30) == "Success",
        "If the offer changes before Confirm, the customer must see the new discount first");
    Check((await admin.GetForSalon(owner, salon, default)).Single(o => o.SeatBookingDetailsID == retry.Booking).DiscountAmount == 50,
        "A confirmed booking keeps its discount when settings change");
    await Save(on: false);
    Check(General(await Customer()).Reason == "Off", "Switching the offer off stops new offers");
    await Save();
    await db.ExecuteAsync("UPDATE dbo.BookingOfferSettings SET EndDate=@yesterday", new { yesterday = CustomerBookingRules.Today.AddDays(-1) });
    Check(General(await Customer()).Reason == "Off", "The offer stops after its end date");
    await Save();
    var lateBooker = await Customer();
    Check(Confirm(await Draft(lateBooker, outsideSalon), null) == "Success", "A new customer first books at a salon that has not joined");
    await Save(on: false);
    await Save();
    Check(General(lateBooker).IsAvailable && await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.BookingOfferEarlierCustomer WHERE CustomerID=@lateBooker", new { lateBooker }) == 0,
        "Switching off and on again does not re-list earlier customers");

    // ----- Budget and salon limit, including two confirmations at once. -----
    var used = (await admin.GetSettings(default)).BudgetUsed;
    await Save(budget: used + 50);
    var raceA = await Draft(await Customer(), salon);
    var raceB = await Draft(await Customer(), otherSalon);
    Check(Review(raceA).Discount == 50 && Review(raceB).Discount == 50, "Two customers are both shown the last offer");
    var results = await Task.WhenAll(Task.Run(() => Confirm(raceA, 50)), Task.Run(() => Confirm(raceB, 50)));
    var final = await admin.GetSettings(default);
    Check(results.Count(r => r == "Success") == 1 && results.Count(r => r == "Offer Changed") == 1
        && final.BudgetUsed == used + 50 && final.BudgetUsed <= final.TotalBudget,
        "Confirming at the same moment gives the last offer to only one; the budget is not exceeded");
    Check(General(await Customer()).Reason == "BudgetUsed", "With the budget used up, no more offers are shown");
    var month = new DateTime(date.Year, date.Month, 1);
    var atSalon = await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.BookingOfferState(SYSUTCDATETIME()) WHERE CountsToLimits=1 AND SalonMasterID=@salon AND BookingDate>=@month AND BookingDate<DATEADD(month,1,@month)", new { salon, month });
    await Save(limit: atSalon);
    var overLimit = await Draft(await Customer(), salon);
    var underLimit = await Draft(await Customer(), otherSalon);
    Check(atSalon > 0 && Review(overLimit).Reason == "SalonLimitReached" && Review(underLimit).IsAvailable,
        "Each salon's monthly limit is separate");
    await Save();
    await admin.SetSalon(salon, false, "tester", default);
    Check(Review(overLimit).Reason == "SalonNotParticipating" && await State(retry) == "Held",
        "A removed salon gives no new offers; confirmed ones remain");
    await admin.SetSalon(salon, true, "tester", default);

    // ----- The Home banner asks before a salon is chosen. -----
    var browsing = await Customer();
    var offerDetails = General(browsing);
    Check(offerDetails.IsAvailable && offerDetails.DiscountType == "Fixed" && offerDetails.DiscountValue == 50
        && offerDetails.MinimumBill == 100 && offerDetails.EndDate == endDate.ToDateTime(TimeOnly.MinValue),
        "Before choosing a salon, a new customer gets the amount and terms to announce");
    await admin.SetSalon(salon, false, "tester", default);
    var oneLeft = General(browsing).IsAvailable;
    await admin.SetSalon(otherSalon, false, "tester", default);
    var noneJoined = General(browsing).Reason;
    await admin.SetSalon(salon, true, "tester", default);
    await admin.SetSalon(otherSalon, true, "tester", default);
    await db.ExecuteAsync("UPDATE dbo.SalonMaster SET IsActive=0 WHERE SalonMasterID IN (@salon,@otherSalon)", new { salon, otherSalon });
    var noneActive = General(browsing).Reason;
    await db.ExecuteAsync("UPDATE dbo.SalonMaster SET IsActive=1 WHERE SalonMasterID IN (@salon,@otherSalon)", new { salon, otherSalon });
    Check(oneLeft && noneJoined == "SalonNotParticipating" && noneActive == "SalonNotParticipating" && General(browsing).IsAvailable,
        "Nothing is announced unless at least one active salon has joined");

    // ----- Claims and repayment. -----
    var claims = await admin.GetClaims("ToCheck", 1, default);
    var claim = claims.Items.Single(c => c.SeatBookingDetailsID == firstDraft.Booking);
    Check(claims.Total == 1 && claim.DiscountAmount == 50 && claim.SalonName == "Joined salon" && claim.ScannedSoonAfterBooking && claim.AccountDeleted,
        "Super Admin sees the claim to check, with warnings");
    Check(await SqlError(() => admin.Review(retry.Booking, "Approve", null, null, "tester", default)) == 50064,
        "A booking without a claim cannot be approved");
    Check(await SqlError(() => admin.Review(firstDraft.Booking, "Paid", "UPI-1", null, "tester", default)) == 50065,
        "A claim must be approved before it is marked paid");
    await admin.Review(firstDraft.Booking, "Approve", null, "Called the customer", "tester", default);
    Check(await SqlError(() => admin.Review(firstDraft.Booking, "Paid", "  ", null, "tester", default)) == 50062, "Paid needs a payment reference");
    await admin.Review(firstDraft.Booking, "Paid", "UPI-1234", null, "tester", default);
    var paid = await admin.GetSettings(default);
    Check(paid.PaidCount == 1 && paid.PaidAmount == 50 && (await admin.GetClaims("Paid", 1, default)).Items.Single().PaymentReference == "UPI-1234"
        && await SqlError(() => admin.Review(firstDraft.Booking, "Reject", null, null, "tester", default)) == 50065,
        "A paid claim is recorded and cannot be changed");

    var rejectCustomer = await Customer("9000000001");   // the first salon's own phone number
    var reject = await Draft(rejectCustomer, salon);
    Check(Confirm(reject, 50) == "Success", "A claim with a suspicious number is booked");
    await MoveTo(reject, 1);
    await Scan(reject);
    Check((await admin.GetClaims("ToCheck", 1, default)).Items.Single(c => c.SeatBookingDetailsID == reject.Booking).UsesSalonPhoneNumber,
        "A customer using the salon's own phone number is flagged");
    var beforeReject = (await admin.GetSettings(default)).BudgetUsed;
    await admin.Review(reject.Booking, "Reject", null, "Not a real visit", "tester", default);
    Check((await admin.GetSettings(default)).BudgetUsed == beforeReject - 50 && General(rejectCustomer).Reason == "NotNewCustomer"
        && await SqlError(() => admin.Review(reject.Booking, "Approve", null, null, "tester", default)) == 50065,
        "A rejected claim releases its budget, stays final, and cannot be claimed again");
    var salons = await admin.GetSalons("", 1, default);
    Check(salons.Total == 3 && salons.Items.Count(s => s.IsParticipating) == 2 && !salons.Items.Last().IsParticipating
        && await SqlError(() => admin.SetSalon(Guid.NewGuid(), true, "tester", default)) == 50063,
        "Participating salons are listed, and an unknown salon is refused");

    var joined = salons.Items.Single(s => s.SalonMasterID == salon);
    Check(joined.PaidAmount == 50 && joined.ToPayAmount == 0 && joined.ToCheckAmount == 0,
        "Each salon shows what is waiting to be checked, owed and repaid");

    // ----- Sign-in protection on the new server addresses. -----
    async Task<HttpClient> Start(Action<WebApplicationBuilder> services, Action<WebApplication> pipeline, System.Reflection.Assembly part)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development", ContentRootPath = content });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(config);
        builder.Configuration["Authentication:KeyRingPath"] = Path.Combine(content, "keys");
        builder.Services.AddDistributedMemoryCache(); builder.Services.AddSession(); builder.Services.AddHttpContextAccessor();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager => manager.ApplicationParts.Clear())
            .AddApplicationPart(part);
        services(builder);
        var app = builder.Build(); pipeline(app); app.MapControllers(); await app.StartAsync(); apps.Add(app);
        return new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    }
    const string testPassword = "test-only-password";
    var account = new Admin.SuperAdminAccount(Guid.NewGuid(), "tester", "Tester",
        new Microsoft.AspNetCore.Identity.PasswordHasher<string>().HashPassword("tester", testPassword),
        Admin.SuperAdminAuthentication.Role, true);
    using var adminClient = await Start(builder =>
    {
        builder.Services.AddSingleton<IConfiguration>(adminConfig);
        builder.Services.AddScoped<Admin.BookingOfferService>();
        builder.Services.AddScoped<Admin.ISuperAdminDashboard, Admin.SuperAdminDashboardService>();
        Admin.SuperAdminAuthentication.Register(builder.Services);
        builder.Services.AddSingleton<Admin.ISuperAdminAccountStore>(new FixtureAdmins(account));
        builder.Services.AddScoped<Admin.ISalonOwnerAuthenticationService, Admin.SalonOwnerAuthenticationService>();
        Admin.SalonOwnerPersistentSignInRegistration.AddSalonOwnerPersistentSignIn(builder.Services, builder.Configuration, builder.Environment);
    }, app =>
    {
        app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
        app.UseRouting(); app.UseRateLimiter(); app.UseMiddleware<Admin.SuperAdminRequestMiddleware>(); app.UseSession();
        app.UseMiddleware<Admin.SalonOwnerSessionMiddleware>(); app.UseAuthorization();
    }, typeof(SalonexaAdminAPI.Controllers.SuperAdminOfferController).Assembly);
    async Task<HttpStatusCode> Call(HttpClient client, HttpMethod method, string path, bool header = true)
    {
        using var message = new HttpRequestMessage(method, path);
        if (header) message.Headers.Add("X-Salonexa-Request", "1");
        if (method != HttpMethod.Get) message.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(message);
        return response.StatusCode;
    }
    foreach (var path in new[] { "/api/SuperAdmin/offer", "/api/SuperAdmin/offer/salons", "/api/SuperAdmin/offer/claims" })
        Check(await Call(adminClient, HttpMethod.Get, path) == HttpStatusCode.Unauthorized, $"Signed-out visitors cannot read {path}");
    Check(await Call(adminClient, HttpMethod.Put, "/api/SuperAdmin/offer") == HttpStatusCode.Unauthorized
        && await Call(adminClient, HttpMethod.Post, $"/api/SuperAdmin/offer/claims/{reject.Booking}/review") == HttpStatusCode.Unauthorized
        && await Call(adminClient, HttpMethod.Put, "/api/SuperAdmin/offer", header: false) == HttpStatusCode.Forbidden,
        "Signed-out visitors cannot change settings or claims, and writes need the application's header");
    Check(await Call(adminClient, HttpMethod.Get, "/api/Admin/offers") == HttpStatusCode.Unauthorized,
        "A signed-out salon owner cannot read offers");

    // ----- A signed-in Super Admin over HTTP: the names and rules the admin screen relies on. -----
    var cookie = string.Empty;
    async Task<(HttpStatusCode Status, string Body)> Signed(HttpMethod method, string path, object? body = null)
    {
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-Salonexa-Request", "1");
        if (cookie.Length > 0) message.Headers.Add("Cookie", cookie);
        if (body is not null) message.Content = System.Net.Http.Json.JsonContent.Create(body);
        using var response = await adminClient.SendAsync(message);
        if (response.Headers.TryGetValues("Set-Cookie", out var values)) cookie = string.Join("; ", values.Select(v => v.Split(';')[0]));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
    static string Names(System.Text.Json.JsonElement element) => string.Join(",", element.EnumerateObject().Select(p => p.Name));
    static System.Text.Json.JsonElement Json(string body) => System.Text.Json.JsonDocument.Parse(body).RootElement;
    Check((await Signed(HttpMethod.Post, "/api/SuperAdmin/login", new { userName = "tester", password = testPassword })).Status == HttpStatusCode.OK,
        "The test Super Admin signs in");
    var read = await Signed(HttpMethod.Get, "/api/SuperAdmin/offer");
    Check(read.Status == HttpStatusCode.OK && Names(Json(read.Body)) == "isEnabled,discountType,discountValue,maximumDiscount,minimumBill,endDate,totalBudget,salonMonthlyLimit,launchedUtc,version,waitingCount,waitingAmount,toCheckCount,toCheckAmount,toPayCount,toPayAmount,paidCount,paidAmount,budgetUsed",
        "Settings use the names the screen reads");
    var salonReply = await Signed(HttpMethod.Get, "/api/SuperAdmin/offer/salons?search=&page=1");
    Check(salonReply.Status == HttpStatusCode.OK && Names(Json(salonReply.Body)) == "total,page,pageSize,items"
        && Names(Json(salonReply.Body).GetProperty("items")[0]) == "salonMasterID,salonName,isActive,isParticipating,usedThisMonth,toCheckAmount,toPayAmount,paidAmount",
        "Salons use the names the screen reads");
    var claimReply = await Signed(HttpMethod.Get, "/api/SuperAdmin/offer/claims?status=Paid&page=1");
    Check(claimReply.Status == HttpStatusCode.OK
        && Names(Json(claimReply.Body).GetProperty("items")[0]) == "seatBookingDetailsID,salonMasterID,salonName,customerName,mobileNo,bookingDate,originalAmount,discountAmount,bookedUtc,scannedUtc,status,paymentReference,reviewNote,scannedSoonAfterBooking,manyOffersSameDay,noCustomerName,usesSalonPhoneNumber,accountDeleted"
        && Json(claimReply.Body).GetProperty("items")[0].GetProperty("scannedUtc").GetString()!.EndsWith('Z'),
        "Claims use the names the screen reads, with times marked as UTC");
    var now = Json(read.Body);
    object Change(int version, string end) => new
    {
        isEnabled = true, discountType = "Fixed", discountValue = 35, maximumDiscount = (decimal?)null, minimumBill = 100,
        endDate = end, totalBudget = 100000, salonMonthlyLimit = 1000, version
    };
    var lastDay = endDate.ToString("yyyy-MM-dd");
    var saved = await Signed(HttpMethod.Put, "/api/SuperAdmin/offer", Change(now.GetProperty("version").GetInt32(), lastDay));
    Check(saved.Status == HttpStatusCode.OK && Json(saved.Body).GetProperty("discountValue").GetDecimal() == 35
        && Json(saved.Body).GetProperty("endDate").GetString()!.StartsWith(lastDay), "The screen's save request is accepted and returns the new settings");
    var stale = await Signed(HttpMethod.Put, "/api/SuperAdmin/offer", Change(now.GetProperty("version").GetInt32(), lastDay));
    var past = await Signed(HttpMethod.Put, "/api/SuperAdmin/offer",
        Change(Json(saved.Body).GetProperty("version").GetInt32(), CustomerBookingRules.Today.AddDays(-1).ToString("yyyy-MM-dd")));
    Check(stale.Status == HttpStatusCode.Conflict && past.Status == HttpStatusCode.BadRequest
        && Json(stale.Body).GetProperty("message").GetString()!.Length > 0 && Json(past.Body).GetProperty("message").GetString()!.Length > 0,
        "Old settings get a conflict, and invalid ones a message to show");
    Check((await Signed(HttpMethod.Put, $"/api/SuperAdmin/offer/salons/{outsideSalon}", new { isParticipating = true })).Status == HttpStatusCode.NoContent
        && (await Signed(HttpMethod.Post, $"/api/SuperAdmin/offer/claims/{firstDraft.Booking}/review", new { action = "Reject", paymentReference = (string?)null, note = (string?)null })).Status == HttpStatusCode.Conflict
        && (await Signed(HttpMethod.Get, "/api/SuperAdmin/offer/claims?status=Nope")).Status == HttpStatusCode.BadRequest,
        "Salon changes are saved, and refused claim changes are reported");

    using var customerClient = await Start(builder => builder.Services.AddMemoryCache().AddSingleton<CustomerSessionRegistry>(), app =>
    {
        app.UseRouting(); app.UseSession(); app.UseMiddleware<CustomerApiSessionMiddleware>();
    }, typeof(InfinityHairartsAPI.Controllers.AppoinmentController).Assembly);
    Check(await Call(customerClient, HttpMethod.Get, "/api/Appoinment/firstBookingOffer") == HttpStatusCode.Unauthorized
        && await Call(customerClient, HttpMethod.Get, "/api/Appoinment/firstBookingOfferBookings") == HttpStatusCode.Unauthorized,
        "Only a signed-in customer can read their offer");

    Console.WriteLine($"Passed {checks} first-booking offer checks on a temporary LocalDB database.");
}
finally
{
    foreach (var app in apps) { await app.StopAsync(); await app.DisposeAsync(); }
    await db.CloseAsync();
    SqlConnection.ClearAllPools();
    System.Data.SqlClient.SqlConnection.ClearAllPools();
    await master.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    try { Directory.Delete(content, true); } catch (IOException) { }
}

sealed record Pending(Guid Customer, Guid Booking, Guid Bill, Guid Slot, Guid Salon);
sealed class FixtureAdmins(Admin.SuperAdminAccount account) : Admin.ISuperAdminAccountStore
{
    public Task<Admin.SuperAdminAccount?> FindByUserNameAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult<Admin.SuperAdminAccount?>(userName == account.UserName ? account : null);
    public Task<Admin.SuperAdminAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<Admin.SuperAdminAccount?>(id == account.SuperAdminID ? account : null);
    public Task<bool> MarkLoginAsync(Admin.SuperAdminAccount signedIn, CancellationToken cancellationToken) => Task.FromResult(true);
}
