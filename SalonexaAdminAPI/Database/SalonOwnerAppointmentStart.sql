-- Start an appointment (2026-11-14). Back up the database first. Re-runnable.
-- Lets a salon owner move a confirmed booking from Pending to InProgress at or after its booked time.
-- Changes on dbo.SeatBookingDetails (shared with the customer app, which never reads CompletionStatus):
--   * adds nullable StartedUtc and StartedByOwnerID columns;
--   * widens CK_SeatBookingDetails_CompletionStatus to also allow N'InProgress'.
-- Existing rows and statuses are not modified. Requires BookingQrWorkflow.sql first.
SET XACT_ABORT ON;
GO
IF COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
    THROW 50003, 'Run BookingQrWorkflow.sql before SalonOwnerAppointmentStart.sql.', 1;
GO
IF COL_LENGTH(N'dbo.SeatBookingDetails', N'StartedUtc') IS NULL
    ALTER TABLE dbo.SeatBookingDetails ADD StartedUtc datetime2(0) NULL;
IF COL_LENGTH(N'dbo.SeatBookingDetails', N'StartedByOwnerID') IS NULL
    ALTER TABLE dbo.SeatBookingDetails ADD StartedByOwnerID uniqueidentifier NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.SeatBookingDetails') AND name = N'CK_SeatBookingDetails_CompletionStatus'
      AND definition LIKE N'%InProgress%')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.SeatBookingDetails') AND name = N'CK_SeatBookingDetails_CompletionStatus')
        ALTER TABLE dbo.SeatBookingDetails DROP CONSTRAINT CK_SeatBookingDetails_CompletionStatus;
    ALTER TABLE dbo.SeatBookingDetails WITH CHECK ADD CONSTRAINT CK_SeatBookingDetails_CompletionStatus
        CHECK (CompletionStatus IN (N'Pending', N'InProgress', N'Completed'));
END;
GO
IF OBJECT_ID(N'dbo.StartSalonOwnerBooking', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.StartSalonOwnerBooking AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.StartSalonOwnerBooking
    @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @BookingID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        IF NOT EXISTS (SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
            WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1)
            THROW 50013, 'Please sign in again.', 1;
        DECLARE @date date, @time time, @status nvarchar(16), @started datetime2(0);
        SELECT @date = sb.BookingDate, @status = sb.CompletionStatus, @started = sb.StartedUtc
        FROM dbo.SeatBookingDetails sb WITH (UPDLOCK, HOLDLOCK)
        WHERE sb.SeatBookingDetailsID = @BookingID AND sb.SalonMasterID = @SalonID AND sb.BookingStatusMasterID = 2;
        IF @status IS NULL THROW 50010, 'This confirmed booking could not be found for your salon.', 1;
        IF @status = N'Completed' THROW 50012, 'This booking is already completed.', 1;
        SELECT @time = MIN(t.FullTiming) FROM dbo.CustomerTimeSelection ct
        JOIN dbo.TimeAllocation t ON t.TimeAllocationID = ct.TimeAllocationID WHERE ct.SeatBookingDetailsID = @BookingID;
        IF @date IS NULL OR @time IS NULL THROW 50012, 'This booking does not have a valid appointment time.', 1;
        -- Booking dates and times are India local time (UTC+05:30).
        IF DATEADD(second, DATEDIFF(second, CAST('00:00:00' AS time), @time), CAST(@date AS datetime2))
            > DATEADD(minute, 330, SYSUTCDATETIME())
            THROW 50011, 'This appointment can be started at or after the booked time.', 1;
        DECLARE @already bit = CASE WHEN @status = N'InProgress' THEN 1 ELSE 0 END;
        IF @already = 0
        BEGIN
            SET @started = SYSUTCDATETIME();
            UPDATE dbo.SeatBookingDetails
            SET CompletionStatus = N'InProgress', StartedUtc = @started, StartedByOwnerID = @OwnerID
            WHERE SeatBookingDetailsID = @BookingID AND SalonMasterID = @SalonID;
        END;
        COMMIT TRANSACTION;
        SELECT SeatBookingDetailsID = @BookingID, BookingStatus = N'InProgress', StartedUtc = @started, WasAlreadyStarted = @already;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
