-- Booking status tabs, latest 10 appointments and completed booking amounts.
-- Run after the QR workflow migration, before deploying the matching API/admin app.
-- Generated from Database/BookingAmounts.sql, BookingStatusTabs.sql, SalonOwnerDashboard.sql and BookingQrWorkflow.sql.
-- Keep admin amounts aligned with the customer booking history's recorded service totals.
-- Some legacy bill headers leave BillIDTotalAmount at zero even when service lines are priced.
IF OBJECT_ID('dbo.BookingBillAmounts','V') IS NULL
    EXEC('CREATE VIEW dbo.BookingBillAmounts AS SELECT CAST(NULL AS uniqueidentifier) SeatBookingDetailsID, CAST(NULL AS decimal(38,2)) BillAmount WHERE 1=0');
GO
ALTER VIEW dbo.BookingBillAmounts
AS
    SELECT bill.SeatBookingDetailsID,
        SUM(CASE WHEN items.LineCount>0 THEN COALESCE(items.LineTotal,0) ELSE bill.BillIDTotalAmount END) BillAmount
    FROM dbo.HairCut_Item_Bill_Master bill
    OUTER APPLY (
        SELECT COUNT_BIG(*) LineCount,
            SUM(CASE WHEN COALESCE(item.IsDeleted,0)=0 THEN item.HairCut_Item_Total ELSE 0 END) LineTotal
        FROM dbo.HairCut_Item_Customer_Bill item WHERE item.UniqueBillID=bill.UniqueBillID
    ) items
    WHERE COALESCE(bill.IsDeleted,0)=0
    GROUP BY bill.SeatBookingDetailsID;
GO

SET XACT_ABORT ON;
GO
IF COL_LENGTH('dbo.SeatBookingDetails','CompletionStatus') IS NULL
    THROW 50003, 'Run the QR completion migration before adding booking status tabs.', 1;
GO
IF EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.SeatBookingDetails') AND name='CK_SeatBookingDetails_CompletionStatus')
    ALTER TABLE dbo.SeatBookingDetails DROP CONSTRAINT CK_SeatBookingDetails_CompletionStatus;
ALTER TABLE dbo.SeatBookingDetails WITH CHECK ADD CONSTRAINT CK_SeatBookingDetails_CompletionStatus
    CHECK(CompletionStatus IN (N'Pending',N'InProgress',N'Completed'));
IF COL_LENGTH('dbo.SeatBookingDetails','StartedUtc') IS NULL
    ALTER TABLE dbo.SeatBookingDetails ADD StartedUtc datetime2(0) NULL;
IF COL_LENGTH('dbo.SeatBookingDetails','StartedByOwnerID') IS NULL
    ALTER TABLE dbo.SeatBookingDetails ADD StartedByOwnerID uniqueidentifier NULL;
GO
IF OBJECT_ID('dbo.getOwnerAppointments','P') IS NULL EXEC('CREATE PROCEDURE dbo.getOwnerAppointments AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.getOwnerAppointments
    @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @FromDate date=NULL, @ToDate date=NULL,
    @Status nvarchar(16), @Page int=1, @PageSize int=10
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID=o.SalonMasterID
        WHERE o.SalonOwnerID=@OwnerID AND o.SalonMasterID=@SalonID AND o.IsActive=1 AND s.IsActive=1)
        THROW 50013, 'Please sign in again.', 1;
    IF (@FromDate IS NULL AND @ToDate IS NOT NULL) OR (@FromDate IS NOT NULL AND @ToDate IS NULL)
        OR (@FromDate IS NOT NULL AND (@FromDate<'20000101' OR @ToDate>'21001231' OR @FromDate>@ToDate))
        OR @Status IS NULL OR @Status NOT IN (N'Pending',N'InProgress',N'Completed')
        OR @Page<1 OR @Page>100000 OR @PageSize<1 OR @PageSize>100
        THROW 50002, 'Choose a valid date range, booking status and page.', 1;

    -- Both result sets come from the same snapshot, independent of the 30-row overview preview.
    SELECT sb.SeatBookingDetailsID,sb.CustomerID,sb.BookingDate,sb.SeatCount,sb.CompletionStatus
    INTO #bookings FROM dbo.SeatBookingDetails sb
    WHERE sb.SalonMasterID=@SalonID AND sb.BookingStatusMasterID=2
      AND (@FromDate IS NULL OR (sb.BookingDate>=@FromDate AND sb.BookingDate<DATEADD(day,1,@ToDate)));
    SELECT Pending=COUNT(CASE WHEN CompletionStatus=N'Pending' THEN 1 END),
        InProgress=COUNT(CASE WHEN CompletionStatus=N'InProgress' THEN 1 END),
        Completed=COUNT(CASE WHEN CompletionStatus=N'Completed' THEN 1 END) FROM #bookings;
    ;WITH appointments AS (
    SELECT b.SeatBookingDetailsID,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(c.FirstName,N' ',c.LastName))),N''),N'Guest customer') CustomerName,
        COALESCE(c.MobileNo,N'') MobileNo,b.BookingDate,
        (SELECT MIN(t.FullTiming) FROM dbo.CustomerTimeSelection ct JOIN dbo.TimeAllocation t ON t.TimeAllocationID=ct.TimeAllocationID
         WHERE ct.SeatBookingDetailsID=b.SeatBookingDetailsID) StartTime,
        COALESCE(b.SeatCount,0) SeatCount,b.CompletionStatus BookingStatus,
        CASE WHEN b.CompletionStatus=N'Completed' THEN
            (SELECT bill.BillAmount FROM dbo.BookingBillAmounts bill
             WHERE bill.SeatBookingDetailsID=b.SeatBookingDetailsID)
        END BillAmount
    FROM #bookings b LEFT JOIN dbo.CustomerRegistration c ON c.CustomerID=b.CustomerID
    WHERE b.CompletionStatus=@Status)
    SELECT * FROM appointments
    ORDER BY CASE WHEN @FromDate IS NULL THEN BookingDate END DESC,
        CASE WHEN @FromDate IS NULL THEN StartTime END DESC,
        CASE WHEN @FromDate IS NOT NULL THEN BookingDate END,
        CASE WHEN @FromDate IS NOT NULL THEN StartTime END,
        SeatBookingDetailsID
    OFFSET ((@Page-1)*@PageSize) ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
