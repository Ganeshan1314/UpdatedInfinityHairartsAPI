IF OBJECT_ID(N'dbo.CustomerPushDevice', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerPushDevice
    (
        CustomerPushDeviceID uniqueidentifier NOT NULL
            CONSTRAINT PK_CustomerPushDevice PRIMARY KEY
            CONSTRAINT DF_CustomerPushDevice_ID DEFAULT NEWID(),
        CustomerID uniqueidentifier NOT NULL,
        DeviceToken nvarchar(512) NOT NULL,
        Platform varchar(20) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_CustomerPushDevice_IsActive DEFAULT (1),
        CreatedUtc datetime2(0) NOT NULL CONSTRAINT DF_CustomerPushDevice_CreatedUtc DEFAULT SYSUTCDATETIME(),
        LastSeenUtc datetime2(0) NOT NULL CONSTRAINT DF_CustomerPushDevice_LastSeenUtc DEFAULT SYSUTCDATETIME()
    );

    CREATE UNIQUE INDEX UX_CustomerPushDevice_DeviceToken
        ON dbo.CustomerPushDevice(DeviceToken);
    CREATE INDEX IX_CustomerPushDevice_Customer
        ON dbo.CustomerPushDevice(CustomerID, IsActive);
END;

IF OBJECT_ID(N'dbo.BookingReminderLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BookingReminderLog
    (
        BookingReminderLogID uniqueidentifier NOT NULL
            CONSTRAINT PK_BookingReminderLog PRIMARY KEY
            CONSTRAINT DF_BookingReminderLog_ID DEFAULT NEWID(),
        SeatBookingDetailsID uniqueidentifier NOT NULL,
        AppointmentStartLocal datetime2(0) NOT NULL,
        Status varchar(20) NOT NULL,
        AttemptCount int NOT NULL CONSTRAINT DF_BookingReminderLog_AttemptCount DEFAULT (0),
        ClaimExpiresUtc datetime2(0) NULL,
        SentUtc datetime2(0) NULL,
        LastError nvarchar(1000) NULL,
        CreatedUtc datetime2(0) NOT NULL CONSTRAINT DF_BookingReminderLog_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(0) NOT NULL CONSTRAINT DF_BookingReminderLog_UpdatedUtc DEFAULT SYSUTCDATETIME()
    );

    CREATE UNIQUE INDEX UX_BookingReminderLog_BookingStart
        ON dbo.BookingReminderLog(SeatBookingDetailsID, AppointmentStartLocal);
END;
GO

IF TYPE_ID(N'dbo.DeviceTokenList') IS NULL
BEGIN
    EXEC(N'CREATE TYPE dbo.DeviceTokenList AS TABLE
    (
        DeviceToken NVARCHAR(512) NOT NULL
    );');
END;
GO

CREATE OR ALTER PROCEDURE dbo.ensurePushNotificationSchema
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID(N'dbo.CustomerPushDevice', N'U') IS NULL
       OR OBJECT_ID(N'dbo.BookingReminderLog', N'U') IS NULL
       OR TYPE_ID(N'dbo.DeviceTokenList') IS NULL
    BEGIN
        THROW 50001, 'Push-notification database objects are not installed. Run PushNotificationSchema.sql.', 1;
    END;
END;
GO

CREATE OR ALTER PROCEDURE dbo.registerCustomerPushDevice
    @CustomerID UNIQUEIDENTIFIER,
    @DeviceToken NVARCHAR(512),
    @Platform VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;

    MERGE dbo.CustomerPushDevice WITH (HOLDLOCK) AS Target
    USING (SELECT @DeviceToken AS DeviceToken) AS Source
        ON Target.DeviceToken = Source.DeviceToken
    WHEN MATCHED THEN
        UPDATE SET
            CustomerID = @CustomerID,
            Platform = @Platform,
            IsActive = 1,
            LastSeenUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (CustomerID, DeviceToken, Platform)
        VALUES (@CustomerID, @DeviceToken, @Platform);
END;
GO

CREATE OR ALTER PROCEDURE dbo.getDueBookingReminders
    @NowLocal DATETIME2(0),
    @ReminderCutoffLocal DATETIME2(0),
    @NowUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;

    WITH AppointmentTimes AS
    (
        SELECT
            SB.SeatBookingDetailsID,
            SB.CustomerID,
            SB.SeatCount,
            DATEADD(
                SECOND,
                DATEDIFF(SECOND, CAST('00:00:00' AS TIME), MIN(TA.FullTiming)),
                CAST(SB.BookingDate AS DATETIME2(0))) AS AppointmentStartLocal
        FROM dbo.SeatBookingDetails SB
        INNER JOIN dbo.CustomerTimeSelection CTS
            ON CTS.SeatBookingDetailsID = SB.SeatBookingDetailsID
        INNER JOIN dbo.TimeAllocation TA
            ON TA.TimeAllocationID = CTS.TimeAllocationID
        WHERE SB.BookingStatusMasterID = 2
          AND ISNULL(TA.IsDeleted, 0) = 0
        GROUP BY
            SB.SeatBookingDetailsID,
            SB.CustomerID,
            SB.SeatCount,
            SB.BookingDate
    )
    SELECT TOP (100)
        A.SeatBookingDetailsID,
        A.CustomerID,
        A.AppointmentStartLocal,
        A.SeatCount
    FROM AppointmentTimes A
    WHERE A.AppointmentStartLocal > @NowLocal
      AND A.AppointmentStartLocal <= @ReminderCutoffLocal
      AND EXISTS
      (
          SELECT 1
          FROM dbo.CustomerPushDevice D
          WHERE D.CustomerID = A.CustomerID
            AND D.IsActive = 1
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.BookingReminderLog L
          WHERE L.SeatBookingDetailsID = A.SeatBookingDetailsID
            AND L.AppointmentStartLocal = A.AppointmentStartLocal
            AND
            (
                L.Status = 'Sent'
                OR L.AttemptCount >= 5
                OR (L.Status = 'Processing' AND L.ClaimExpiresUtc > @NowUtc)
            )
      )
    ORDER BY A.AppointmentStartLocal;
END;
GO

CREATE OR ALTER PROCEDURE dbo.tryClaimBookingReminder
    @SeatBookingDetailsID UNIQUEIDENTIFIER,
    @AppointmentStartLocal DATETIME2(0),
    @NowUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

    BEGIN TRANSACTION;

    BEGIN TRY
        DECLARE @Claimed BIT = 0;

        IF NOT EXISTS
        (
            SELECT 1
            FROM dbo.BookingReminderLog WITH (UPDLOCK, HOLDLOCK)
            WHERE SeatBookingDetailsID = @SeatBookingDetailsID
              AND AppointmentStartLocal = @AppointmentStartLocal
        )
        BEGIN
            INSERT dbo.BookingReminderLog
                (SeatBookingDetailsID, AppointmentStartLocal, Status, AttemptCount, ClaimExpiresUtc)
            VALUES
                (@SeatBookingDetailsID, @AppointmentStartLocal, 'Processing', 1, DATEADD(MINUTE, 5, @NowUtc));
            SET @Claimed = 1;
        END
        ELSE
        BEGIN
            UPDATE dbo.BookingReminderLog
            SET
                Status = 'Processing',
                AttemptCount = AttemptCount + 1,
                ClaimExpiresUtc = DATEADD(MINUTE, 5, @NowUtc),
                LastError = NULL,
                UpdatedUtc = @NowUtc
            WHERE SeatBookingDetailsID = @SeatBookingDetailsID
              AND AppointmentStartLocal = @AppointmentStartLocal
              AND AttemptCount < 5
              AND
              (
                  Status = 'Failed'
                  OR (Status = 'Processing' AND ClaimExpiresUtc <= @NowUtc)
              );

            IF @@ROWCOUNT > 0
                SET @Claimed = 1;
        END;

        COMMIT TRANSACTION;
        SELECT CAST(@Claimed AS INT);
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE OR ALTER PROCEDURE dbo.getActiveCustomerPushTokens
    @CustomerID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DeviceToken
    FROM dbo.CustomerPushDevice
    WHERE CustomerID = @CustomerID
      AND IsActive = 1;
END;
GO

CREATE OR ALTER PROCEDURE dbo.markBookingReminderSent
    @SeatBookingDetailsID UNIQUEIDENTIFIER,
    @AppointmentStartLocal DATETIME2(0),
    @NowUtc DATETIME2(0),
    @Error NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.BookingReminderLog
    SET
        Status = 'Sent',
        SentUtc = @NowUtc,
        ClaimExpiresUtc = NULL,
        UpdatedUtc = @NowUtc
    WHERE SeatBookingDetailsID = @SeatBookingDetailsID
      AND AppointmentStartLocal = @AppointmentStartLocal;
END;
GO

CREATE OR ALTER PROCEDURE dbo.markBookingReminderFailed
    @SeatBookingDetailsID UNIQUEIDENTIFIER,
    @AppointmentStartLocal DATETIME2(0),
    @NowUtc DATETIME2(0),
    @Error NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.BookingReminderLog
    SET
        Status = 'Failed',
        LastError = @Error,
        ClaimExpiresUtc = NULL,
        UpdatedUtc = @NowUtc
    WHERE SeatBookingDetailsID = @SeatBookingDetailsID
      AND AppointmentStartLocal = @AppointmentStartLocal;
END;
GO

CREATE OR ALTER PROCEDURE dbo.deactivateCustomerPushDevices
    @Tokens dbo.DeviceTokenList READONLY
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE D
    SET
        D.IsActive = 0,
        D.LastSeenUtc = SYSUTCDATETIME()
    FROM dbo.CustomerPushDevice D
    INNER JOIN @Tokens T
        ON T.DeviceToken = D.DeviceToken;
END;
GO
