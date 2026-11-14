-- Salon details for the signed-in owner (2026-11-14). Back up the database first. Re-runnable.
-- Defines dbo.GetSalonOwnerProfile and dbo.SaveSalonOwnerProfile exactly as installed on the existing
-- database, so a new server can be built from source control.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL OR OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL OR OBJECT_ID(N'dbo.LocationMaster', N'U') IS NULL
    THROW 50003, 'Install the salon, location and salon-owner schemas before SalonOwnerSalonProfile.sql.', 1;
GO
IF COL_LENGTH(N'dbo.SalonMaster', N'OpeningTime') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'ClosingTime') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'ChairCount') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'WeeklyHoliday') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Latitude') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Longitude') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'City') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Pincode') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'ImagePath') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'IsVisibleToCustomers') IS NULL
    THROW 50003, 'SalonMaster is missing the opening hours, chair, map, photo or visibility columns.', 1;
GO
IF OBJECT_ID(N'dbo.GetSalonOwnerProfile', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerProfile AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerProfile @OwnerID uniqueidentifier, @SalonID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.SalonMasterID, s.SalonName, s.LocationMasterID, l.LocationName,
        COALESCE(s.SalonAddress, '') AS SalonAddress, COALESCE(s.ContactNumber, '') AS ContactNumber,
        CONVERT(varchar(5), s.OpeningTime, 108) AS OpeningTime,
        CONVERT(varchar(5), s.ClosingTime, 108) AS ClosingTime, s.ChairCount, s.WeeklyHoliday, s.Latitude, s.Longitude,
        COALESCE(s.City, '') AS City, COALESCE(s.Pincode, '') AS Pincode,
        COALESCE(s.ImagePath, '') AS ImagePath, COALESCE(o.EmailAddress, '') AS OwnerEmailAddress,
        s.IsVisibleToCustomers
    FROM dbo.SalonOwner o
    JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
    JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1;
END;
GO
IF OBJECT_ID(N'dbo.SaveSalonOwnerProfile', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.SaveSalonOwnerProfile AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.SaveSalonOwnerProfile
    @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @SalonName nvarchar(200), @LocationMasterID uniqueidentifier,
    @SalonAddress nvarchar(500), @ContactNumber nvarchar(20), @OpeningTime time(0), @ClosingTime time(0),
    @ChairCount int, @WeeklyHoliday tinyint = NULL, @Latitude decimal(9,6) = NULL, @Longitude decimal(9,6) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1)
        THROW 50013, 'Please sign in again.', 1;
    IF NOT EXISTS(SELECT 1 FROM dbo.LocationMaster WHERE LocationMasterID = @LocationMasterID AND IsActive = 1)
        THROW 50015, 'Select an active location.', 1;
    IF NULLIF(LTRIM(RTRIM(@SalonName)), '') IS NULL OR @OpeningTime IS NULL OR @ClosingTime IS NULL
        OR @ClosingTime <= @OpeningTime OR @ChairCount NOT BETWEEN 1 AND 100
        OR (@WeeklyHoliday IS NOT NULL AND @WeeklyHoliday > 6)
        THROW 50015, 'Please check your salon details.', 1;
    IF (@Latitude IS NULL AND @Longitude IS NOT NULL) OR (@Latitude IS NOT NULL AND @Longitude IS NULL)
        OR @Latitude NOT BETWEEN -90 AND 90 OR @Longitude NOT BETWEEN -180 AND 180
        THROW 50015, 'Select a valid map pin.', 1;
    UPDATE dbo.SalonMaster SET SalonName = @SalonName, LocationMasterID = @LocationMasterID,
        SalonAddress = @SalonAddress, ContactNumber = @ContactNumber, OpeningTime = @OpeningTime,
        ClosingTime = @ClosingTime, ChairCount = @ChairCount, WeeklyHoliday = @WeeklyHoliday, Latitude = @Latitude, Longitude = @Longitude, UpdatedUtc = SYSUTCDATETIME()
    WHERE SalonMasterID = @SalonID;
    EXEC dbo.GetSalonOwnerProfile @OwnerID, @SalonID;
END;
GO
