SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL
BEGIN
    THROW 50000, 'Run Database/SalonMaster.sql before SalonOwnerAuthentication.sql.', 1;
END;
GO

IF OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalonOwner
    (
        SalonOwnerID UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT PK_SalonOwner PRIMARY KEY,
        SalonMasterID UNIQUEIDENTIFIER NOT NULL,
        OwnerName NVARCHAR(150) NOT NULL,
        UserName NVARCHAR(100) NOT NULL,
        PasswordHash NVARCHAR(512) NOT NULL,
        EmailAddress NVARCHAR(254) NULL,
        IsActive BIT NOT NULL
            CONSTRAINT DF_SalonOwner_IsActive DEFAULT (1),
        LastLoginUtc DATETIME2(0) NULL,
        CreatedUtc DATETIME2(0) NOT NULL
            CONSTRAINT DF_SalonOwner_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc DATETIME2(0) NULL,
        CONSTRAINT FK_SalonOwner_SalonMaster
            FOREIGN KEY (SalonMasterID)
            REFERENCES dbo.SalonMaster (SalonMasterID)
    );

    CREATE UNIQUE INDEX UX_SalonOwner_UserName
        ON dbo.SalonOwner (UserName);

    CREATE INDEX IX_SalonOwner_SalonMaster_IsActive
        ON dbo.SalonOwner (SalonMasterID, IsActive);
END;
GO

IF OBJECT_ID(N'dbo.getSalonOwnerCredentialByUsername', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE dbo.getSalonOwnerCredentialByUsername AS BEGIN SET NOCOUNT ON; END');
END;
GO

ALTER PROCEDURE dbo.getSalonOwnerCredentialByUsername
    @UserName NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        SO.SalonOwnerID,
        SO.SalonMasterID,
        SO.OwnerName,
        SO.UserName,
        SO.PasswordHash,
        SM.SalonName
    FROM dbo.SalonOwner SO
    INNER JOIN dbo.SalonMaster SM
        ON SM.SalonMasterID = SO.SalonMasterID
    WHERE SO.UserName = @UserName
      AND SO.IsActive = 1
      AND SM.IsActive = 1;
END;
GO

IF OBJECT_ID(N'dbo.getActiveSalonOwnerById', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE dbo.getActiveSalonOwnerById AS BEGIN SET NOCOUNT ON; END');
END;
GO

ALTER PROCEDURE dbo.getActiveSalonOwnerById
    @SalonOwnerID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        SO.SalonOwnerID,
        SO.SalonMasterID,
        SO.OwnerName,
        SO.UserName,
        SO.PasswordHash,
        SM.SalonName
    FROM dbo.SalonOwner SO
    INNER JOIN dbo.SalonMaster SM
        ON SM.SalonMasterID = SO.SalonMasterID
    WHERE SO.SalonOwnerID = @SalonOwnerID
      AND SO.IsActive = 1
      AND SM.IsActive = 1;
END;
GO

IF OBJECT_ID(N'dbo.markSalonOwnerLoginSucceeded', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE dbo.markSalonOwnerLoginSucceeded AS BEGIN SET NOCOUNT ON; END');
END;
GO

ALTER PROCEDURE dbo.markSalonOwnerLoginSucceeded
    @SalonOwnerID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.SalonOwner
    SET LastLoginUtc = SYSUTCDATETIME(),
        UpdatedUtc = SYSUTCDATETIME()
    WHERE SalonOwnerID = @SalonOwnerID;
END;
GO

IF OBJECT_ID(N'dbo.initializeSalonOwner', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE dbo.initializeSalonOwner AS BEGIN SET NOCOUNT ON; END');
END;
GO

ALTER PROCEDURE dbo.initializeSalonOwner
    @SalonOwnerID UNIQUEIDENTIFIER,
    @SalonMasterID UNIQUEIDENTIFIER,
    @OwnerName NVARCHAR(150),
    @UserName NVARCHAR(100),
    @PasswordHash NVARCHAR(512),
    @EmailAddress NVARCHAR(254) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF EXISTS (SELECT 1 FROM dbo.SalonOwner WITH (UPDLOCK, HOLDLOCK))
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50001, 'The first salon owner has already been initialized.', 1;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.SalonMaster WITH (UPDLOCK, HOLDLOCK)
        WHERE SalonMasterID = @SalonMasterID
          AND IsActive = 1
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50002, 'The selected salon does not exist or is inactive.', 1;
    END;

    INSERT INTO dbo.SalonOwner
    (
        SalonOwnerID,
        SalonMasterID,
        OwnerName,
        UserName,
        PasswordHash,
        EmailAddress
    )
    VALUES
    (
        @SalonOwnerID,
        @SalonMasterID,
        @OwnerName,
        @UserName,
        @PasswordHash,
        @EmailAddress
    );

    COMMIT TRANSACTION;

    SELECT
        SO.SalonOwnerID,
        SO.SalonMasterID,
        SO.OwnerName,
        SO.UserName,
        SO.PasswordHash,
        SM.SalonName
    FROM dbo.SalonOwner SO
    INNER JOIN dbo.SalonMaster SM
        ON SM.SalonMasterID = SO.SalonMasterID
    WHERE SO.SalonOwnerID = @SalonOwnerID;
END;
GO
