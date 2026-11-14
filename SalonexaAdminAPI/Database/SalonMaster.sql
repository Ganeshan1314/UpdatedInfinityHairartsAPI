SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalonMaster
    (
        SalonMasterID UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT PK_SalonMaster PRIMARY KEY
            CONSTRAINT DF_SalonMaster_SalonMasterID DEFAULT NEWSEQUENTIALID(),
        LocationMasterID UNIQUEIDENTIFIER NOT NULL,
        SalonName NVARCHAR(200) NOT NULL,
        SalonAddress NVARCHAR(500) NULL,
        ContactNumber NVARCHAR(20) NULL,
        IsActive BIT NOT NULL
            CONSTRAINT DF_SalonMaster_IsActive DEFAULT (1),
        DisplayOrder INT NOT NULL
            CONSTRAINT DF_SalonMaster_DisplayOrder DEFAULT (0),
        CreatedUtc DATETIME2(0) NOT NULL
            CONSTRAINT DF_SalonMaster_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc DATETIME2(0) NULL,
        CONSTRAINT FK_SalonMaster_LocationMaster
            FOREIGN KEY (LocationMasterID)
            REFERENCES dbo.LocationMaster (LocationMasterID)
    );

    CREATE UNIQUE INDEX UX_SalonMaster_Location_SalonName
        ON dbo.SalonMaster (LocationMasterID, SalonName);

    CREATE INDEX IX_SalonMaster_Location_IsActive
        ON dbo.SalonMaster (LocationMasterID, IsActive, DisplayOrder);
END;

DECLARE @RasampalayamLocationID UNIQUEIDENTIFIER =
(
    SELECT TOP (1) LocationMasterID
    FROM dbo.LocationMaster
    WHERE LocationName = N'Rasampalayam'
);

IF @RasampalayamLocationID IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM dbo.SalonMaster
       WHERE LocationMasterID = @RasampalayamLocationID
         AND SalonName = N'Infinity Hair Arts'
   )
BEGIN
    INSERT INTO dbo.SalonMaster
    (
        LocationMasterID,
        SalonName,
        IsActive,
        DisplayOrder
    )
    VALUES
    (
        @RasampalayamLocationID,
        N'Infinity Hair Arts',
        1,
        1
    );
END;

COMMIT TRANSACTION;
GO

IF OBJECT_ID(N'dbo.getActiveSalonsByLocation', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE dbo.getActiveSalonsByLocation AS BEGIN SET NOCOUNT ON; END');
END;
GO

ALTER PROCEDURE dbo.getActiveSalonsByLocation
    @LocationMasterID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        SalonMasterID,
        LocationMasterID,
        SalonName,
        COALESCE(SalonAddress, '') AS SalonAddress,
        COALESCE(ContactNumber, '') AS ContactNumber
    FROM dbo.SalonMaster
    WHERE LocationMasterID = @LocationMasterID
      AND IsActive = 1
    ORDER BY DisplayOrder, SalonName;
END;
GO
