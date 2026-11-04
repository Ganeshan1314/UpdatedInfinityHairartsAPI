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