IF OBJECT_ID('dbo.startOwnerBooking','P') IS NULL EXEC('CREATE PROCEDURE dbo.startOwnerBooking AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.startOwnerBooking
    @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @BookingID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID=o.SalonMasterID
            WHERE o.SalonOwnerID=@OwnerID AND o.SalonMasterID=@SalonID AND o.IsActive=1 AND s.IsActive=1)
            THROW 50013, 'Please sign in again.', 1;
        DECLARE @date date,@time time,@status nvarchar(16),@started datetime2(0);
        SELECT @date=sb.BookingDate,@status=sb.CompletionStatus,@started=sb.StartedUtc
        FROM dbo.SeatBookingDetails sb WITH(UPDLOCK,HOLDLOCK)
        WHERE sb.SeatBookingDetailsID=@BookingID AND sb.SalonMasterID=@SalonID AND sb.BookingStatusMasterID=2;
        IF @status IS NULL THROW 50010, 'This confirmed booking could not be found for your salon.', 1;
        IF @status=N'Completed' THROW 50012, 'This booking is already completed.', 1;
        SELECT @time=MIN(t.FullTiming) FROM dbo.CustomerTimeSelection ct
        JOIN dbo.TimeAllocation t ON t.TimeAllocationID=ct.TimeAllocationID WHERE ct.SeatBookingDetailsID=@BookingID;
        IF @date IS NULL OR @time IS NULL THROW 50012, 'This booking does not have a valid appointment time.', 1;
        IF DATEADD(second,DATEDIFF(second,CAST('00:00:00' AS time),@time),CAST(@date AS datetime2))>DATEADD(minute,330,SYSUTCDATETIME())
            THROW 50011, 'This appointment can be started at or after the booked time.', 1;
        DECLARE @already bit=CASE WHEN @status=N'InProgress' THEN 1 ELSE 0 END;
        IF @already=0
        BEGIN
            SET @started=SYSUTCDATETIME();
            UPDATE dbo.SeatBookingDetails SET CompletionStatus=N'InProgress',StartedUtc=@started,StartedByOwnerID=@OwnerID
            WHERE SeatBookingDetailsID=@BookingID AND SalonMasterID=@SalonID;
        END;
        COMMIT;
        SELECT SeatBookingDetailsID=@BookingID,BookingStatus=N'InProgress',StartedUtc=@started,WasAlreadyStarted=@already;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO

SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
    THROW 50003, 'Run BookingQrWorkflow.sql before SalonOwnerDashboard.sql.', 1;
GO

