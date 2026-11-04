-- Run before deploying the API. No historical bookings are notified.
SET XACT_ABORT ON;
GO
IF OBJECT_ID('dbo.OwnerBookingNotification', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OwnerBookingNotification(
        NotificationID bigint IDENTITY PRIMARY KEY,
        SeatBookingDetailsID uniqueidentifier NOT NULL UNIQUE,
        SalonMasterID uniqueidentifier NOT NULL,
        CustomerName nvarchar(301) NOT NULL,
        BookingDate date NOT NULL,
        StartTime time NULL,
        SeatCount int NOT NULL,
        CreatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
    CREATE INDEX IX_OwnerBookingNotification_Salon ON dbo.OwnerBookingNotification(SalonMasterID, NotificationID DESC);
END;
IF OBJECT_ID('dbo.OwnerNotificationRead', 'U') IS NULL
    CREATE TABLE dbo.OwnerNotificationRead(
        SalonOwnerID uniqueidentifier NOT NULL,
        NotificationID bigint NOT NULL REFERENCES dbo.OwnerBookingNotification(NotificationID),
        ReadUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY(SalonOwnerID, NotificationID));
IF OBJECT_ID('dbo.OwnerPushDevice', 'U') IS NULL
    CREATE TABLE dbo.OwnerPushDevice(
        DeviceID bigint IDENTITY PRIMARY KEY,
        DeviceToken varchar(512) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE,
        SalonOwnerID uniqueidentifier NOT NULL,
        SalonMasterID uniqueidentifier NOT NULL,
        IsActive bit NOT NULL DEFAULT 1,
        Generation int NOT NULL DEFAULT 1,
        UpdatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.OwnerBookingPushDelivery', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OwnerBookingPushDelivery(
        DeliveryID bigint IDENTITY PRIMARY KEY,
        NotificationID bigint NOT NULL REFERENCES dbo.OwnerBookingNotification(NotificationID),
        DeviceID bigint NOT NULL REFERENCES dbo.OwnerPushDevice(DeviceID),
        SalonOwnerID uniqueidentifier NOT NULL,
        Generation int NOT NULL,
        Attempts int NOT NULL DEFAULT 0,
        NextAttemptUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        SentUtc datetime2(3) NULL,
        LastError nvarchar(1000) NULL,
        LeaseID uniqueidentifier NULL,
        UNIQUE(NotificationID, DeviceID));
    CREATE INDEX IX_OwnerBookingPushDelivery_Pending ON dbo.OwnerBookingPushDelivery(NextAttemptUtc)
        INCLUDE(NotificationID, DeviceID, Attempts) WHERE SentUtc IS NULL;
END;
GO
IF OBJECT_ID('dbo.tr_SeatBookingDetails_OwnerNotification', 'TR') IS NULL
    EXEC('CREATE TRIGGER dbo.tr_SeatBookingDetails_OwnerNotification ON dbo.SeatBookingDetails AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END');
GO
ALTER TRIGGER dbo.tr_SeatBookingDetails_OwnerNotification
ON dbo.SeatBookingDetails AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    -- The inbox and push outbox commit or roll back with booking confirmation.
    DECLARE @created TABLE(NotificationID bigint, SalonMasterID uniqueidentifier);
    INSERT dbo.OwnerBookingNotification(SeatBookingDetailsID, SalonMasterID, CustomerName, BookingDate, StartTime, SeatCount)
    OUTPUT inserted.NotificationID, inserted.SalonMasterID INTO @created
    SELECT b.SeatBookingDetailsID, b.SalonMasterID,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(c.FirstName, N' ', c.LastName))), N''), N'Guest customer'),
        CONVERT(date,b.BookingDate),
        (SELECT MIN(t.FullTiming) FROM dbo.CustomerTimeSelection s JOIN dbo.TimeAllocation t ON t.TimeAllocationID=s.TimeAllocationID
         WHERE s.SeatBookingDetailsID=b.SeatBookingDetailsID), ISNULL(b.SeatCount,1)
    FROM inserted b
    LEFT JOIN deleted old ON old.SeatBookingDetailsID=b.SeatBookingDetailsID
    LEFT JOIN dbo.CustomerRegistration c ON c.CustomerID=b.CustomerID
    WHERE b.BookingStatusMasterID=2 AND ISNULL(old.BookingStatusMasterID,0)<>2
      AND b.SalonMasterID IS NOT NULL
      AND NOT EXISTS(SELECT 1 FROM dbo.OwnerBookingNotification n WITH(UPDLOCK,HOLDLOCK) WHERE n.SeatBookingDetailsID=b.SeatBookingDetailsID);

    INSERT dbo.OwnerBookingPushDelivery(NotificationID,DeviceID,SalonOwnerID,Generation)
    SELECT n.NotificationID,d.DeviceID,d.SalonOwnerID,d.Generation FROM @created n
    JOIN dbo.OwnerPushDevice d ON d.SalonMasterID=n.SalonMasterID AND d.IsActive=1
    JOIN dbo.SalonOwner o ON o.SalonOwnerID=d.SalonOwnerID AND o.SalonMasterID=d.SalonMasterID AND o.IsActive=1
    JOIN dbo.SalonMaster salon ON salon.SalonMasterID=o.SalonMasterID AND salon.IsActive=1;
