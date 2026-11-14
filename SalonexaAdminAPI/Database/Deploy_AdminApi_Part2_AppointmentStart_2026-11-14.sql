-- =====================================================================================
-- Salonexa Admin API - database update, PART 2 of 2: Start appointment (2026-11-14)
-- Run Part 1 first. Run this one outside busy hours. Safe to run again.
-- =====================================================================================
-- BEFORE YOU RUN THIS FILE, take a backup of the database you are connected to:
--
--   BACKUP DATABASE [<your database name>]
--   TO DISK = N'D:\Backups\<your database name>_20261114.bak'
--   WITH COPY_ONLY, INIT, COMPRESSION;
--
-- HOW TO RUN: in SSMS choose the live database in the toolbar, open this file, press F5 once.
-- The first step checks the backup and the prerequisites. If it prints PREFLIGHT FAILED, nothing
-- is changed and the rest of the file is skipped. If you see a red error message in the middle of
-- a run, stop and send the full Messages tab. Every statement is safe to run again.

SET NOCOUNT ON;
SET NOEXEC OFF;
SET CONTEXT_INFO 0x00;
GO
BEGIN TRY
    DECLARE @SkipBackupCheck bit = 0;   -- set to 1 ONLY if you backed up by another method
    DECLARE @problems nvarchar(max) = N'';
    PRINT N'Server: ' + @@SERVERNAME + N'    Database: ' + DB_NAME();
    IF @SkipBackupCheck = 0 AND NOT EXISTS (
        SELECT 1 FROM msdb.dbo.backupset
        WHERE database_name = DB_NAME() AND type = 'D' AND backup_finish_date > DATEADD(HOUR, -24, GETDATE()))
        SET @problems += N'- No full backup of this database in the last 24 hours. Take one first (see the top of this file).' + NCHAR(10);
    IF OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL SET @problems += N'- Table dbo.SalonOwner is missing.' + NCHAR(10);
    IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL SET @problems += N'- Table dbo.SalonMaster is missing.' + NCHAR(10);
    IF OBJECT_ID(N'dbo.SeatBookingDetails', N'U') IS NULL SET @problems += N'- Table dbo.SeatBookingDetails is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
        SET @problems += N'- dbo.SeatBookingDetails.CompletionStatus is missing (the QR completion update was never installed).' + NCHAR(10);
    IF OBJECT_ID(N'dbo.CustomerTimeSelection', N'U') IS NULL SET @problems += N'- Table dbo.CustomerTimeSelection is missing.' + NCHAR(10);
    IF OBJECT_ID(N'dbo.TimeAllocation', N'U') IS NULL SET @problems += N'- Table dbo.TimeAllocation is missing.' + NCHAR(10);
    PRINT N'Info: this part changes dbo.SeatBookingDetails, a table the customer app also uses (two new empty columns and a wider status check). Run it outside busy hours.';
    IF @problems <> N''
    BEGIN
        PRINT N'PREFLIGHT FAILED - nothing was changed. Fix the following and run the file again:';
        PRINT @problems;
        SET CONTEXT_INFO 0xFA11;
        SET NOEXEC ON;
    END
    ELSE PRINT N'Preflight OK. Starting the update...';
END TRY
BEGIN CATCH
    PRINT N'PREFLIGHT FAILED - nothing was changed: ' + ERROR_MESSAGE();
    SET CONTEXT_INFO 0xFA11;
    SET NOEXEC ON;
END CATCH
GO
-- ===== Start appointment (Database/SalonOwnerAppointmentStart.sql) =====
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
GO
SET NOEXEC OFF;
GO
SET NOCOUNT ON;
IF SUBSTRING(CONTEXT_INFO(), 1, 2) = 0xFA11
BEGIN
    PRINT N'Nothing was changed because the preflight failed (see the message near the top).';
    SET NOEXEC ON;
END
PRINT N'--- Verification (every line must say ok) ---';
SELECT N'dbo.StartSalonOwnerBooking' AS [Check], CASE WHEN OBJECT_ID(N'dbo.StartSalonOwnerBooking', N'P') IS NULL THEN N'MISSING' ELSE N'ok' END AS [Status]
UNION ALL SELECT N'SeatBookingDetails.StartedUtc', CASE WHEN COL_LENGTH(N'dbo.SeatBookingDetails', N'StartedUtc') IS NULL THEN N'MISSING' ELSE N'ok' END
UNION ALL SELECT N'SeatBookingDetails.StartedByOwnerID', CASE WHEN COL_LENGTH(N'dbo.SeatBookingDetails', N'StartedByOwnerID') IS NULL THEN N'MISSING' ELSE N'ok' END
UNION ALL SELECT N'CompletionStatus check allows InProgress', CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.SeatBookingDetails') AND name = N'CK_SeatBookingDetails_CompletionStatus'
      AND definition LIKE N'%InProgress%') THEN N'ok' ELSE N'MISSING' END;
PRINT N'Part 2 finished. If any status above says MISSING, or a red error appeared earlier, stop and send the Messages tab.';
GO
SET NOEXEC OFF;
GO
