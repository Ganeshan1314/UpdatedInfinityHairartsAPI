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