END;
GO
IF OBJECT_ID('dbo.getOwnerBookingNotifications', 'P') IS NULL EXEC('CREATE PROCEDURE dbo.getOwnerBookingNotifications AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.getOwnerBookingNotifications
    @SalonOwnerID uniqueidentifier, @SalonMasterID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID=o.SalonMasterID
        WHERE o.SalonOwnerID=@SalonOwnerID AND o.SalonMasterID=@SalonMasterID AND o.IsActive=1 AND s.IsActive=1)
        THROW 50013, 'This salon-owner account is unavailable.', 1;
    SELECT TOP(50) n.*, CONVERT(bit,CASE WHEN r.NotificationID IS NULL THEN 0 ELSE 1 END) IsRead
    FROM dbo.OwnerBookingNotification n LEFT JOIN dbo.OwnerNotificationRead r
        ON r.NotificationID=n.NotificationID AND r.SalonOwnerID=@SalonOwnerID
    WHERE n.SalonMasterID=@SalonMasterID ORDER BY n.NotificationID DESC;
    SELECT COUNT(*) FROM dbo.OwnerBookingNotification n WHERE n.SalonMasterID=@SalonMasterID
        AND NOT EXISTS(SELECT 1 FROM dbo.OwnerNotificationRead r WHERE r.NotificationID=n.NotificationID AND r.SalonOwnerID=@SalonOwnerID);
END;
GO
IF OBJECT_ID('dbo.readOwnerBookingNotifications', 'P') IS NULL EXEC('CREATE PROCEDURE dbo.readOwnerBookingNotifications AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.readOwnerBookingNotifications
    @SalonOwnerID uniqueidentifier, @SalonMasterID uniqueidentifier, @ThroughID bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID=o.SalonMasterID
        WHERE o.SalonOwnerID=@SalonOwnerID AND o.SalonMasterID=@SalonMasterID AND o.IsActive=1 AND s.IsActive=1)
        THROW 50013, 'This salon-owner account is unavailable.', 1;
    BEGIN TRANSACTION;
    INSERT dbo.OwnerNotificationRead(SalonOwnerID,NotificationID)
    SELECT @SalonOwnerID,n.NotificationID FROM dbo.OwnerBookingNotification n WHERE n.SalonMasterID=@SalonMasterID AND n.NotificationID<=@ThroughID
        AND NOT EXISTS(SELECT 1 FROM dbo.OwnerNotificationRead r WITH(UPDLOCK,HOLDLOCK) WHERE r.SalonOwnerID=@SalonOwnerID AND r.NotificationID=n.NotificationID);
    COMMIT;
