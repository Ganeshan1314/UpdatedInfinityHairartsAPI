-- Separate application-owner accounts. No default accounts or plaintext passwords.
-- Safe to rerun; existing accounts and salon-owner data are preserved.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.SuperAdmin', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SuperAdmin
    (
        SuperAdminID uniqueidentifier NOT NULL
            CONSTRAINT PK_SuperAdmin PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        UserName nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        DisplayName nvarchar(150) NOT NULL,
        PasswordHash nvarchar(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Role varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT DF_SuperAdmin_Role DEFAULT ('SuperAdmin'),
        IsActive bit NOT NULL CONSTRAINT DF_SuperAdmin_IsActive DEFAULT (1),
        CreatedUtc datetime2(0) NOT NULL CONSTRAINT DF_SuperAdmin_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(0) NULL,
        LastLoginUtc datetime2(0) NULL,
        CONSTRAINT UQ_SuperAdmin_UserName UNIQUE (UserName),
        CONSTRAINT CK_SuperAdmin_UserName CHECK (LEN(UserName) >= 3
            AND UserName NOT LIKE N'%[^-a-zA-Z0-9._]%'
            AND DATALENGTH(UserName) = DATALENGTH(LTRIM(RTRIM(UserName)))),
        CONSTRAINT CK_SuperAdmin_DisplayName CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0),
        CONSTRAINT CK_SuperAdmin_PasswordHash CHECK (LEN(PasswordHash) > 0),
        CONSTRAINT CK_SuperAdmin_Role CHECK (Role = 'SuperAdmin' AND DATALENGTH(Role) = 10)
    );
END;

COMMIT TRANSACTION;
