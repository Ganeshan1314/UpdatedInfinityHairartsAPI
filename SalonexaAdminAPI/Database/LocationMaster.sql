SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.LocationMaster', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LocationMaster
    (
        LocationMasterID UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT PK_LocationMaster PRIMARY KEY
            CONSTRAINT DF_LocationMaster_LocationMasterID DEFAULT NEWSEQUENTIALID(),
        LocationName NVARCHAR(150) NOT NULL,
        IsActive BIT NOT NULL
            CONSTRAINT DF_LocationMaster_IsActive DEFAULT (1),
        DisplayOrder INT NOT NULL
            CONSTRAINT DF_LocationMaster_DisplayOrder DEFAULT (0),
        CreatedUtc DATETIME2(0) NOT NULL
            CONSTRAINT DF_LocationMaster_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc DATETIME2(0) NULL
    );

    CREATE UNIQUE INDEX UX_LocationMaster_LocationName
        ON dbo.LocationMaster (LocationName);
END;

IF EXISTS
(
    SELECT 1
    FROM dbo.LocationMaster
    WHERE LocationName = N'Rasampalayam'
)
BEGIN
    UPDATE dbo.LocationMaster
    SET IsActive = 1,
        DisplayOrder = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE LocationName = N'Rasampalayam';
END
ELSE
BEGIN
    INSERT INTO dbo.LocationMaster (LocationName, IsActive, DisplayOrder)
    VALUES (N'Rasampalayam', 1, 1);
END;

COMMIT TRANSACTION;
GO

IF OBJECT_ID(N'dbo.getActiveLocations', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE dbo.getActiveLocations AS BEGIN SET NOCOUNT ON; END');
END;
GO

ALTER PROCEDURE dbo.getActiveLocations
AS
BEGIN
    SET NOCOUNT ON;

    SELECT LocationMasterID, LocationName
    FROM dbo.LocationMaster
    WHERE IsActive = 1
    ORDER BY DisplayOrder, LocationName;
END;
GO
