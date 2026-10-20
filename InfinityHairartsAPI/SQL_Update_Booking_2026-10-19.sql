/*
Booking time-selection SQL update - 19 October 2026
Target database: InfinityHairArtsAdmin (select it in SSMS before running).
Run the ENTIRE file in a new SSMS query window; SQLCMD mode is not needed.

Before running: back up the database and stop/drain old API booking workers.
After success: deploy the matching API and Ionic app before reopening bookings.
This file contains database changes only. The API supplies reservation locking.
Existing booking rows are preserved. Rerunning the file is supported.
The existing salon/booking schema must already be installed.

All changes and validation execute in one transaction. On an error, the whole
update rolls back. The script refuses an already-open transaction. Use the
printed server/database result to verify the target connection.

Generated from AtomicTimeReservations.sql; migration source SHA-256:
0c3c50fd304489572b64259480f43538506a72a5d8252246b8fb847d12686d95
*/
-- RAISERROR/RETURN rejects an ambient transaction without rolling it back.
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
    THROW 51010, 'Select the InfinityHairArtsAdmin database before running this file.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.SeatBookingDetails', N'U') IS NULL
       OR OBJECT_ID(N'dbo.CustomerTimeSelection', N'U') IS NULL
        THROW 51012, 'Required booking tables are missing. No update was applied.', 1;
    IF COL_LENGTH(N'dbo.SeatBookingDetails', N'SalonMasterID') IS NULL
        THROW 51013, 'Install the existing salon booking schema (CartSalonNames.sql) first.', 1;

    -- Migration batch 1. Dynamic execution permits transactional trigger DDL
    -- and compilation after adding the version column (SQL Server 2014+).
    EXEC sys.sp_executesql N'-- Apply before deploying the API. Additive: existing bookings are preserved.
SET XACT_ABORT ON;';

    -- Migration batch 2. Dynamic execution permits transactional trigger DDL
    -- and compilation after adding the version column (SQL Server 2014+).
    EXEC sys.sp_executesql N'IF COL_LENGTH(''dbo.SeatBookingDetails'', ''ReservationVersion'') IS NULL
    ALTER TABLE dbo.SeatBookingDetails ADD ReservationVersion bigint NOT NULL
        CONSTRAINT DF_SeatBookingDetails_ReservationVersion DEFAULT (0) WITH VALUES;';

    -- Migration batch 3. Dynamic execution permits transactional trigger DDL
    -- and compilation after adding the version column (SQL Server 2014+).
    EXEC sys.sp_executesql N'IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(''dbo.CustomerTimeSelection'') AND name=''IX_CustomerTimeSelection_BookingCustomer'')
    CREATE INDEX IX_CustomerTimeSelection_BookingCustomer ON dbo.CustomerTimeSelection(SeatBookingDetailsID,CustomerID)
        INCLUDE(TimeAllocationID,CreatedOn);';

    -- Migration batch 4. Dynamic execution permits transactional trigger DDL
    -- and compilation after adding the version column (SQL Server 2014+).
    EXEC sys.sp_executesql N'IF OBJECT_ID(''dbo.TR_CustomerTimeSelection_Version'',''TR'') IS NULL
    EXEC(''CREATE TRIGGER dbo.TR_CustomerTimeSelection_Version ON dbo.CustomerTimeSelection AFTER INSERT,DELETE,UPDATE AS RETURN'');';

    -- Migration batch 5. Dynamic execution permits transactional trigger DDL
    -- and compilation after adding the version column (SQL Server 2014+).
    EXEC sys.sp_executesql N'ALTER TRIGGER dbo.TR_CustomerTimeSelection_Version ON dbo.CustomerTimeSelection AFTER INSERT,DELETE,UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE s SET ReservationVersion=ReservationVersion+1
    FROM dbo.SeatBookingDetails s
    JOIN (SELECT SeatBookingDetailsID FROM inserted UNION SELECT SeatBookingDetailsID FROM deleted) changed
      ON changed.SeatBookingDetailsID=s.SeatBookingDetailsID;
END;';

    -- Migration batch 6. Dynamic execution permits transactional trigger DDL
    -- and compilation after adding the version column (SQL Server 2014+).
    EXEC sys.sp_executesql N'IF OBJECT_ID(''dbo.TR_SeatBookingDetails_ReservationVersion'',''TR'') IS NULL
    EXEC(''CREATE TRIGGER dbo.TR_SeatBookingDetails_ReservationVersion ON dbo.SeatBookingDetails AFTER UPDATE AS RETURN'');';

    -- Migration batch 7. Dynamic execution permits transactional trigger DDL
    -- and compilation after adding the version column (SQL Server 2014+).
    EXEC sys.sp_executesql N'ALTER TRIGGER dbo.TR_SeatBookingDetails_ReservationVersion ON dbo.SeatBookingDetails AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT (UPDATE(BookingDate) OR UPDATE(SalonMasterID) OR UPDATE(SeatCount) OR UPDATE(BookingStatusMasterID)) RETURN;
    UPDATE s SET ReservationVersion=s.ReservationVersion+1
    FROM dbo.SeatBookingDetails s JOIN inserted i ON i.SeatBookingDetailsID=s.SeatBookingDetailsID
    JOIN deleted d ON d.SeatBookingDetailsID=i.SeatBookingDetailsID
    WHERE EXISTS (SELECT i.BookingDate,i.SalonMasterID,i.SeatCount,i.BookingStatusMasterID
                  EXCEPT SELECT d.BookingDate,d.SalonMasterID,d.SeatCount,d.BookingStatusMasterID);
END;';

    ENABLE TRIGGER dbo.TR_CustomerTimeSelection_Version ON dbo.CustomerTimeSelection;
    ENABLE TRIGGER dbo.TR_SeatBookingDetails_ReservationVersion ON dbo.SeatBookingDetails;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.SeatBookingDetails')
          AND name=N'ReservationVersion' AND system_type_id=127 AND is_nullable=0
    )
        THROW 51014, 'ReservationVersion must be a non-null bigint. All changes rolled back.', 1;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.CustomerTimeSelection')
          AND name=N'IX_CustomerTimeSelection_BookingCustomer' AND is_disabled=0
    )
        THROW 51015, 'The reservation lookup index is missing or disabled. All changes rolled back.', 1;
    IF (SELECT COUNT(*) FROM sys.triggers
        WHERE object_id IN (OBJECT_ID(N'dbo.TR_CustomerTimeSelection_Version'),
                            OBJECT_ID(N'dbo.TR_SeatBookingDetails_ReservationVersion'))
          AND is_disabled=0) <> 2
        THROW 51016, 'Reservation version triggers are not enabled. All changes rolled back.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- Successful execution returns these verification results, without customer data.
SELECT N'Booking SQL update completed successfully' AS Result,
       CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) AS ServerName,
       DB_NAME() AS DatabaseName;
SELECT name AS VersionColumn, TYPE_NAME(system_type_id) AS DataType, is_nullable
FROM sys.columns
WHERE object_id=OBJECT_ID(N'dbo.SeatBookingDetails') AND name=N'ReservationVersion';
SELECT name AS LookupIndex, is_disabled
FROM sys.indexes
WHERE object_id=OBJECT_ID(N'dbo.CustomerTimeSelection')
  AND name=N'IX_CustomerTimeSelection_BookingCustomer';
SELECT name AS VersionTrigger, is_disabled
FROM sys.triggers
WHERE object_id IN (OBJECT_ID(N'dbo.TR_CustomerTimeSelection_Version'),
                    OBJECT_ID(N'dbo.TR_SeatBookingDetails_ReservationVersion'));
