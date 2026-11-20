-- Daily bookable times, selected separately by each salon owner, with optional per-date overrides.
-- Run on the API database before deploying the matching API and apps.
-- Rerunnable. Existing appointments, global time slots and booking rules are preserved.
-- New bookings require an explicit selection: no timings are enabled automatically.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL OR OBJECT_ID(N'dbo.TimeAllocation', N'U') IS NULL
    THROW 50070, 'Create the salon and time-allocation tables first.', 1;
IF OBJECT_ID(N'dbo.SalonTimingAvailability', N'U') IS NULL
    CREATE TABLE dbo.SalonTimingAvailability (
        SalonMasterID uniqueidentifier NOT NULL PRIMARY KEY REFERENCES dbo.SalonMaster(SalonMasterID),
        Version bigint NOT NULL DEFAULT 0,
        UpdatedByOwnerID uniqueidentifier NOT NULL,
        UpdatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
    );
IF OBJECT_ID(N'dbo.SalonBookableTime', N'U') IS NULL
    CREATE TABLE dbo.SalonBookableTime (
        SalonMasterID uniqueidentifier NOT NULL REFERENCES dbo.SalonMaster(SalonMasterID),
        TimeAllocationID uniqueidentifier NOT NULL REFERENCES dbo.TimeAllocation(TimeAllocationID),
        PRIMARY KEY(SalonMasterID, TimeAllocationID)
    );
IF OBJECT_ID(N'dbo.SalonTimingDateOverride', N'U') IS NULL
    CREATE TABLE dbo.SalonTimingDateOverride (
        SalonMasterID uniqueidentifier NOT NULL REFERENCES dbo.SalonMaster(SalonMasterID),
        BookingDate date NOT NULL,
        UpdatedByOwnerID uniqueidentifier NOT NULL,
        UpdatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY(SalonMasterID, BookingDate)
    );
-- A date with an override row uses ONLY its own times (none = closed that day). Other dates use SalonBookableTime.
IF OBJECT_ID(N'dbo.SalonBookableTimeDate', N'U') IS NULL
    CREATE TABLE dbo.SalonBookableTimeDate (
        SalonMasterID uniqueidentifier NOT NULL,
        BookingDate date NOT NULL,
        TimeAllocationID uniqueidentifier NOT NULL REFERENCES dbo.TimeAllocation(TimeAllocationID),
        PRIMARY KEY(SalonMasterID, BookingDate, TimeAllocationID),
        FOREIGN KEY(SalonMasterID, BookingDate) REFERENCES dbo.SalonTimingDateOverride(SalonMasterID, BookingDate) ON DELETE CASCADE
    );
COMMIT TRANSACTION;
