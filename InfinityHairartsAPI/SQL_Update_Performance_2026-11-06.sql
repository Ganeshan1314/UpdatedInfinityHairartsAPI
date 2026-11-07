/*
  Performance indexes - 2026-11-06
  Run once on the Salonexa database. Safe to re-run: each index is created only
  if it does not already exist. Indexes are non-unique, so they cannot fail on
  existing duplicate data. No data or procedures are changed.
*/

-- 1) Customer login / profile lookup by mobile number.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CustomerRegistration')
                 AND name = N'IX_CustomerRegistration_MobileNo')
    CREATE INDEX IX_CustomerRegistration_MobileNo
    ON dbo.CustomerRegistration (MobileNo)
    INCLUDE (CustomerID, FirstName, EmailAddress, GeneralAddress, ImageName);
GO

-- 2) Time slots: occupied-times query filters bookings by BookingDate range.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.SeatBookingDetails')
                 AND name = N'IX_SeatBookingDetails_BookingDate')
    CREATE INDEX IX_SeatBookingDetails_BookingDate
    ON dbo.SeatBookingDetails (BookingDate)
    INCLUDE (BookingStatusMasterID, SalonMasterID, CustomerID);
GO

-- 3) Time slots: conflict check looks up a specific time slot across bookings.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CustomerTimeSelection')
                 AND name = N'IX_CustomerTimeSelection_TimeAllocation')
    CREATE INDEX IX_CustomerTimeSelection_TimeAllocation
    ON dbo.CustomerTimeSelection (TimeAllocationID)
    INCLUDE (SeatBookingDetailsID, CreatedOn);
GO

-- Optional check: all three should show as present.
SELECT name AS IndexName, OBJECT_NAME(object_id) AS TableName, is_disabled
FROM sys.indexes
WHERE name IN (N'IX_CustomerRegistration_MobileNo',
               N'IX_SeatBookingDetails_BookingDate',
               N'IX_CustomerTimeSelection_TimeAllocation');
GO