IF OBJECT_ID(N'dbo.GetSalonOwnerDashboard', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerDashboard AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerDashboard @OwnerID uniqueidentifier, @SalonID uniqueidentifier,
    @Date date = NULL, @FromDate date = NULL, @ToDate date = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1)
        THROW 50013, 'Please sign in again.', 1;
    DECLARE @RangeStart date = COALESCE(@FromDate, @ToDate, @Date, CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME())));
    DECLARE @RangeEnd date = COALESCE(@ToDate, @FromDate, @Date, @RangeStart);
    IF @RangeStart < '20000101' OR @RangeEnd > '21001231' OR @RangeStart > @RangeEnd
        THROW 50002, 'Choose dates between 2000 and 2100, with From date on or before To date.', 1;
    -- Service creation timestamps are UTC; date controls represent India calendar days.
    DECLARE @ServiceStartUtc datetime2 = DATEADD(minute, -330, CAST(@RangeStart AS datetime2));
    DECLARE @ServiceEndUtc datetime2 = DATEADD(minute, -330, DATEADD(day, 1, CAST(@RangeEnd AS datetime2)));
    -- CompletionStatus is saved atomically with the appointment QR audit row.
    SELECT TodayBookings = COUNT(*),
        TodaySeats = COALESCE(SUM(sb.SeatCount), 0),
        PendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Pending' THEN 1 END),
        InProgressBookings = COUNT(CASE WHEN sb.CompletionStatus = N'InProgress' THEN 1 END),
        CompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Completed' THEN 1 END),
        TotalBookings = COUNT(*),
        TotalPendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Pending' THEN 1 END),
        TotalInProgressBookings = COUNT(CASE WHEN sb.CompletionStatus = N'InProgress' THEN 1 END),
        TotalCompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Completed' THEN 1 END),
        CompletedRevenue = (SELECT COALESCE(SUM(b.BillAmount), 0)
            FROM dbo.BookingBillAmounts b
            JOIN dbo.SeatBookingDetails completed ON completed.SeatBookingDetailsID = b.SeatBookingDetailsID
            WHERE completed.SalonMasterID = @SalonID AND completed.BookingStatusMasterID = 2
                AND completed.CompletionStatus = N'Completed'
                AND completed.BookingDate >= @RangeStart AND completed.BookingDate < DATEADD(day, 1, @RangeEnd)),
        TodayRevenue = (SELECT COALESCE(SUM(b.BillIDTotalAmount), 0) FROM dbo.HairCut_Item_Bill_Master b
            JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
            WHERE sb.SalonMasterID = @SalonID AND b.CreatedDate >= @RangeStart AND b.CreatedDate < DATEADD(day, 1, @RangeEnd) AND COALESCE(b.IsDeleted, 0) = 0),
        TotalCustomers = COUNT(DISTINCT sb.CustomerID),
        ActiveServices = (SELECT COUNT(*) FROM dbo.SalonService WHERE SalonMasterID = @SalonID AND IsActive = 1
            AND CreatedUtc >= @ServiceStartUtc AND CreatedUtc < @ServiceEndUtc),
        UpcomingBookings = COUNT(CASE WHEN sb.BookingDate >= CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME()))
            AND sb.CompletionStatus = N'Pending' THEN 1 END)
    FROM dbo.SeatBookingDetails sb
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingStatusMasterID = 2
        AND sb.BookingDate >= @RangeStart AND sb.BookingDate < DATEADD(day, 1, @RangeEnd);
    SELECT TOP(30) sb.SeatBookingDetailsID,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(cr.FirstName, ' ', cr.LastName))), ''), 'Guest customer') AS CustomerName,
        COALESCE(cr.MobileNo, '') AS MobileNo, sb.BookingDate,
        MIN(ta.FullTiming) AS StartTime, COALESCE(sb.SeatCount, 0) AS SeatCount,
        sb.CompletionStatus AS BookingStatus,
        CASE WHEN sb.CompletionStatus = N'Completed' THEN
            (SELECT bill.BillAmount FROM dbo.BookingBillAmounts bill
             WHERE bill.SeatBookingDetailsID = sb.SeatBookingDetailsID)
        END AS BillAmount
    FROM dbo.SeatBookingDetails sb
    LEFT JOIN dbo.CustomerRegistration cr ON cr.CustomerID = sb.CustomerID
    LEFT JOIN dbo.CustomerTimeSelection ct ON ct.SeatBookingDetailsID = sb.SeatBookingDetailsID
    LEFT JOIN dbo.TimeAllocation ta ON ta.TimeAllocationID = ct.TimeAllocationID
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingDate >= @RangeStart AND sb.BookingDate < DATEADD(day, 1, @RangeEnd) AND sb.BookingStatusMasterID = 2
    GROUP BY sb.SeatBookingDetailsID, cr.FirstName, cr.LastName, cr.MobileNo, sb.BookingDate, sb.SeatCount, sb.CompletionStatus
    ORDER BY sb.BookingDate, MIN(ta.FullTiming), sb.SeatBookingDetailsID;
    ;WITH Dates AS (
        SELECT @RangeStart AS BookingDate
        UNION ALL
        SELECT DATEADD(day, 1, BookingDate) FROM Dates WHERE BookingDate < @RangeEnd
    )
    SELECT d.BookingDate, COUNT(sb.SeatBookingDetailsID) AS BookingCount FROM Dates d
    LEFT JOIN dbo.SeatBookingDetails sb ON sb.SalonMasterID = @SalonID AND sb.BookingDate = d.BookingDate AND sb.BookingStatusMasterID = 2
    GROUP BY d.BookingDate ORDER BY d.BookingDate OPTION (MAXRECURSION 0);
    SELECT TOP(4) h.HairCut_Item_ID AS HairCutItemID, MAX(h.HairCut_Item_Name) AS ServiceName,
        SUM(cb.HairCut_Item_Quantity) AS BookingCount, SUM(cb.HairCut_Item_Total) AS Revenue
    FROM dbo.HairCut_Item_Customer_Bill cb
    JOIN dbo.HairCut_Item_List h ON h.HairCut_Item_ID = cb.HairCut_Item_ID
    JOIN dbo.HairCut_Item_Bill_Master b ON b.UniqueBillID = cb.UniqueBillID
    JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
    WHERE sb.SalonMasterID = @SalonID AND cb.Created_Date >= @RangeStart AND cb.Created_Date < DATEADD(day, 1, @RangeEnd)
        AND COALESCE(cb.IsDeleted, 0) = 0 AND COALESCE(b.IsDeleted, 0) = 0
    GROUP BY h.HairCut_Item_ID ORDER BY SUM(cb.HairCut_Item_Quantity) DESC;
    -- The admin screen needs these result sets alongside all six summary values.
    SELECT s.SalonMasterID, s.SalonName, s.LocationMasterID, l.LocationName,
        COALESCE(s.SalonAddress, N'') AS SalonAddress, COALESCE(s.ContactNumber, N'') AS ContactNumber,
        CONVERT(varchar(5), s.OpeningTime, 108) AS OpeningTime,
        CONVERT(varchar(5), s.ClosingTime, 108) AS ClosingTime,
        s.ChairCount, s.WeeklyHoliday, s.Latitude, s.Longitude,
        COALESCE(s.City, N'') AS City, COALESCE(s.Pincode, N'') AS Pincode,
        COALESCE(s.ImagePath, N'') AS ImagePath, COALESCE(o.EmailAddress, N'') AS OwnerEmailAddress,
        s.IsVisibleToCustomers
    FROM dbo.SalonOwner o
    JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
    JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1;
    SELECT SalonServiceID, ServiceName, Category, Price, DurationMinutes,
        COALESCE(Description, N'') AS Description, COALESCE(ImagePath, N'') AS ImagePath, IsActive
    FROM dbo.SalonService WHERE SalonMasterID = @SalonID ORDER BY IsActive DESC, ServiceName;
