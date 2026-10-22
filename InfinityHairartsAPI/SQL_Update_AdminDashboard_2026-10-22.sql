/*
Admin dashboard and QR completion SQL changes - 22 October 2026.

HOW TO RUN
1. Open this file in a new SQL Server Management Studio query window.
2. Select the API database: InfinityHairArtsAdmin.
3. Execute this entire file. It runs in one transaction and supports reruns.
4. Deploy the matching UpdatedInfinityHairartsAPI build and admin app.

INCLUDED CHANGES
- Stores Pending/Completed status and completion timestamps on SeatBookingDetails.
- Creates the BookingQrCompletion appointment audit table when missing.
- Updates completeBookingFromQr with salon/owner validation and India-time checks.
- Completes a booking only after every selected appointment QR is completed.
- Updates GetSalonOwnerDashboard with all six overview card values:
  Bookings, Completed, Pending and Billed amount for the selected date;
  distinct Salon customers and Active services across the signed-in salon.
- Returns the salon profile and service collection required by the admin screen.

PREREQUISITES AND DATA
Requires the existing salon-owner dashboard, booking and billing base tables.
No earlier QR-completion SQL update is required; its changes are included here.
Preserves existing bookings and QR completions. Matching historical QR completions
are used to backfill booking completion status. No bookings are reassigned to salons.
BookingStatusMasterID remains the reservation/payment-confirmation status.
Source: Database/BookingQrWorkflow.sql completion batches and SalonOwnerDashboard.sql.
*/
IF @@TRANCOUNT <> 0
BEGIN
    RAISERROR('Run this file in a new query window without an open transaction.', 16, 1);
    RETURN;
END;
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
IF DB_NAME() <> N'InfinityHairArtsAdmin'
    THROW 51010, 'Select InfinityHairArtsAdmin before running this file.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.SeatBookingDetails', N'U') IS NULL
       OR OBJECT_ID(N'dbo.CustomerTimeSelection', N'U') IS NULL
       OR OBJECT_ID(N'dbo.TimeAllocation', N'U') IS NULL
       OR OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL
       OR OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL
       OR OBJECT_ID(N'dbo.SalonService', N'U') IS NULL
       OR COL_LENGTH(N'dbo.SeatBookingDetails', N'SalonMasterID') IS NULL
        THROW 51012, 'Install the existing salon/owner booking schema before this update.', 1;
    EXEC sys.sp_executesql N'IF OBJECT_ID(N''dbo.BookingQrCompletion'', N''U'') IS NULL
BEGIN
    CREATE TABLE dbo.BookingQrCompletion
    (
        SeatBookingDetailsID UNIQUEIDENTIFIER NOT NULL,
        TimeAllocationID UNIQUEIDENTIFIER NOT NULL,
        SalonOwnerID UNIQUEIDENTIFIER NOT NULL,
        CompletedUtc DATETIME2(0) NOT NULL
            CONSTRAINT DF_BookingQrCompletion_CompletedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_BookingQrCompletion
            PRIMARY KEY (SeatBookingDetailsID, TimeAllocationID)
    );
END;';

    EXEC sys.sp_executesql N'-- BookingStatusMasterID continues to represent reservation/payment confirmation.
-- Visit completion is separate so completed visits still occupy their booked slots.
IF COL_LENGTH(N''dbo.SeatBookingDetails'', N''CompletionStatus'') IS NULL
    ALTER TABLE dbo.SeatBookingDetails ADD CompletionStatus NVARCHAR(16) NOT NULL
        CONSTRAINT DF_SeatBookingDetails_CompletionStatus DEFAULT N''Pending'' WITH VALUES
        CONSTRAINT CK_SeatBookingDetails_CompletionStatus CHECK (CompletionStatus IN (N''Pending'', N''Completed''));
IF COL_LENGTH(N''dbo.SeatBookingDetails'', N''CompletedUtc'') IS NULL
    ALTER TABLE dbo.SeatBookingDetails ADD CompletedUtc DATETIME2(0) NULL;';

    EXEC sys.sp_executesql N'-- Preserve completions already recorded by the previous QR workflow. A booking
