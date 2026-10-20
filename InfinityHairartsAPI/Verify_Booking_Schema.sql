-- Read-only readiness checks. Select the database used by the deployed API.
SET NOCOUNT ON;

SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) AS ServerName,
       DB_NAME() AS DatabaseName,
       CASE WHEN DB_NAME() = N'InfinityHairArtsAdmin' THEN N'EXPECTED DATABASE'
            ELSE N'Check the API connection string before applying the migration' END AS TargetCheck;

SELECT N'SeatBookingDetails' AS Requirement,
       CASE WHEN OBJECT_ID(N'dbo.SeatBookingDetails',N'U') IS NOT NULL THEN N'READY' ELSE N'MISSING' END AS Status
UNION ALL
SELECT N'CustomerTimeSelection',
       CASE WHEN OBJECT_ID(N'dbo.CustomerTimeSelection',N'U') IS NOT NULL THEN N'READY' ELSE N'MISSING' END
UNION ALL
SELECT N'SalonMasterID booking column',
       CASE WHEN COL_LENGTH(N'dbo.SeatBookingDetails',N'SalonMasterID') IS NOT NULL THEN N'READY' ELSE N'MISSING' END
UNION ALL
SELECT N'ReservationVersion bigint NOT NULL',
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.SeatBookingDetails') AND name=N'ReservationVersion'
             AND system_type_id=127 AND is_nullable=0) THEN N'READY' ELSE N'MISSING OR INCOMPATIBLE' END
UNION ALL
SELECT N'Reservation lookup index',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
           WHERE object_id=OBJECT_ID(N'dbo.CustomerTimeSelection')
             AND name=N'IX_CustomerTimeSelection_BookingCustomer' AND is_disabled=0)
           THEN N'READY' ELSE N'MISSING OR DISABLED' END
UNION ALL
SELECT N'CustomerTimeSelection version trigger',
       CASE WHEN EXISTS (SELECT 1 FROM sys.triggers
           WHERE object_id=OBJECT_ID(N'dbo.TR_CustomerTimeSelection_Version') AND is_disabled=0)
           THEN N'READY' ELSE N'MISSING OR DISABLED' END
UNION ALL
SELECT N'SeatBookingDetails version trigger',
       CASE WHEN EXISTS (SELECT 1 FROM sys.triggers
           WHERE object_id=OBJECT_ID(N'dbo.TR_SeatBookingDetails_ReservationVersion') AND is_disabled=0)
           THEN N'READY' ELSE N'MISSING OR DISABLED' END;