END;
GO
IF OBJECT_ID('dbo.registerOwnerPushDevice', 'P') IS NULL EXEC('CREATE PROCEDURE dbo.registerOwnerPushDevice AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.registerOwnerPushDevice
    @SalonOwnerID uniqueidentifier, @SalonMasterID uniqueidentifier, @DeviceToken varchar(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID=o.SalonMasterID
        WHERE o.SalonOwnerID=@SalonOwnerID AND o.SalonMasterID=@SalonMasterID AND o.IsActive=1 AND s.IsActive=1)
        THROW 50013, 'This salon-owner account is unavailable.', 1;
    BEGIN TRANSACTION;
    IF EXISTS(SELECT 1 FROM dbo.OwnerPushDevice WITH(UPDLOCK,HOLDLOCK) WHERE DeviceToken=@DeviceToken)
        UPDATE dbo.OwnerPushDevice SET
            Generation=Generation+CASE WHEN SalonOwnerID<>@SalonOwnerID OR SalonMasterID<>@SalonMasterID OR IsActive=0 THEN 1 ELSE 0 END,
            SalonOwnerID=@SalonOwnerID,SalonMasterID=@SalonMasterID,IsActive=1,UpdatedUtc=SYSUTCDATETIME()
        WHERE DeviceToken=@DeviceToken;
    ELSE INSERT dbo.OwnerPushDevice(DeviceToken,SalonOwnerID,SalonMasterID) VALUES(@DeviceToken,@SalonOwnerID,@SalonMasterID);
    COMMIT;
END;
GO
IF OBJECT_ID('dbo.unregisterOwnerPushDevice', 'P') IS NULL EXEC('CREATE PROCEDURE dbo.unregisterOwnerPushDevice AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.unregisterOwnerPushDevice
    @SalonOwnerID uniqueidentifier, @DeviceToken varchar(512)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.OwnerPushDevice SET IsActive=0,Generation=Generation+1,UpdatedUtc=SYSUTCDATETIME()
    WHERE SalonOwnerID=@SalonOwnerID AND DeviceToken=@DeviceToken;
END;
GO
IF OBJECT_ID('dbo.claimOwnerBookingPush', 'P') IS NULL EXEC('CREATE PROCEDURE dbo.claimOwnerBookingPush AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.claimOwnerBookingPush
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lease uniqueidentifier=NEWID();
    DECLARE @claimed TABLE(DeliveryID bigint, NotificationID bigint, DeviceID bigint, Generation int);
    ;WITH candidates AS (
        SELECT TOP(25) q.* FROM dbo.OwnerBookingPushDelivery q WITH(UPDLOCK,READPAST,ROWLOCK)
        JOIN dbo.OwnerPushDevice d ON d.DeviceID=q.DeviceID AND d.Generation=q.Generation AND d.SalonOwnerID=q.SalonOwnerID AND d.IsActive=1
        JOIN dbo.OwnerBookingNotification n ON n.NotificationID=q.NotificationID AND n.SalonMasterID=d.SalonMasterID
        JOIN dbo.SalonOwner o ON o.SalonOwnerID=d.SalonOwnerID AND o.SalonMasterID=d.SalonMasterID AND o.IsActive=1
        JOIN dbo.SalonMaster s ON s.SalonMasterID=d.SalonMasterID AND s.IsActive=1
        WHERE q.SentUtc IS NULL AND q.Attempts<8 AND q.NextAttemptUtc<=SYSUTCDATETIME()
          AND n.CreatedUtc>DATEADD(day,-1,SYSUTCDATETIME())
        ORDER BY q.DeliveryID)
    UPDATE candidates SET Attempts=Attempts+1,NextAttemptUtc=DATEADD(minute,2,SYSUTCDATETIME()),LeaseID=@lease
    OUTPUT inserted.DeliveryID,inserted.NotificationID,inserted.DeviceID,inserted.Generation INTO @claimed;
    SELECT q.DeliveryID,q.DeviceID,q.Generation,@lease LeaseID,d.DeviceToken,n.* FROM @claimed q
    JOIN dbo.OwnerPushDevice d ON d.DeviceID=q.DeviceID
    JOIN dbo.OwnerBookingNotification n ON n.NotificationID=q.NotificationID;
END;
GO
IF OBJECT_ID('dbo.finishOwnerBookingPush', 'P') IS NULL EXEC('CREATE PROCEDURE dbo.finishOwnerBookingPush AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.finishOwnerBookingPush
    @DeliveryID bigint, @LeaseID uniqueidentifier, @Success bit, @InvalidToken bit, @Error nvarchar(1000)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.OwnerBookingPushDelivery SET SentUtc=CASE WHEN @Success=1 THEN SYSUTCDATETIME() ELSE NULL END,
        NextAttemptUtc=DATEADD(minute,1,SYSUTCDATETIME()),LastError=@Error
    WHERE DeliveryID=@DeliveryID AND LeaseID=@LeaseID;
    IF @InvalidToken=1
        UPDATE d SET IsActive=0 FROM dbo.OwnerPushDevice d JOIN dbo.OwnerBookingPushDelivery q ON q.DeviceID=d.DeviceID AND q.Generation=d.Generation
        WHERE q.DeliveryID=@DeliveryID AND q.LeaseID=@LeaseID;
END;
GO
IF OBJECT_ID('dbo.isOwnerBookingPushCurrent', 'P') IS NULL EXEC('CREATE PROCEDURE dbo.isOwnerBookingPushCurrent AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.isOwnerBookingPushCurrent
    @DeliveryID bigint, @LeaseID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) FROM dbo.OwnerBookingPushDelivery q
    JOIN dbo.OwnerPushDevice d ON d.DeviceID=q.DeviceID AND d.Generation=q.Generation AND d.SalonOwnerID=q.SalonOwnerID AND d.IsActive=1
    JOIN dbo.OwnerBookingNotification n ON n.NotificationID=q.NotificationID AND n.SalonMasterID=d.SalonMasterID
    JOIN dbo.SalonOwner o ON o.SalonOwnerID=d.SalonOwnerID AND o.SalonMasterID=d.SalonMasterID AND o.IsActive=1
    JOIN dbo.SalonMaster s ON s.SalonMasterID=d.SalonMasterID AND s.IsActive=1
    WHERE q.DeliveryID=@DeliveryID AND q.LeaseID=@LeaseID AND q.SentUtc IS NULL;
END;
GO
