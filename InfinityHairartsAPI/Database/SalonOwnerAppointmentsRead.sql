-- Restores GET /api/Admin/appointments. Only a read procedure is created/updated.
-- Existing bookings, saved statuses, bills and completion workflows are unchanged.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.GetSalonOwnerAppointments', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerAppointments AS BEGIN SET NOCOUNT ON; END;');
GO
ALTER PROCEDURE dbo.GetSalonOwnerAppointments
    @OwnerID uniqueidentifier, @SalonID uniqueidentifier,
    @FromDate date = NULL, @ToDate date = NULL,
    @Status nvarchar(16) = N'Pending', @Page int = 1
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Today date = CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME()));
    IF NOT EXISTS (
        SELECT 1 FROM dbo.SalonOwner o
        JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID
          AND o.IsActive = 1 AND s.IsActive = 1
    )
        THROW 50013, 'Please sign in again.', 1;
    IF @Status IS NULL OR @Status NOT IN (N'Pending', N'InProgress', N'Completed') OR @Page IS NULL OR @Page < 1
        THROW 50002, 'Choose a valid appointment status and page.', 1;
    IF (@FromDate IS NULL AND @ToDate IS NOT NULL) OR (@FromDate IS NOT NULL AND @ToDate IS NULL)
        OR @FromDate < '20000101' OR @ToDate > '21001231' OR @FromDate > @ToDate
        THROW 50002, 'Choose both dates between 2000 and 2100, with From date on or before To date.', 1;

    -- One snapshot supplies counts and pagination; no date filter means all history.
    SELECT sb.SeatBookingDetailsID, sb.CustomerID, sb.BookingDate, sb.SeatCount, sb.CompletionStatus
    INTO #Appointments
    FROM dbo.SeatBookingDetails sb
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingStatusMasterID = 2
      AND (@FromDate IS NULL OR (sb.BookingDate >= @FromDate AND sb.BookingDate <= @ToDate));

    SELECT Pending = COUNT(CASE WHEN CompletionStatus = N'Pending' THEN 1 END),
        InProgress = COUNT(CASE WHEN CompletionStatus = N'InProgress' THEN 1 END),
        Completed = COUNT(CASE WHEN CompletionStatus = N'Completed' THEN 1 END)
    FROM #Appointments;
    SELECT COUNT(*) FROM #Appointments WHERE CompletionStatus = @Status;

    SELECT a.SeatBookingDetailsID,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(cr.FirstName, N' ', cr.LastName))), N''), N'Guest customer') AS CustomerName,
        COALESCE(cr.MobileNo, N'') AS MobileNo, a.BookingDate, timing.StartTime,
        COALESCE(a.SeatCount, 0) AS SeatCount, a.CompletionStatus AS BookingStatus,
        CASE WHEN a.CompletionStatus = N'Completed' THEN bill.Amount END AS BillAmount
    FROM #Appointments a
    LEFT JOIN dbo.CustomerRegistration cr ON cr.CustomerID = a.CustomerID
    OUTER APPLY (
        SELECT MIN(ta.FullTiming) AS StartTime
        FROM dbo.CustomerTimeSelection selected
        JOIN dbo.TimeAllocation ta ON ta.TimeAllocationID = selected.TimeAllocationID
        WHERE selected.SeatBookingDetailsID = a.SeatBookingDetailsID AND selected.CustomerID = a.CustomerID
    ) timing
    OUTER APPLY (
        SELECT SUM(b.BillIDTotalAmount) AS Amount
        FROM dbo.HairCut_Item_Bill_Master b
        WHERE b.SeatBookingDetailsID = a.SeatBookingDetailsID AND COALESCE(b.IsDeleted, 0) = 0
    ) bill
    WHERE a.CompletionStatus = @Status
    -- Today first, then upcoming days (tomorrow, ...), then past days newest-first. Today is IST (UTC+5:30).
    ORDER BY
        CASE WHEN a.BookingDate >= @Today THEN 0 ELSE 1 END,
        CASE WHEN a.BookingDate >= @Today THEN a.BookingDate END,
        CASE WHEN a.BookingDate < @Today THEN a.BookingDate END DESC,
        timing.StartTime, a.SeatBookingDetailsID
    OFFSET ((CAST(@Page AS bigint) - 1) * 10) ROWS FETCH NEXT 10 ROWS ONLY;
END;
GO
