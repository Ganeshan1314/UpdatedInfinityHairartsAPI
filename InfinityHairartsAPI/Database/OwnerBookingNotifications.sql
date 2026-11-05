-- Run on the API database before deploying the booking-notification backend.
-- Rerunnable. Existing bookings are not backfilled and no external messages are sent.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL OR COL_LENGTH(N'dbo.SeatBookingDetails', N'SalonMasterID') IS NULL
    THROW 50060, 'Deploy salon-owner authentication and salon-scoped bookings first.', 1;
GO
IF OBJECT_ID(N'dbo.OwnerBookingNotification', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OwnerBookingNotification (
        NotificationID bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SeatBookingDetailsID uniqueidentifier NOT NULL UNIQUE,
        SalonMasterID uniqueidentifier NOT NULL,
        CustomerName nvarchar(300) NOT NULL,
        BookingDate date NOT NULL,
        StartTime time(0) NULL,
        SeatCount int NOT NULL,
        CreatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_OwnerBookingNotification_Salon ON dbo.OwnerBookingNotification(SalonMasterID, NotificationID DESC);
END;
IF OBJECT_ID(N'dbo.OwnerBookingNotificationRead', N'U') IS NULL
    CREATE TABLE dbo.OwnerBookingNotificationRead (
        SalonOwnerID uniqueidentifier NOT NULL, NotificationID bigint NOT NULL,
        ReadUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(), PRIMARY KEY(SalonOwnerID, NotificationID)
    );
IF OBJECT_ID(N'dbo.OwnerPushDevice', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OwnerPushDevice (
        OwnerPushDeviceID uniqueidentifier NOT NULL PRIMARY KEY DEFAULT NEWID(),
        SalonOwnerID uniqueidentifier NOT NULL,
        DeviceToken nvarchar(512) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE,
        BindingID uniqueidentifier NOT NULL DEFAULT NEWID(),
        IsActive bit NOT NULL DEFAULT 1,
        CreatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
        LastSeenUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_OwnerPushDevice_Owner ON dbo.OwnerPushDevice(SalonOwnerID, IsActive);
END;
IF OBJECT_ID(N'dbo.OwnerBookingPushDelivery', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OwnerBookingPushDelivery (
        DeliveryID bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        NotificationID bigint NOT NULL,
        OwnerPushDeviceID uniqueidentifier NOT NULL,
        BindingID uniqueidentifier NOT NULL,
        Status varchar(12) NOT NULL DEFAULT 'Pending',
        AttemptCount int NOT NULL DEFAULT 0,
        NextAttemptUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
        LeaseID uniqueidentifier NULL,
        LeaseUntilUtc datetime2(0) NULL,
        SentUtc datetime2(0) NULL,
        LastError nvarchar(1000) NULL,
        CreatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
        UNIQUE(NotificationID, OwnerPushDeviceID, BindingID)
    );
    CREATE INDEX IX_OwnerBookingPushDelivery_Pending ON dbo.OwnerBookingPushDelivery(Status, NextAttemptUtc);
END;
GO
IF OBJECT_ID(N'dbo.TR_SeatBookingDetails_OwnerNotification', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER dbo.TR_SeatBookingDetails_OwnerNotification ON dbo.SeatBookingDetails AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END');
GO
ALTER TRIGGER dbo.TR_SeatBookingDetails_OwnerNotification
ON dbo.SeatBookingDetails AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.SeatBookingDetailsID = i.SeatBookingDetailsID
        WHERE i.BookingStatusMasterID = 2 AND ISNULL(d.BookingStatusMasterID, 0) <> 2) RETURN;
    DECLARE @New TABLE(NotificationID bigint, SalonMasterID uniqueidentifier);
    INSERT dbo.OwnerBookingNotification(SeatBookingDetailsID, SalonMasterID, CustomerName, BookingDate, StartTime, SeatCount)
    OUTPUT inserted.NotificationID, inserted.SalonMasterID INTO @New
    SELECT i.SeatBookingDetailsID, i.SalonMasterID,
        LEFT(COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(c.FirstName, N' ', c.LastName))), N''), N'Customer'), 300),
        CONVERT(date, i.BookingDate), times.StartTime, COALESCE(i.SeatCount, 1)
    FROM inserted i LEFT JOIN deleted d ON d.SeatBookingDetailsID = i.SeatBookingDetailsID
    LEFT JOIN dbo.CustomerRegistration c ON c.CustomerID = i.CustomerID
    OUTER APPLY (SELECT MIN(CONVERT(time(0), t.FullTiming)) StartTime FROM dbo.CustomerTimeSelection cs
        INNER JOIN dbo.TimeAllocation t ON t.TimeAllocationID = cs.TimeAllocationID
        WHERE cs.SeatBookingDetailsID = i.SeatBookingDetailsID AND cs.CustomerID = i.CustomerID) times
    WHERE i.BookingStatusMasterID = 2 AND ISNULL(d.BookingStatusMasterID, 0) <> 2 AND i.SalonMasterID IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM dbo.OwnerBookingNotification n WITH(UPDLOCK, HOLDLOCK)
            WHERE n.SeatBookingDetailsID = i.SeatBookingDetailsID);
    -- The inbox and per-device outbox commit or roll back with the booking itself.
    INSERT dbo.OwnerBookingPushDelivery(NotificationID, OwnerPushDeviceID, BindingID)
    SELECT n.NotificationID, p.OwnerPushDeviceID, p.BindingID FROM @New n
    INNER JOIN dbo.SalonOwner o ON o.SalonMasterID = n.SalonMasterID AND o.IsActive = 1
    INNER JOIN dbo.SalonMaster s ON s.SalonMasterID = n.SalonMasterID AND s.IsActive = 1
    INNER JOIN dbo.OwnerPushDevice p ON p.SalonOwnerID = o.SalonOwnerID AND p.IsActive = 1;