-- with several selected times completes only after every appointment QR is read.
UPDATE SB
SET CompletionStatus = N''Completed'', CompletedUtc = Completion.CompletedUtc
FROM dbo.SeatBookingDetails SB
CROSS APPLY
(
    SELECT MAX(Q.CompletedUtc) AS CompletedUtc
    FROM dbo.BookingQrCompletion Q
    INNER JOIN dbo.CustomerTimeSelection CTS
        ON CTS.SeatBookingDetailsID = Q.SeatBookingDetailsID
       AND CTS.TimeAllocationID = Q.TimeAllocationID
    WHERE Q.SeatBookingDetailsID = SB.SeatBookingDetailsID
) Completion
WHERE Completion.CompletedUtc IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.CustomerTimeSelection CTS
      WHERE CTS.SeatBookingDetailsID = SB.SeatBookingDetailsID
        AND NOT EXISTS
        (
            SELECT 1 FROM dbo.BookingQrCompletion Q
            WHERE Q.SeatBookingDetailsID = CTS.SeatBookingDetailsID
              AND Q.TimeAllocationID = CTS.TimeAllocationID
        )
  )
  AND (SB.CompletionStatus <> N''Completed'' OR SB.CompletedUtc IS NULL);';

    EXEC sys.sp_executesql N'-- The legacy source tables do not consistently declare their ID columns as
-- primary/unique keys, so SQL Server cannot create foreign keys to them. The
-- completion procedure validates all three IDs before inserting instead.
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N''dbo.BookingQrCompletion'')
      AND name = N''IX_BookingQrCompletion_CompletedUtc''
)
BEGIN
    CREATE INDEX IX_BookingQrCompletion_CompletedUtc
        ON dbo.BookingQrCompletion (CompletedUtc);
