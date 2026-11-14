-- Salon owner signup (2026-11-14). Back up the database first. Re-runnable.
-- Defines dbo.GetSalonOwnerSignupLocations and dbo.RegisterSalonOwner exactly as installed on the
-- existing database, so a new server can be built from source control.
-- Needs the salon, location and salon-owner schemas (SalonMaster.sql, LocationMaster.sql,
-- SalonOwnerAuthentication.sql) and the salon columns used by SalonOwnerDashboard.sql.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL OR OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL OR OBJECT_ID(N'dbo.LocationMaster', N'U') IS NULL
    THROW 50003, 'Install the salon, location and salon-owner schemas before SalonOwnerSignup.sql.', 1;
GO
IF COL_LENGTH(N'dbo.SalonMaster', N'IsVisibleToCustomers') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'City') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Pincode') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Latitude') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Longitude') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'ImagePath') IS NULL
    THROW 50003, 'SalonMaster is missing the City, Pincode, map, photo or visibility columns.', 1;
GO
IF OBJECT_ID(N'dbo.GetSalonOwnerSignupLocations', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerSignupLocations AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerSignupLocations
AS
BEGIN
    SET NOCOUNT ON;

    SELECT LocationMasterID, LocationName
    FROM dbo.LocationMaster
    WHERE IsActive = 1
    ORDER BY DisplayOrder, LocationName;
END;
GO
IF OBJECT_ID(N'dbo.RegisterSalonOwner', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.RegisterSalonOwner AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.RegisterSalonOwner
    @SalonOwnerID uniqueidentifier,
    @SalonMasterID uniqueidentifier,
    @OwnerName nvarchar(150),
    @UserName nvarchar(100),
    @PasswordHash nvarchar(512),
    @SalonName nvarchar(200),
    @LocationMasterID uniqueidentifier,
    @EmailAddress nvarchar(254),
    @ContactNumber nvarchar(20),
    @SalonAddress nvarchar(500) = NULL,
    @City nvarchar(150) = NULL,
    @Pincode nvarchar(20) = NULL,
    @Latitude float = NULL,
    @Longitude float = NULL,
    @ImagePath nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Reject malformed postal details before opening a transaction or creating a city.
    IF @City IS NOT NULL
    BEGIN
        SET @City = LTRIM(RTRIM(@City));
        IF @City = N'' OR @Pincode IS NULL OR LEN(@Pincode) <> 6
            OR @Pincode LIKE N'%[^0-9]%' OR LEFT(@Pincode, 1) = N'0'
        BEGIN
            SELECT 1 AS Result; -- InvalidDetails
            RETURN;
        END;
    END;

    -- Result codes match the SignupResult enum in the API.
    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS (SELECT 1 FROM dbo.SalonOwner WITH (UPDLOCK, HOLDLOCK)
                   WHERE UserName = @UserName)
        BEGIN
            ROLLBACK TRANSACTION;
            SELECT 3 AS Result; -- DuplicateUserName
            RETURN;
        END;

        -- Names are reserved across every city, regardless of approval or active status.
        -- Check before resolving a city so rejected registrations create no city records.
        SET @SalonName = LTRIM(RTRIM(@SalonName));
        IF EXISTS (SELECT 1 FROM dbo.SalonMaster WITH (UPDLOCK, HOLDLOCK)
                   WHERE LTRIM(RTRIM(SalonName)) COLLATE Latin1_General_100_CI_AS
                       = @SalonName COLLATE Latin1_General_100_CI_AS)
        BEGIN
            COMMIT TRANSACTION;
            SELECT 4 AS Result; -- DuplicateSalon
            RETURN;
        END;

        IF @City IS NOT NULL
        BEGIN
            DECLARE @CityLocation nvarchar(150) = @City;
            IF LOWER(@City) IN (N'trichy', N'tiruchirappalli', N'tiruchirapalli', N'tiruchchirappalli')
                SET @CityLocation = N'Trichy';
            SET @LocationMasterID = NULL;
            SELECT TOP (1) @LocationMasterID = LocationMasterID
            FROM dbo.LocationMaster WITH (UPDLOCK, HOLDLOCK)
            WHERE LocationName = @CityLocation
                OR (@CityLocation = N'Trichy' AND LOWER(LocationName) IN
                    (N'trichy', N'tiruchirappalli', N'tiruchirapalli', N'tiruchchirappalli'))
            ORDER BY CASE WHEN LocationName = @CityLocation THEN 0 ELSE 1 END, LocationMasterID;
            IF @LocationMasterID IS NULL
            BEGIN
                SET @LocationMasterID = NEWID();
                INSERT dbo.LocationMaster(LocationMasterID, LocationName, IsActive, DisplayOrder, CreatedUtc)
                VALUES(@LocationMasterID, @CityLocation, 1, 0, SYSUTCDATETIME());
            END
            ELSE
                UPDATE dbo.LocationMaster SET IsActive = 1, UpdatedUtc = SYSUTCDATETIME()
                WHERE LocationMasterID = @LocationMasterID AND IsActive = 0;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.LocationMaster WITH (HOLDLOCK)
                       WHERE LocationMasterID = @LocationMasterID AND IsActive = 1)
        BEGIN
            ROLLBACK TRANSACTION;
            SELECT 2 AS Result; -- InvalidLocation
            RETURN;
        END;

        INSERT INTO dbo.SalonMaster
            (SalonMasterID, LocationMasterID, SalonName, SalonAddress, ContactNumber, IsActive, IsVisibleToCustomers, DisplayOrder, CreatedUtc,
             City, Pincode, Latitude, Longitude, ImagePath)
        VALUES
            (@SalonMasterID, @LocationMasterID, @SalonName, @SalonAddress, @ContactNumber, 1, 0, 0, SYSUTCDATETIME(),
             @City, @Pincode, @Latitude, @Longitude, @ImagePath);

        INSERT INTO dbo.SalonOwner
            (SalonOwnerID, SalonMasterID, OwnerName, UserName, PasswordHash, EmailAddress, IsActive, CreatedUtc)
        VALUES
            (@SalonOwnerID, @SalonMasterID, @OwnerName, @UserName, @PasswordHash, @EmailAddress, 1, SYSUTCDATETIME());

        COMMIT TRANSACTION;
        SELECT 0 AS Result; -- Created
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

        IF ERROR_NUMBER() IN (2601, 2627)
            SELECT 5 AS Result; -- DuplicateDetails
        ELSE
            THROW;
    END CATCH;
END;
GO
