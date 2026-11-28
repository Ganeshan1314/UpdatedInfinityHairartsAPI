-- First-booking offer. The platform pays: salons collect less from the customer and are repaid.
-- Works on SQL Server 2016 without Service Pack 1, which has no "create or alter" statement.
-- Running this script changes no existing table, procedure or customer data. It adds new tables,
-- one function and new procedures. The offer is OFF until it is switched on in Super Admin.
-- Run it on the application database. It can be run again; saved settings and offers are kept.
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.BookingOfferSettings', N'U') IS NULL
    CREATE TABLE dbo.BookingOfferSettings
    (
        SettingsID tinyint NOT NULL CONSTRAINT PK_BookingOfferSettings PRIMARY KEY
            CONSTRAINT CK_BookingOfferSettings_OneRow CHECK (SettingsID = 1),
        IsEnabled bit NOT NULL,
        DiscountType nvarchar(10) NOT NULL
            CONSTRAINT CK_BookingOfferSettings_Type CHECK (DiscountType IN (N'Fixed', N'Percent')),
        DiscountValue decimal(10,2) NOT NULL,
        MaximumDiscount decimal(10,2) NULL,
        MinimumBill decimal(10,2) NOT NULL,
        EndDate date NULL,
        TotalBudget decimal(12,2) NOT NULL,
        SalonMonthlyLimit int NOT NULL,
        LaunchedUtc datetime2(0) NULL,
        Version int NOT NULL,
        UpdatedUtc datetime2(0) NOT NULL,
        UpdatedBy nvarchar(100) NULL
    );
GO

IF NOT EXISTS (SELECT 1 FROM dbo.BookingOfferSettings)
    INSERT dbo.BookingOfferSettings(SettingsID, IsEnabled, DiscountType, DiscountValue, MaximumDiscount,
        MinimumBill, EndDate, TotalBudget, SalonMonthlyLimit, LaunchedUtc, Version, UpdatedUtc, UpdatedBy)
    VALUES (1, 0, N'Fixed', 0, NULL, 0, NULL, 0, 0, NULL, 0, SYSUTCDATETIME(), NULL);
GO

-- Only salons listed here take part.
IF OBJECT_ID(N'dbo.BookingOfferSalon', N'U') IS NULL
    CREATE TABLE dbo.BookingOfferSalon
    (
        SalonMasterID uniqueidentifier NOT NULL CONSTRAINT PK_BookingOfferSalon PRIMARY KEY,
        AddedUtc datetime2(0) NOT NULL CONSTRAINT DF_BookingOfferSalon_Added DEFAULT SYSUTCDATETIME(),
        AddedBy nvarchar(100) NULL
    );
GO

-- Everyone who had confirmed a booking when the offer was first switched on.
IF OBJECT_ID(N'dbo.BookingOfferEarlierCustomer', N'U') IS NULL
    CREATE TABLE dbo.BookingOfferEarlierCustomer
    (
        CustomerID uniqueidentifier NOT NULL CONSTRAINT PK_BookingOfferEarlierCustomer PRIMARY KEY
    );
GO