END;
GO
IF OBJECT_ID(N'dbo.RegisterOwnerPushDevice', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.RegisterOwnerPushDevice AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.RegisterOwnerPushDevice @OwnerID uniqueidentifier, @Token nvarchar(512)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.IsActive = 1 AND s.IsActive = 1)
        THROW 50061, 'The salon-owner account is inactive.', 1;
    BEGIN TRANSACTION;
    DECLARE @DeviceID uniqueidentifier, @PreviousOwner uniqueidentifier, @Active bit;
    SELECT @DeviceID = OwnerPushDeviceID, @PreviousOwner = SalonOwnerID, @Active = IsActive
        FROM dbo.OwnerPushDevice WITH(UPDLOCK, HOLDLOCK) WHERE DeviceToken = @Token;
    IF @DeviceID IS NULL
        INSERT dbo.OwnerPushDevice(SalonOwnerID, DeviceToken) VALUES(@OwnerID, @Token);
    ELSE
    BEGIN
        UPDATE dbo.OwnerPushDevice SET SalonOwnerID = @OwnerID, IsActive = 1, LastSeenUtc = SYSUTCDATETIME(),
            BindingID = CASE WHEN @PreviousOwner <> @OwnerID OR @Active = 0 THEN NEWID() ELSE BindingID END
        WHERE OwnerPushDeviceID = @DeviceID;
        UPDATE q SET Status = 'Cancelled', LeaseUntilUtc = NULL FROM dbo.OwnerBookingPushDelivery q
        JOIN dbo.OwnerPushDevice p ON p.OwnerPushDeviceID = q.OwnerPushDeviceID
        WHERE q.OwnerPushDeviceID = @DeviceID AND q.BindingID <> p.BindingID AND q.Status IN ('Pending', 'Sending');
    END;
    COMMIT;
