-- Make the hidden appointment times appear on the owner Timings screen.
-- The admin API lists only TimeAllocation rows with IsDeleted = 0 and BookingAvailableStatus = 1.
-- This script switches BookingAvailableStatus on for the listed times.
--
-- Safe by default: with @Apply = 0 it only shows what it WOULD change and rolls back.
-- Set @Apply = 1 to commit. Rerunnable. Deleted rows (IsDeleted = 1) are never touched.
-- Enabling a time does not turn it on for any salon: each owner still ticks it on the Timings screen.
SET XACT_ABORT ON;
DECLARE @Apply bit = 0;   -- <-- change to 1 to apply

DECLARE @Wanted TABLE (FullTiming time(0) PRIMARY KEY);
INSERT @Wanted VALUES ('09:30'), ('10:00'), ('14:00'), ('14:30'), ('15:00');   -- add/remove times here

BEGIN TRANSACTION;

-- 1) What the database holds for each wanted time.
SELECT w.FullTiming,
       t.TimeAllocationID,
       t.IsDeleted,
       t.BookingAvailableStatus,
       CASE WHEN t.TimeAllocationID IS NULL THEN 'MISSING: no row, needs to be inserted'
            WHEN ISNULL(t.IsDeleted, 0) = 1 THEN 'DELETED: left untouched'
            WHEN t.BookingAvailableStatus = 1 THEN 'already visible'
            ELSE 'will be enabled' END AS Action
FROM @Wanted w
LEFT JOIN dbo.TimeAllocation t ON CONVERT(time(0), t.FullTiming) = w.FullTiming
ORDER BY w.FullTiming;

-- 2) Enable the hidden ones.
UPDATE t SET BookingAvailableStatus = 1
FROM dbo.TimeAllocation t
JOIN @Wanted w ON CONVERT(time(0), t.FullTiming) = w.FullTiming
WHERE ISNULL(t.IsDeleted, 0) = 0 AND t.BookingAvailableStatus = 0;
SELECT @@ROWCOUNT AS RowsEnabled;

-- 3) Every time the owner screen will now list.
SELECT CONVERT(varchar(5), FullTiming, 108) AS Time, TimeAllocationID
FROM dbo.TimeAllocation
WHERE ISNULL(IsDeleted, 0) = 0 AND BookingAvailableStatus = 1
ORDER BY FullTiming;

IF @Apply = 1 COMMIT TRANSACTION;
ELSE BEGIN ROLLBACK TRANSACTION; PRINT 'Preview only: nothing was changed. Set @Apply = 1 to commit.'; END