-- One row for each booking confirmed with the offer. CustomerKey is a keyed code of the mobile
-- number (the key is a server setting, not stored here), so a number that already used the offer is
-- recognised even after account deletion erases the number itself.
IF OBJECT_ID(N'dbo.BookingOffer', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BookingOffer
    (
        SeatBookingDetailsID uniqueidentifier NOT NULL CONSTRAINT PK_BookingOffer PRIMARY KEY,
        CustomerID uniqueidentifier NOT NULL,
        CustomerKey varbinary(32) NOT NULL,
        SalonMasterID uniqueidentifier NOT NULL,
        BookingDate date NOT NULL,
        OriginalAmount decimal(10,2) NOT NULL,
        DiscountAmount decimal(10,2) NOT NULL,
        CreatedUtc datetime2(0) NOT NULL CONSTRAINT DF_BookingOffer_Created DEFAULT SYSUTCDATETIME(),
        ReviewStatus nvarchar(10) NULL
            CONSTRAINT CK_BookingOffer_Review CHECK (ReviewStatus IN (N'Approved', N'Rejected', N'Paid')),
        ReviewedUtc datetime2(0) NULL,
        ReviewedBy nvarchar(100) NULL,
        PaymentReference nvarchar(100) NULL,
        ReviewNote nvarchar(300) NULL,
        CONSTRAINT CK_BookingOffer_Amounts CHECK (DiscountAmount > 0 AND DiscountAmount <= OriginalAmount)
    );
    CREATE INDEX IX_BookingOffer_Customer ON dbo.BookingOffer(CustomerID);
    CREATE INDEX IX_BookingOffer_CustomerKey ON dbo.BookingOffer(CustomerKey);
    CREATE INDEX IX_BookingOffer_Salon ON dbo.BookingOffer(SalonMasterID, BookingDate);
END;
GO

-- Each offer's state is worked out from the booking and its QR scan, so the existing booking and
-- scan procedures stay unchanged.
--   Claim     scanned in time: the offer is used, and the salon may be owed the discount.
--   Held      waiting for the visit. The customer cannot hold a second one.
--   Expired   not scanned by the end of the day after the appointment (India time).
--   Cancelled the booking is no longer confirmed.
-- CountsToLimits marks the rows that use up the total budget and the salon's monthly limit.
IF OBJECT_ID(N'dbo.BookingOfferState', N'IF') IS NULL
    EXEC (N'CREATE FUNCTION dbo.BookingOfferState(@NowUtc datetime2(0)) RETURNS TABLE AS RETURN (SELECT 1 AS Placeholder);');
GO

ALTER FUNCTION dbo.BookingOfferState(@NowUtc datetime2(0))
RETURNS TABLE
AS RETURN
(
    SELECT o.SeatBookingDetailsID, o.CustomerID, o.CustomerKey, o.SalonMasterID, o.BookingDate,
        o.OriginalAmount, o.DiscountAmount, o.CreatedUtc, o.ReviewStatus, o.ReviewedUtc, o.ReviewedBy,
        o.PaymentReference, o.ReviewNote, scan.FirstScanUtc, deadline.DeadlineUtc, result.State,
        CountsToLimits = CONVERT(bit, CASE
            WHEN result.State = N'Held' THEN 1
            WHEN result.State = N'Claim' AND COALESCE(o.ReviewStatus, N'') <> N'Rejected' THEN 1
            ELSE 0 END)
    FROM dbo.BookingOffer o
    OUTER APPLY (
        SELECT MIN(q.CompletedUtc) AS FirstScanUtc
        FROM dbo.BookingQrCompletion q
        WHERE q.SeatBookingDetailsID = o.SeatBookingDetailsID
    ) scan
    CROSS APPLY (
        -- Midnight in India at the end of the day after the appointment.
        SELECT DATEADD(minute, -330, CAST(DATEADD(day, 2, o.BookingDate) AS datetime2(0))) AS DeadlineUtc
    ) deadline
    OUTER APPLY (
        SELECT TOP (1) b.BookingStatusMasterID
        FROM dbo.SeatBookingDetails b
        WHERE b.SeatBookingDetailsID = o.SeatBookingDetailsID
    ) booking
    CROSS APPLY (
        SELECT State = CASE
            WHEN scan.FirstScanUtc < deadline.DeadlineUtc THEN N'Claim'
            WHEN scan.FirstScanUtc IS NOT NULL THEN N'Expired'
            WHEN COALESCE(booking.BookingStatusMasterID, 0) <> 2 THEN N'Cancelled'
            WHEN @NowUtc >= deadline.DeadlineUtc THEN N'Expired'
            ELSE N'Held' END
    ) result
);
GO

-- What this customer gets. Reason is 'Available' or why the offer is not available. With no salon
-- it answers "can this customer still get the offer?"; with a salon, date and amount it also
-- returns the discount for that booking. The server decides; the phone never does.
IF OBJECT_ID(N'dbo.QuoteBookingOffer', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.QuoteBookingOffer AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.QuoteBookingOffer
    @CustomerID uniqueidentifier,
    @CustomerKey varbinary(32),
    @SalonID uniqueidentifier = NULL,
    @BookingDate date = NULL,
    @Amount decimal(10,2) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    -- Confirmations are already one at a time (the reservation lock). Reading this way avoids
    -- holding range locks on whole customer and booking tables when called during a confirmation.
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

    DECLARE @Now datetime2(0) = SYSUTCDATETIME();
    DECLARE @Today date = CONVERT(date, DATEADD(minute, 330, @Now));
    DECLARE @Type nvarchar(10), @Value decimal(10,2), @Maximum decimal(10,2), @MinimumBill decimal(10,2),
        @EndDate date, @Budget decimal(12,2), @SalonLimit int, @Enabled bit, @Launched datetime2(0);
    SELECT @Enabled = IsEnabled, @Type = DiscountType, @Value = DiscountValue, @Maximum = MaximumDiscount,
        @MinimumBill = MinimumBill, @EndDate = EndDate, @Budget = TotalBudget, @SalonLimit = SalonMonthlyLimit,
        @Launched = LaunchedUtc
    FROM dbo.BookingOfferSettings WHERE SettingsID = 1;

    DECLARE @Reason nvarchar(30) = N'Available';
    DECLARE @Discount decimal(10,2) = 0;

    IF COALESCE(@Enabled, 0) = 0 OR @Launched IS NULL OR @EndDate IS NULL OR @Today > @EndDate
        SET @Reason = N'Off';

    -- Every account that has used this mobile number, including older copies.
    DECLARE @Customers TABLE (CustomerID uniqueidentifier PRIMARY KEY);
    IF @Reason = N'Available'
    BEGIN
        DECLARE @MobileNo nvarchar(50) =
            (SELECT TOP (1) NULLIF(MobileNo, N'') FROM dbo.CustomerRegistration
             WHERE CustomerID = @CustomerID AND COALESCE(IsDeleted, 0) = 0);
        INSERT @Customers(CustomerID)
        SELECT @CustomerID
        UNION
        SELECT CustomerID FROM dbo.CustomerRegistration WHERE MobileNo = @MobileNo AND CustomerID IS NOT NULL;

        IF @MobileNo IS NULL OR @CustomerKey IS NULL
            SET @Reason = N'NotNewCustomer';
        ELSE IF EXISTS (SELECT 1 FROM dbo.BookingOfferEarlierCustomer e JOIN @Customers c ON c.CustomerID = e.CustomerID)
            OR EXISTS (SELECT 1 FROM dbo.SeatBookingDetails b JOIN @Customers c ON c.CustomerID = b.CustomerID
                       WHERE b.CompletionStatus = N'Completed'
                          OR EXISTS (SELECT 1 FROM dbo.BookingQrCompletion q WHERE q.SeatBookingDetailsID = b.SeatBookingDetailsID))
            SET @Reason = N'NotNewCustomer';
    END;

    IF @Reason = N'Available'
    BEGIN
        DECLARE @Mine TABLE (State nvarchar(10) NOT NULL);
        INSERT @Mine(State)
        SELECT s.State FROM dbo.BookingOfferState(@Now) s
        WHERE s.CustomerKey = @CustomerKey OR s.CustomerID IN (SELECT CustomerID FROM @Customers);

        IF EXISTS (SELECT 1 FROM @Mine WHERE State = N'Claim') SET @Reason = N'NotNewCustomer';
        ELSE IF EXISTS (SELECT 1 FROM @Mine WHERE State = N'Held') SET @Reason = N'AlreadyHeld';
    END;

    DECLARE @Remaining decimal(12,2) = 0;
    IF @Reason = N'Available'
    BEGIN
        SET @Remaining = @Budget - COALESCE(
            (SELECT SUM(DiscountAmount) FROM dbo.BookingOfferState(@Now) WHERE CountsToLimits = 1), 0);
        IF @Remaining <= 0 OR (@Type = N'Fixed' AND @Remaining < @Value)
            SET @Reason = N'BudgetUsed';
    END;

    -- With no salon chosen (the Home banner), do not announce an offer that no salon can give.
    IF @Reason = N'Available' AND @SalonID IS NULL
        AND (COALESCE(@SalonLimit, 0) <= 0
             OR NOT EXISTS (SELECT 1 FROM dbo.BookingOfferSalon o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
                            WHERE s.IsActive = 1))
        SET @Reason = N'SalonNotParticipating';

    IF @Reason = N'Available' AND @SalonID IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.BookingOfferSalon o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
                       WHERE o.SalonMasterID = @SalonID AND s.IsActive = 1)
            SET @Reason = N'SalonNotParticipating';
        ELSE
        BEGIN
            -- The monthly limit goes by the month of the appointment.
            DECLARE @Day date = COALESCE(@BookingDate, @Today);
            DECLARE @MonthStart date = DATEADD(day, 1 - DAY(@Day), @Day);
            IF (SELECT COUNT(*) FROM dbo.BookingOfferState(@Now)
                WHERE CountsToLimits = 1 AND SalonMasterID = @SalonID
                  AND BookingDate >= @MonthStart AND BookingDate < DATEADD(month, 1, @MonthStart)) >= @SalonLimit
                SET @Reason = N'SalonLimitReached';
        END;
    END;

    IF @Reason = N'Available' AND @Amount IS NOT NULL
    BEGIN
        IF @Amount <= 0 OR @Amount < @MinimumBill
            SET @Reason = N'BelowMinimum';
        ELSE
        BEGIN
            -- A percentage is rounded down to whole rupees and capped.
            SET @Discount = CASE WHEN @Type = N'Percent' THEN FLOOR(@Amount * @Value / 100) ELSE @Value END;
            IF @Type = N'Percent' AND @Discount > @Maximum SET @Discount = @Maximum;
            IF @Discount > @Amount SET @Discount = @Amount;
            IF @Discount <= 0 SELECT @Discount = 0, @Reason = N'BelowMinimum';
            ELSE IF @Discount > @Remaining SELECT @Discount = 0, @Reason = N'BudgetUsed';
        END;
    END;

    SELECT Reason = @Reason, Discount = CAST(@Discount AS decimal(10,2)), DiscountType = @Type,
        DiscountValue = @Value, MaximumDiscount = @Maximum, MinimumBill = @MinimumBill, EndDate = @EndDate;
END;
GO

IF OBJECT_ID(N'dbo.GetCustomerBookingOffers', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.GetCustomerBookingOffers AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.GetCustomerBookingOffers
    @CustomerID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT SeatBookingDetailsID, OriginalAmount, DiscountAmount, State,
        AmountToPay = CASE WHEN State IN (N'Held', N'Claim') THEN OriginalAmount - DiscountAmount ELSE OriginalAmount END
    FROM dbo.BookingOfferState(SYSUTCDATETIME())
    WHERE CustomerID = @CustomerID
    ORDER BY CreatedUtc DESC;
END;
GO

-- The salon sees which of its bookings carry the offer and what to collect.
IF OBJECT_ID(N'dbo.GetSalonBookingOffers', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.GetSalonBookingOffers AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.GetSalonBookingOffers
    @OwnerID uniqueidentifier,
    @SalonID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (
        SELECT 1 FROM dbo.SalonOwner o
        JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID
          AND o.IsActive = 1 AND s.IsActive = 1
    )
        THROW 50013, 'Please sign in again.', 1;

    SELECT TOP (500) SeatBookingDetailsID, BookingDate, OriginalAmount, DiscountAmount, State,
        AmountToCollect = CASE WHEN State IN (N'Held', N'Claim') THEN OriginalAmount - DiscountAmount ELSE OriginalAmount END,
        ReviewStatus, PaymentReference, DeadlineUtc
    FROM dbo.BookingOfferState(SYSUTCDATETIME())
    WHERE SalonMasterID = @SalonID
    ORDER BY BookingDate DESC, CreatedUtc DESC;
END;
GO

IF OBJECT_ID(N'dbo.GetBookingOfferSettings', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.GetBookingOfferSettings AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.GetBookingOfferSettings
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.IsEnabled, s.DiscountType, s.DiscountValue, s.MaximumDiscount, s.MinimumBill, s.EndDate,
        s.TotalBudget, s.SalonMonthlyLimit, s.LaunchedUtc, s.Version,
        totals.WaitingCount, totals.WaitingAmount, totals.ToCheckCount, totals.ToCheckAmount,
        totals.ToPayCount, totals.ToPayAmount, totals.PaidCount, totals.PaidAmount,
        BudgetUsed = totals.WaitingAmount + totals.ToCheckAmount + totals.ToPayAmount + totals.PaidAmount
    FROM dbo.BookingOfferSettings s
    CROSS APPLY (
        SELECT
            WaitingCount = COUNT(CASE WHEN State = N'Held' THEN 1 END),
            WaitingAmount = COALESCE(SUM(CASE WHEN State = N'Held' THEN DiscountAmount END), 0),
            ToCheckCount = COUNT(CASE WHEN State = N'Claim' AND ReviewStatus IS NULL THEN 1 END),
            ToCheckAmount = COALESCE(SUM(CASE WHEN State = N'Claim' AND ReviewStatus IS NULL THEN DiscountAmount END), 0),
            ToPayCount = COUNT(CASE WHEN State = N'Claim' AND ReviewStatus = N'Approved' THEN 1 END),
            ToPayAmount = COALESCE(SUM(CASE WHEN State = N'Claim' AND ReviewStatus = N'Approved' THEN DiscountAmount END), 0),
            PaidCount = COUNT(CASE WHEN State = N'Claim' AND ReviewStatus = N'Paid' THEN 1 END),
            PaidAmount = COALESCE(SUM(CASE WHEN State = N'Claim' AND ReviewStatus = N'Paid' THEN DiscountAmount END), 0)
        FROM dbo.BookingOfferState(SYSUTCDATETIME())
    ) totals
    WHERE s.SettingsID = 1;
END;
GO

IF OBJECT_ID(N'dbo.SaveBookingOfferSettings', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.SaveBookingOfferSettings AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.SaveBookingOfferSettings
    @IsEnabled bit,
    @DiscountType nvarchar(10),
    @DiscountValue decimal(10,2),
    @MaximumDiscount decimal(10,2) = NULL,
    @MinimumBill decimal(10,2),
    @EndDate date = NULL,
    @TotalBudget decimal(12,2),
    @SalonMonthlyLimit int,
    @ExpectedVersion int,
    @Actor nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @IsEnabled IS NULL OR @DiscountType IS NULL OR @DiscountType NOT IN (N'Fixed', N'Percent')
        OR @DiscountValue IS NULL OR @DiscountValue < 0 OR @MinimumBill IS NULL OR @MinimumBill < 0
        OR @TotalBudget IS NULL OR @TotalBudget < 0 OR @SalonMonthlyLimit IS NULL OR @SalonMonthlyLimit < 0
        OR @MaximumDiscount < 0 OR (@DiscountType = N'Percent' AND @DiscountValue > 100)
        THROW 50062, 'Enter valid offer settings.', 1;
    IF @IsEnabled = 1
    BEGIN
        IF @DiscountValue <= 0 OR @TotalBudget <= 0 OR @SalonMonthlyLimit <= 0
            THROW 50062, 'To switch the offer on, enter a discount, a total budget and a monthly limit for each salon.', 1;
        IF @DiscountType = N'Percent' AND COALESCE(@MaximumDiscount, 0) <= 0
            THROW 50062, 'A percentage offer needs a maximum discount.', 1;
        IF @EndDate IS NULL OR @EndDate < CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME()))
            THROW 50062, 'Choose an end date from today onwards.', 1;
    END;

    BEGIN TRANSACTION;

    -- No booking can be confirmed while the earlier customers are being listed.
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'Salonexa.TimeReservations.v1',
        @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
    IF @LockResult < 0
        THROW 50066, 'Bookings are busy. Please try again.', 1;

    DECLARE @Version int, @Launched datetime2(0);
    SELECT @Version = Version, @Launched = LaunchedUtc
    FROM dbo.BookingOfferSettings WITH (UPDLOCK, HOLDLOCK) WHERE SettingsID = 1;
    IF @ExpectedVersion IS NULL OR @Version <> @ExpectedVersion
        THROW 50061, 'The offer settings changed. Reload and try again.', 1;

    IF @IsEnabled = 1 AND @Launched IS NULL
    BEGIN
        INSERT dbo.BookingOfferEarlierCustomer(CustomerID)
        SELECT CustomerID FROM dbo.SeatBookingDetails
        WHERE CustomerID IS NOT NULL AND (BookingStatusMasterID = 2 OR CompletionStatus = N'Completed')
        UNION
        SELECT b.CustomerID FROM dbo.SeatBookingDetails b
        WHERE b.CustomerID IS NOT NULL AND EXISTS
            (SELECT 1 FROM dbo.HairCut_Item_Bill_Master m WHERE m.SeatBookingDetailsID = b.SeatBookingDetailsID);
        SET @Launched = SYSUTCDATETIME();
    END;

    UPDATE dbo.BookingOfferSettings
    SET IsEnabled = @IsEnabled, DiscountType = @DiscountType, DiscountValue = @DiscountValue,
        MaximumDiscount = @MaximumDiscount, MinimumBill = @MinimumBill, EndDate = @EndDate,
        TotalBudget = @TotalBudget, SalonMonthlyLimit = @SalonMonthlyLimit, LaunchedUtc = @Launched,
        Version = Version + 1, UpdatedUtc = SYSUTCDATETIME(), UpdatedBy = @Actor
    WHERE SettingsID = 1;

    COMMIT TRANSACTION;
END;
GO

IF OBJECT_ID(N'dbo.GetBookingOfferSalons', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.GetBookingOfferSalons AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.GetBookingOfferSalons
    @Search nvarchar(100) = N'',
    @Page int = 1,
    @PageSize int = 20
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Pattern nvarchar(210) =
        N'%' + REPLACE(REPLACE(REPLACE(COALESCE(@Search, N''), N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]') + N'%';
    DECLARE @Today date = CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME()));
    DECLARE @MonthStart date = DATEADD(day, 1 - DAY(@Today), @Today);

    SELECT COUNT(*) FROM dbo.SalonMaster WHERE SalonName LIKE @Pattern;

    SELECT s.SalonMasterID, s.SalonName, s.IsActive,
        IsParticipating = CONVERT(bit, CASE WHEN o.SalonMasterID IS NULL THEN 0 ELSE 1 END),
        totals.UsedThisMonth, totals.ToCheckAmount, totals.ToPayAmount, totals.PaidAmount
    FROM dbo.SalonMaster s
    LEFT JOIN dbo.BookingOfferSalon o ON o.SalonMasterID = s.SalonMasterID
    OUTER APPLY (
        -- What this salon has used this month, and what is waiting, owed and repaid.
        SELECT
            UsedThisMonth = COUNT(CASE WHEN u.CountsToLimits = 1
                AND u.BookingDate >= @MonthStart AND u.BookingDate < DATEADD(month, 1, @MonthStart) THEN 1 END),
            ToCheckAmount = COALESCE(SUM(CASE WHEN u.State = N'Claim' AND u.ReviewStatus IS NULL THEN u.DiscountAmount END), 0),
            ToPayAmount = COALESCE(SUM(CASE WHEN u.State = N'Claim' AND u.ReviewStatus = N'Approved' THEN u.DiscountAmount END), 0),
            PaidAmount = COALESCE(SUM(CASE WHEN u.State = N'Claim' AND u.ReviewStatus = N'Paid' THEN u.DiscountAmount END), 0)
        FROM dbo.BookingOfferState(SYSUTCDATETIME()) u
        WHERE u.SalonMasterID = s.SalonMasterID
    ) totals
    WHERE s.SalonName LIKE @Pattern
    -- Salons you owe come first, even after they leave the offer.
    ORDER BY CASE WHEN totals.ToPayAmount > 0 THEN 0 ELSE 1 END,
        CASE WHEN o.SalonMasterID IS NULL THEN 1 ELSE 0 END, s.SalonName, s.SalonMasterID
    OFFSET ((CAST(@Page AS bigint) - 1) * @PageSize) ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO

IF OBJECT_ID(N'dbo.SetBookingOfferSalon', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.SetBookingOfferSalon AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.SetBookingOfferSalon
    @SalonID uniqueidentifier,
    @Participating bit,
    @Actor nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.SalonMaster WHERE SalonMasterID = @SalonID)
        THROW 50063, 'This salon no longer exists.', 1;
    IF @Participating = 1
        INSERT dbo.BookingOfferSalon(SalonMasterID, AddedBy)
        SELECT @SalonID, @Actor
        WHERE NOT EXISTS (SELECT 1 FROM dbo.BookingOfferSalon WITH (UPDLOCK, HOLDLOCK) WHERE SalonMasterID = @SalonID);
    ELSE
        -- Bookings already confirmed with the offer keep their discount.
        DELETE dbo.BookingOfferSalon WHERE SalonMasterID = @SalonID;
END;
GO

-- Claims to check before paying a salon. The warning columns help; they do not prove anything.
IF OBJECT_ID(N'dbo.GetBookingOfferClaims', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.GetBookingOfferClaims AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.GetBookingOfferClaims
    @Status nvarchar(10) = N'ToCheck',
    @Page int = 1,
    @PageSize int = 20
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Now datetime2(0) = SYSUTCDATETIME();

    SELECT * INTO #Claims FROM dbo.BookingOfferState(@Now) WHERE State = N'Claim';

    SELECT COUNT(*) FROM #Claims WHERE COALESCE(ReviewStatus, N'ToCheck') = @Status;

    SELECT c.SeatBookingDetailsID, c.SalonMasterID, SalonName = COALESCE(s.SalonName, N''),
        CustomerName = COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(r.FirstName, N' ', r.LastName))), N''), N''),
        MobileNo = COALESCE(r.MobileNo, N''), c.BookingDate, c.OriginalAmount, c.DiscountAmount,
        BookedUtc = c.CreatedUtc, ScannedUtc = c.FirstScanUtc,
        Status = COALESCE(c.ReviewStatus, N'ToCheck'), c.PaymentReference, c.ReviewNote,
        ScannedSoonAfterBooking = CONVERT(bit, CASE WHEN DATEDIFF(second, c.CreatedUtc, c.FirstScanUtc) < 1800 THEN 1 ELSE 0 END),
        ManyOffersSameDay = CONVERT(bit, CASE WHEN
            (SELECT COUNT(*) FROM dbo.BookingOffer d
             WHERE d.SalonMasterID = c.SalonMasterID AND d.BookingDate = c.BookingDate) >= 3 THEN 1 ELSE 0 END),
        NoCustomerName = CONVERT(bit, CASE WHEN NULLIF(LTRIM(RTRIM(CONCAT(r.FirstName, N' ', r.LastName))), N'') IS NULL THEN 1 ELSE 0 END),
        UsesSalonPhoneNumber = CONVERT(bit, CASE WHEN NULLIF(r.MobileNo, N'') IS NOT NULL
            AND RIGHT(REPLACE(REPLACE(COALESCE(s.ContactNumber, N''), N' ', N''), N'-', N''), 10) = RIGHT(r.MobileNo, 10)
            THEN 1 ELSE 0 END),
        AccountDeleted = CONVERT(bit, CASE WHEN COALESCE(r.IsDeleted, 0) = 1 THEN 1 ELSE 0 END)
    FROM #Claims c
    LEFT JOIN dbo.SalonMaster s ON s.SalonMasterID = c.SalonMasterID
    OUTER APPLY (
        SELECT TOP (1) FirstName, LastName, MobileNo, IsDeleted FROM dbo.CustomerRegistration
        WHERE CustomerID = c.CustomerID ORDER BY COALESCE(IsDeleted, 0)
    ) r
    WHERE COALESCE(c.ReviewStatus, N'ToCheck') = @Status
    ORDER BY c.FirstScanUtc, c.SeatBookingDetailsID
    OFFSET ((CAST(@Page AS bigint) - 1) * @PageSize) ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO

-- To check -> Approved or Rejected; Approved -> Paid or Rejected. Rejected and Paid are final, so a
-- rejected claim can never push repayments back over the budget it released. No money is sent here.
IF OBJECT_ID(N'dbo.ReviewBookingOfferClaim', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.ReviewBookingOfferClaim AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.ReviewBookingOfferClaim
    @SeatBookingDetailsID uniqueidentifier,
    @Action nvarchar(10),
    @PaymentReference nvarchar(100) = NULL,
    @Note nvarchar(300) = NULL,
    @Actor nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @PaymentReference = NULLIF(LTRIM(RTRIM(@PaymentReference)), N'');
    IF @Action IS NULL OR @Action NOT IN (N'Approve', N'Reject', N'Paid')
        THROW 50062, 'Choose approve, reject or paid.', 1;
    IF @Action = N'Paid' AND @PaymentReference IS NULL
        THROW 50062, 'Enter the payment reference.', 1;

    BEGIN TRANSACTION;
    -- An unreviewed claim is named, because a comparison with an empty value would let any change through.
    DECLARE @Current nvarchar(10), @Found bit = 0;
    SELECT @Current = COALESCE(ReviewStatus, N'ToCheck'), @Found = 1
    FROM dbo.BookingOffer WITH (UPDLOCK, HOLDLOCK) WHERE SeatBookingDetailsID = @SeatBookingDetailsID;

    IF @Found = 0 OR NOT EXISTS (SELECT 1 FROM dbo.BookingOfferState(SYSUTCDATETIME())
        WHERE SeatBookingDetailsID = @SeatBookingDetailsID AND State = N'Claim')
        THROW 50064, 'This booking has no claim to review.', 1;

    IF NOT (
        (@Current = N'ToCheck' AND @Action IN (N'Approve', N'Reject'))
        OR (@Current = N'Approved' AND @Action IN (N'Paid', N'Reject')))
        THROW 50065, 'This claim cannot be changed that way. Reload and check its status.', 1;

    UPDATE dbo.BookingOffer
    SET ReviewStatus = CASE @Action WHEN N'Approve' THEN N'Approved' WHEN N'Reject' THEN N'Rejected' ELSE N'Paid' END,
        ReviewedUtc = SYSUTCDATETIME(), ReviewedBy = @Actor,
        PaymentReference = CASE WHEN @Action = N'Paid' THEN @PaymentReference ELSE PaymentReference END,
        ReviewNote = COALESCE(NULLIF(LTRIM(RTRIM(@Note)), N''), ReviewNote)
    WHERE SeatBookingDetailsID = @SeatBookingDetailsID;
    COMMIT TRANSACTION;
END;
GO