END;
GO
IF OBJECT_ID(N'dbo.UnregisterOwnerPushDevice', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.UnregisterOwnerPushDevice AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.UnregisterOwnerPushDevice @OwnerID uniqueidentifier, @Token nvarchar(512)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;
    UPDATE dbo.OwnerPushDevice SET IsActive = 0, BindingID = NEWID() WHERE SalonOwnerID = @OwnerID AND DeviceToken = @Token;
    UPDATE q SET Status = 'Cancelled', LeaseUntilUtc = NULL FROM dbo.OwnerBookingPushDelivery q
    JOIN dbo.OwnerPushDevice p ON p.OwnerPushDeviceID = q.OwnerPushDeviceID
    WHERE p.SalonOwnerID = @OwnerID AND p.DeviceToken = @Token AND q.Status IN ('Pending', 'Sending');
    COMMIT;
END;
GO
IF OBJECT_ID(N'dbo.GetOwnerBookingNotifications', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetOwnerBookingNotifications AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetOwnerBookingNotifications @OwnerID uniqueidentifier, @SalonID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP(50) n.NotificationID, n.SeatBookingDetailsID, n.SalonMasterID, n.CustomerName,
        n.BookingDate, n.StartTime, n.SeatCount, n.CreatedUtc, CONVERT(bit, CASE WHEN r.NotificationID IS NULL THEN 0 ELSE 1 END) IsRead
    FROM dbo.OwnerBookingNotification n JOIN dbo.SalonOwner o ON o.SalonMasterID = n.SalonMasterID
    JOIN dbo.SalonMaster s ON s.SalonMasterID = n.SalonMasterID
    LEFT JOIN dbo.OwnerBookingNotificationRead r ON r.NotificationID = n.NotificationID AND r.SalonOwnerID = @OwnerID
    WHERE o.SalonOwnerID = @OwnerID AND n.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1
    ORDER BY n.NotificationID DESC;
    SELECT COUNT(*) FROM dbo.OwnerBookingNotification n JOIN dbo.SalonOwner o ON o.SalonMasterID = n.SalonMasterID
    JOIN dbo.SalonMaster s ON s.SalonMasterID = n.SalonMasterID
    WHERE o.SalonOwnerID = @OwnerID AND n.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1
        AND NOT EXISTS (SELECT 1 FROM dbo.OwnerBookingNotificationRead r WHERE r.NotificationID = n.NotificationID AND r.SalonOwnerID = @OwnerID);
END;
GO
IF OBJECT_ID(N'dbo.ReadOwnerBookingNotifications', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.ReadOwnerBookingNotifications AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.ReadOwnerBookingNotifications @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @ThroughID bigint
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;
    INSERT dbo.OwnerBookingNotificationRead(SalonOwnerID, NotificationID)
    SELECT @OwnerID, n.NotificationID FROM dbo.OwnerBookingNotification n
    JOIN dbo.SalonOwner o ON o.SalonMasterID = n.SalonMasterID AND o.SalonOwnerID = @OwnerID AND o.IsActive = 1
    JOIN dbo.SalonMaster s ON s.SalonMasterID = n.SalonMasterID AND s.IsActive = 1
    WHERE n.SalonMasterID = @SalonID AND n.NotificationID <= @ThroughID
        AND NOT EXISTS (SELECT 1 FROM dbo.OwnerBookingNotificationRead r WITH(UPDLOCK, HOLDLOCK)
            WHERE r.SalonOwnerID = @OwnerID AND r.NotificationID = n.NotificationID);
    COMMIT;
END;
GO
IF OBJECT_ID(N'dbo.ClaimOwnerBookingPush', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.ClaimOwnerBookingPush AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.ClaimOwnerBookingPush @LeaseID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @Now datetime2(0) = SYSUTCDATETIME();
    -- Retired devices and reassigned salons must never receive queued customer details.
    UPDATE q SET Status = 'Cancelled' FROM dbo.OwnerBookingPushDelivery q
    JOIN dbo.OwnerBookingNotification n ON n.NotificationID = q.NotificationID
    LEFT JOIN dbo.OwnerPushDevice p ON p.OwnerPushDeviceID = q.OwnerPushDeviceID
    LEFT JOIN dbo.SalonOwner o ON o.SalonOwnerID = p.SalonOwnerID
    LEFT JOIN dbo.SalonMaster s ON s.SalonMasterID = n.SalonMasterID
    WHERE q.Status IN ('Pending', 'Sending') AND (p.OwnerPushDeviceID IS NULL OR p.IsActive = 0 OR p.BindingID <> q.BindingID
        OR o.SalonOwnerID IS NULL OR o.IsActive = 0 OR o.SalonMasterID <> n.SalonMasterID OR ISNULL(s.IsActive, 0) = 0);
    UPDATE dbo.OwnerBookingPushDelivery SET Status = 'Failed'
        WHERE Status IN ('Pending', 'Sending') AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc <= @Now)
            AND (AttemptCount >= 8 OR CreatedUtc < DATEADD(hour, -24, @Now));
    DECLARE @Claimed TABLE(DeliveryID bigint);
    ;WITH Due AS (
        SELECT TOP(20) * FROM dbo.OwnerBookingPushDelivery WITH(UPDLOCK, READPAST, ROWLOCK)
        WHERE (Status = 'Pending' OR (Status = 'Sending' AND LeaseUntilUtc <= @Now))
            AND NextAttemptUtc <= @Now AND AttemptCount < 8 AND CreatedUtc >= DATEADD(hour, -24, @Now)
        ORDER BY DeliveryID
    )
    UPDATE Due SET Status = 'Sending', LeaseID = @LeaseID, LeaseUntilUtc = DATEADD(minute, 2, @Now), AttemptCount = AttemptCount + 1
        OUTPUT inserted.DeliveryID INTO @Claimed;
    SELECT q.DeliveryID, q.LeaseID, q.BindingID, q.OwnerPushDeviceID, n.NotificationID, n.SeatBookingDetailsID,
        n.SalonMasterID, n.CustomerName, n.BookingDate, n.StartTime, n.SeatCount, p.DeviceToken
    FROM @Claimed c JOIN dbo.OwnerBookingPushDelivery q ON q.DeliveryID = c.DeliveryID
    JOIN dbo.OwnerBookingNotification n ON n.NotificationID = q.NotificationID
    JOIN dbo.OwnerPushDevice p ON p.OwnerPushDeviceID = q.OwnerPushDeviceID AND p.BindingID = q.BindingID AND p.IsActive = 1
    JOIN dbo.SalonOwner o ON o.SalonOwnerID = p.SalonOwnerID AND o.SalonMasterID = n.SalonMasterID AND o.IsActive = 1
    JOIN dbo.SalonMaster s ON s.SalonMasterID = n.SalonMasterID AND s.IsActive = 1;
END;
GO
IF OBJECT_ID(N'dbo.FinishOwnerBookingPush', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.FinishOwnerBookingPush AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.FinishOwnerBookingPush @DeliveryID bigint, @LeaseID uniqueidentifier,
    @Success bit, @InvalidToken bit, @Error nvarchar(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;
    DECLARE @DeviceID uniqueidentifier, @BindingID uniqueidentifier;
    SELECT @DeviceID = OwnerPushDeviceID, @BindingID = BindingID FROM dbo.OwnerBookingPushDelivery WITH(UPDLOCK, HOLDLOCK)
        WHERE DeliveryID = @DeliveryID AND Status = 'Sending' AND LeaseID = @LeaseID;
    IF @DeviceID IS NOT NULL
    BEGIN
        UPDATE dbo.OwnerBookingPushDelivery SET Status = CASE WHEN @Success = 1 THEN 'Sent'
            WHEN @InvalidToken = 1 OR AttemptCount >= 8 THEN 'Failed' ELSE 'Pending' END,
            SentUtc = CASE WHEN @Success = 1 THEN SYSUTCDATETIME() ELSE NULL END,
            NextAttemptUtc = DATEADD(second, CONVERT(int, POWER(2, AttemptCount)) * 5, SYSUTCDATETIME()),
            LeaseUntilUtc = NULL, LastError = LEFT(@Error, 1000)
        WHERE DeliveryID = @DeliveryID;
        IF @InvalidToken = 1
            UPDATE dbo.OwnerPushDevice SET IsActive = 0 WHERE OwnerPushDeviceID = @DeviceID AND BindingID = @BindingID;
    END;
    COMMIT;
END;
GO