END;';

    EXEC sys.sp_executesql N'IF OBJECT_ID(N''dbo.completeBookingFromQr'', N''P'') IS NULL
    EXEC(N''CREATE PROCEDURE dbo.completeBookingFromQr AS BEGIN SET NOCOUNT ON; END'');';

    EXEC sys.sp_executesql N'ALTER PROCEDURE dbo.completeBookingFromQr
    @SeatBookingDetailsID UNIQUEIDENTIFIER,
    @TimeAllocationID UNIQUEIDENTIFIER,
    @SalonOwnerID UNIQUEIDENTIFIER,
    @SalonMasterID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.SalonOwner
        WHERE SalonOwnerID = @SalonOwnerID
          AND SalonMasterID = @SalonMasterID
          AND IsActive = 1
          AND EXISTS (SELECT 1 FROM dbo.SalonMaster WHERE SalonMasterID = @SalonMasterID AND IsActive = 1)
    )
    BEGIN
        THROW 50013, ''This admin account is not authorized for the selected salon.'', 1;
    END;

    DECLARE @BookingDate DATE;
    DECLARE @BookingTime TIME;
    DECLARE @CustomerName NVARCHAR(301);

    SELECT
        @BookingDate = CONVERT(DATE, SB.BookingDate),
        @BookingTime = TA.FullTiming,
        @CustomerName = COALESCE(
            NULLIF(LTRIM(RTRIM(CONCAT(CR.FirstName, N'' '', CR.LastName))), N''''),
            N''Guest customer'')
    -- Serialize completions for the entire booking, including different QR slots.
    FROM dbo.SeatBookingDetails SB WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.CustomerTimeSelection CTS
        ON CTS.SeatBookingDetailsID = SB.SeatBookingDetailsID
       AND CTS.TimeAllocationID = @TimeAllocationID
    INNER JOIN dbo.TimeAllocation TA
        ON TA.TimeAllocationID = CTS.TimeAllocationID
    LEFT JOIN dbo.CustomerRegistration CR
        ON CR.CustomerID = SB.CustomerID
    WHERE SB.SeatBookingDetailsID = @SeatBookingDetailsID
      AND SB.BookingStatusMasterID = 2
      AND SB.SalonMasterID = @SalonMasterID;

    IF @BookingDate IS NULL OR @BookingTime IS NULL
    BEGIN
        THROW 50010, ''This QR code does not match a confirmed booking for your salon.'', 1;
    END;

    DECLARE @AppointmentStart DATETIME2(0) = DATEADD(
        SECOND,
        DATEDIFF(SECOND, CAST(''00:00:00'' AS TIME), @BookingTime),
        CAST(@BookingDate AS DATETIME2(0)));

    -- Booking dates and slot times are in India time, regardless of server zone.
    IF @AppointmentStart > DATEADD(MINUTE, 330, SYSUTCDATETIME())
    BEGIN
        THROW 50011, ''This appointment is still Upcoming. It can be completed at or after the booked time.'', 1;
    END;

    DECLARE @WasAlreadyCompleted BIT = 0;
    DECLARE @CompletedUtc DATETIME2(0);

    SELECT @CompletedUtc = CompletedUtc
    FROM dbo.BookingQrCompletion WITH (UPDLOCK, HOLDLOCK)
    WHERE SeatBookingDetailsID = @SeatBookingDetailsID
      AND TimeAllocationID = @TimeAllocationID;

    IF @CompletedUtc IS NULL
    BEGIN
        SET @CompletedUtc = SYSUTCDATETIME();

        INSERT INTO dbo.BookingQrCompletion
        (
            SeatBookingDetailsID,
            TimeAllocationID,
            SalonOwnerID,
            CompletedUtc
        )
        VALUES
        (
            @SeatBookingDetailsID,
            @TimeAllocationID,
            @SalonOwnerID,
            @CompletedUtc
        );
    END
    ELSE
    BEGIN
        SET @WasAlreadyCompleted = 1;
    END;

    DECLARE @BookingStatus NVARCHAR(16) = N''Pending'';
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.CustomerTimeSelection CTS
        WHERE CTS.SeatBookingDetailsID = @SeatBookingDetailsID
          AND NOT EXISTS
          (
              SELECT 1 FROM dbo.BookingQrCompletion Q
              WHERE Q.SeatBookingDetailsID = CTS.SeatBookingDetailsID
                AND Q.TimeAllocationID = CTS.TimeAllocationID
          )
    )
        SET @BookingStatus = N''Completed'';

    UPDATE dbo.SeatBookingDetails
    SET CompletionStatus = @BookingStatus,
        CompletedUtc = CASE WHEN @BookingStatus = N''Completed'' THEN
            (SELECT MAX(CompletedUtc) FROM dbo.BookingQrCompletion WHERE SeatBookingDetailsID = @SeatBookingDetailsID)
            ELSE NULL END
    WHERE SeatBookingDetailsID = @SeatBookingDetailsID
      AND SalonMasterID = @SalonMasterID;

    COMMIT TRANSACTION;

    SELECT
        SeatBookingDetailsID = @SeatBookingDetailsID,
        TimeAllocationID = @TimeAllocationID,
        CustomerName = @CustomerName,
        BookingDate = @BookingDate,
        BookingTime = @BookingTime,
        CompletedUtc = @CompletedUtc,
        WasAlreadyCompleted = @WasAlreadyCompleted,
        BookingStatus = @BookingStatus;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;';

    EXEC sys.sp_executesql N'SET XACT_ABORT ON;';

    EXEC sys.sp_executesql N'IF COL_LENGTH(N''dbo.SeatBookingDetails'', N''CompletionStatus'') IS NULL
    THROW 50003, ''Run BookingQrWorkflow.sql before SalonOwnerDashboard.sql.'', 1;';

    EXEC sys.sp_executesql N'IF OBJECT_ID(N''dbo.GetSalonOwnerDashboard'', N''P'') IS NULL
    EXEC(N''CREATE PROCEDURE dbo.GetSalonOwnerDashboard AS BEGIN SET NOCOUNT ON; END'');';

    EXEC sys.sp_executesql N'ALTER PROCEDURE dbo.GetSalonOwnerDashboard @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @Date date
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1)
        THROW 50013, ''Please sign in again.'', 1;
    -- CompletionStatus is saved atomically with the appointment QR audit row.
    SELECT TodayBookings = COUNT(CASE WHEN sb.BookingDate = @Date THEN 1 END),
        TodaySeats = COALESCE(SUM(CASE WHEN sb.BookingDate = @Date THEN sb.SeatCount ELSE 0 END), 0),
        PendingBookings = COUNT(CASE WHEN sb.BookingDate = @Date AND sb.CompletionStatus = N''Pending'' THEN 1 END),
        CompletedBookings = COUNT(CASE WHEN sb.BookingDate = @Date AND sb.CompletionStatus = N''Completed'' THEN 1 END),
        TotalBookings = COUNT(*),
        TotalPendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N''Pending'' THEN 1 END),
        TotalCompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N''Completed'' THEN 1 END),
        TodayRevenue = (SELECT COALESCE(SUM(b.BillIDTotalAmount), 0) FROM dbo.HairCut_Item_Bill_Master b
            JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
            WHERE sb.SalonMasterID = @SalonID AND b.CreatedDate >= @Date AND b.CreatedDate < DATEADD(day, 1, @Date) AND COALESCE(b.IsDeleted, 0) = 0),
        TotalCustomers = (SELECT COUNT(DISTINCT CustomerID) FROM dbo.SeatBookingDetails WHERE SalonMasterID = @SalonID AND BookingStatusMasterID = 2),
        ActiveServices = (SELECT COUNT(*) FROM dbo.SalonService WHERE SalonMasterID = @SalonID AND IsActive = 1),
        UpcomingBookings = COUNT(CASE WHEN sb.BookingDate >= @Date AND sb.CompletionStatus = N''Pending'' THEN 1 END)
    FROM dbo.SeatBookingDetails sb
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingStatusMasterID = 2;
    SELECT TOP(30) sb.SeatBookingDetailsID,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(cr.FirstName, '' '', cr.LastName))), ''''), ''Guest customer'') AS CustomerName,
        COALESCE(cr.MobileNo, '''') AS MobileNo, sb.BookingDate,
        MIN(ta.FullTiming) AS StartTime, COALESCE(sb.SeatCount, 0) AS SeatCount,
        sb.CompletionStatus AS BookingStatus
    FROM dbo.SeatBookingDetails sb
    LEFT JOIN dbo.CustomerRegistration cr ON cr.CustomerID = sb.CustomerID
    LEFT JOIN dbo.CustomerTimeSelection ct ON ct.SeatBookingDetailsID = sb.SeatBookingDetailsID
    LEFT JOIN dbo.TimeAllocation ta ON ta.TimeAllocationID = ct.TimeAllocationID
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingDate = @Date AND sb.BookingStatusMasterID = 2
    GROUP BY sb.SeatBookingDetailsID, cr.FirstName, cr.LastName, cr.MobileNo, sb.BookingDate, sb.SeatCount, sb.CompletionStatus
    ORDER BY MIN(ta.FullTiming), sb.SeatBookingDetailsID;
    ;WITH Dates AS (SELECT DATEADD(day, v.n - 6, @Date) AS BookingDate FROM (VALUES(0),(1),(2),(3),(4),(5),(6)) v(n))
    SELECT d.BookingDate, COUNT(sb.SeatBookingDetailsID) AS BookingCount FROM Dates d
    LEFT JOIN dbo.SeatBookingDetails sb ON sb.SalonMasterID = @SalonID AND sb.BookingDate = d.BookingDate AND sb.BookingStatusMasterID = 2
    GROUP BY d.BookingDate ORDER BY d.BookingDate;
    SELECT TOP(4) h.HairCut_Item_ID AS HairCutItemID, MAX(h.HairCut_Item_Name) AS ServiceName,
        SUM(cb.HairCut_Item_Quantity) AS BookingCount, SUM(cb.HairCut_Item_Total) AS Revenue
    FROM dbo.HairCut_Item_Customer_Bill cb
    JOIN dbo.HairCut_Item_List h ON h.HairCut_Item_ID = cb.HairCut_Item_ID
    JOIN dbo.HairCut_Item_Bill_Master b ON b.UniqueBillID = cb.UniqueBillID
    JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
    WHERE sb.SalonMasterID = @SalonID AND cb.Created_Date >= DATEADD(day, -29, @Date) AND cb.Created_Date < DATEADD(day, 1, @Date)
        AND COALESCE(cb.IsDeleted, 0) = 0 AND COALESCE(b.IsDeleted, 0) = 0
    GROUP BY h.HairCut_Item_ID ORDER BY SUM(cb.HairCut_Item_Quantity) DESC;
    -- The admin screen needs these result sets alongside all six summary values.
    SELECT s.SalonMasterID, s.SalonName, s.LocationMasterID, l.LocationName,
        COALESCE(s.SalonAddress, N'''') AS SalonAddress, COALESCE(s.ContactNumber, N'''') AS ContactNumber,
        CONVERT(varchar(5), s.OpeningTime, 108) AS OpeningTime,
        CONVERT(varchar(5), s.ClosingTime, 108) AS ClosingTime,
        s.ChairCount, s.WeeklyHoliday, s.Latitude, s.Longitude,
        COALESCE(s.City, N'''') AS City, COALESCE(s.Pincode, N'''') AS Pincode,
        COALESCE(s.ImagePath, N'''') AS ImagePath, COALESCE(o.EmailAddress, N'''') AS OwnerEmailAddress,
        s.IsVisibleToCustomers
    FROM dbo.SalonOwner o
    JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
    JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1;
    SELECT SalonServiceID, ServiceName, Category, Price, DurationMinutes,
        COALESCE(Description, N'''') AS Description, COALESCE(ImagePath, N'''') AS ImagePath, IsActive
    FROM dbo.SalonService WHERE SalonMasterID = @SalonID ORDER BY IsActive DESC, ServiceName;
END;';
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
SELECT N'QR completion SQL update completed successfully' AS Result, DB_NAME() AS DatabaseName;