END;
GO

ALTER PROCEDURE dbo.completeBookingFromQr
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
        THROW 50013, 'This admin account is not authorized for the selected salon.', 1;
    END;

    DECLARE @BookingDate DATE;
    DECLARE @BookingTime TIME;
    DECLARE @CustomerName NVARCHAR(301);

    SELECT
        @BookingDate = CONVERT(DATE, SB.BookingDate),
        @BookingTime = TA.FullTiming,
        @CustomerName = COALESCE(
            NULLIF(LTRIM(RTRIM(CONCAT(CR.FirstName, N' ', CR.LastName))), N''),
            N'Guest customer')
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
        THROW 50010, 'This QR code does not match a confirmed booking for your salon.', 1;
    END;

    DECLARE @AppointmentStart DATETIME2(0) = DATEADD(
        SECOND,
        DATEDIFF(SECOND, CAST('00:00:00' AS TIME), @BookingTime),
        CAST(@BookingDate AS DATETIME2(0)));

    -- Booking dates and slot times are in India time, regardless of server zone.
    IF @AppointmentStart > DATEADD(MINUTE, 330, SYSUTCDATETIME())
    BEGIN
        THROW 50011, 'This appointment is still Upcoming. It can be completed at or after the booked time.', 1;
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

    -- Preserve an explicitly started visit until all of its appointment QRs are completed.
    DECLARE @BookingStatus NVARCHAR(16) = (SELECT CASE WHEN CompletionStatus=N'InProgress' THEN N'InProgress' ELSE N'Pending' END
        FROM dbo.SeatBookingDetails WHERE SeatBookingDetailsID=@SeatBookingDetailsID);
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
        SET @BookingStatus = N'Completed';

    UPDATE dbo.SeatBookingDetails
    SET CompletionStatus = @BookingStatus,
        CompletedUtc = CASE WHEN @BookingStatus = N'Completed' THEN
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
END;
GO
