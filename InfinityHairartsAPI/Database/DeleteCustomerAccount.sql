-- Customer self-service account deletion (Apple guideline 5.1.1(v)).
-- Run on the API database BEFORE deploying the API that calls it. Safe to run more than once.
-- Running this script changes no customer data. It creates one empty table and its procedures.
-- Works on SQL Server 2016 without Service Pack 1, which has no "create or alter" statement.
--
-- A customer's request only records a date 7 days ahead; they can cancel until then.
-- When that date has passed, the API runs dbo.DeleteCustomerAccount, which:
--   * erases personal details on every CustomerRegistration row with that mobile number
--     and marks those rows deleted;
--   * cancels appointments dated that day or later that are still Pending, freeing their times;
--   * removes the customer's name from salon-owner booking notifications;
--   * removes the customer's push-notification tokens and the deletion request.
-- Past visits and their bills stay for salon records, linked only to the erased customer row.
-- Profile photo files are removed by the API, using the CustomerID list returned here.
-- A "customer's rows" below means every CustomerRegistration row with the same mobile number.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.CustomerAccountDeletionRequest', N'U') IS NULL
    CREATE TABLE dbo.CustomerAccountDeletionRequest
    (
        CustomerID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerAccountDeletionRequest PRIMARY KEY,
        RequestedUtc datetime2(0) NOT NULL,
        DeleteAfterUtc datetime2(0) NOT NULL
    );
GO

-- The date a pending deletion becomes due, or NULL when none is pending.
IF OBJECT_ID(N'dbo.GetCustomerAccountDeletion', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.GetCustomerAccountDeletion AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.GetCustomerAccountDeletion
    @CustomerID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT MIN(r.DeleteAfterUtc) AS DeleteAfterUtc
    FROM dbo.CustomerAccountDeletionRequest r
    WHERE r.CustomerID = @CustomerID
       OR r.CustomerID IN (
            SELECT same.CustomerID
            FROM dbo.CustomerRegistration mine
            INNER JOIN dbo.CustomerRegistration same ON same.MobileNo = mine.MobileNo
            WHERE mine.CustomerID = @CustomerID AND NULLIF(LTRIM(RTRIM(mine.MobileNo)), N'') IS NOT NULL);
END;
GO

-- Records a request. Asking again keeps the original date.
IF OBJECT_ID(N'dbo.RequestCustomerAccountDeletion', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.RequestCustomerAccountDeletion AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.RequestCustomerAccountDeletion
    @CustomerID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @MobileNo nvarchar(50);
    SELECT @MobileNo = LTRIM(RTRIM(MobileNo))
    FROM dbo.CustomerRegistration WITH (UPDLOCK, HOLDLOCK)
    WHERE CustomerID = @CustomerID AND ISNULL(IsDeleted, 0) = 0;
    IF @@ROWCOUNT = 0
        THROW 50071, 'The customer account was not found.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.CustomerAccountDeletionRequest r WITH (UPDLOCK, HOLDLOCK)
        WHERE r.CustomerID = @CustomerID
           OR (NULLIF(@MobileNo, N'') IS NOT NULL AND r.CustomerID IN (
                SELECT CustomerID FROM dbo.CustomerRegistration WHERE MobileNo = @MobileNo)))
        INSERT dbo.CustomerAccountDeletionRequest (CustomerID, RequestedUtc, DeleteAfterUtc)
        VALUES (@CustomerID, SYSUTCDATETIME(), DATEADD(DAY, 7, SYSUTCDATETIME()));

    EXEC dbo.GetCustomerAccountDeletion @CustomerID;

    COMMIT TRANSACTION;
END;
GO

-- Cancels a request that is not yet due. Returns NULL once nothing is pending; a date that
-- is still returned means the waiting period has ended and the deletion can no longer be stopped.
IF OBJECT_ID(N'dbo.CancelCustomerAccountDeletion', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.CancelCustomerAccountDeletion AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.CancelCustomerAccountDeletion
    @CustomerID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @MobileNo nvarchar(50) = (
        SELECT LTRIM(RTRIM(MobileNo)) FROM dbo.CustomerRegistration WHERE CustomerID = @CustomerID);

    DELETE r FROM dbo.CustomerAccountDeletionRequest r
    WHERE r.DeleteAfterUtc > SYSUTCDATETIME()
      AND (r.CustomerID = @CustomerID
        OR (NULLIF(@MobileNo, N'') IS NOT NULL AND r.CustomerID IN (
            SELECT CustomerID FROM dbo.CustomerRegistration WHERE MobileNo = @MobileNo)));

    EXEC dbo.GetCustomerAccountDeletion @CustomerID;

    COMMIT TRANSACTION;
END;
GO

-- Requests whose waiting period has ended.
IF OBJECT_ID(N'dbo.GetDueCustomerAccountDeletions', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.GetDueCustomerAccountDeletions AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.GetDueCustomerAccountDeletions
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (100) CustomerID
    FROM dbo.CustomerAccountDeletionRequest
    WHERE DeleteAfterUtc <= SYSUTCDATETIME()
    ORDER BY DeleteAfterUtc;
END;
GO

-- Erases an account. It does nothing, and returns no rows, unless that account has a
-- request whose waiting period has ended.
IF OBJECT_ID(N'dbo.DeleteCustomerAccount', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.DeleteCustomerAccount AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.DeleteCustomerAccount
    @CustomerID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    -- The same lock that time reservations take, so no time can be reserved mid-deletion.
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock
        @Resource = N'Salonexa.TimeReservations.v1', @LockMode = N'Exclusive',
        @LockOwner = N'Transaction', @LockTimeout = 5000;
    IF @LockResult < 0
        THROW 50070, 'Account deletion is busy. Please try again.', 1;

    DECLARE @Customers TABLE (CustomerID uniqueidentifier PRIMARY KEY);

    -- Holding this row stops a cancellation from slipping in while the account is erased.
    IF NOT EXISTS (
        SELECT 1 FROM dbo.CustomerAccountDeletionRequest WITH (UPDLOCK, HOLDLOCK)
        WHERE CustomerID = @CustomerID AND DeleteAfterUtc <= SYSUTCDATETIME())
    BEGIN
        SELECT CustomerID FROM @Customers;
        COMMIT TRANSACTION;
        RETURN;
    END;

    DECLARE @MobileNo nvarchar(50);
    SELECT @MobileNo = LTRIM(RTRIM(MobileNo))
    FROM dbo.CustomerRegistration WITH (UPDLOCK, HOLDLOCK)
    WHERE CustomerID = @CustomerID;
    IF @@ROWCOUNT = 0
    BEGIN
        -- The account no longer exists, so only the request is left to clear.
        DELETE dbo.CustomerAccountDeletionRequest WHERE CustomerID = @CustomerID;
        SELECT CustomerID FROM @Customers;
        COMMIT TRANSACTION;
        RETURN;
    END;

    -- Include older rows for the same number, so no copy of the details remains.
    INSERT @Customers (CustomerID) VALUES (@CustomerID);
    IF NULLIF(@MobileNo, N'') IS NOT NULL
        INSERT @Customers (CustomerID)
        SELECT DISTINCT CustomerID FROM dbo.CustomerRegistration
        WHERE MobileNo = @MobileNo AND CustomerID <> @CustomerID;

    DECLARE @Today date = CONVERT(date, DATEADD(MINUTE, 330, SYSUTCDATETIME())); -- India time
    DECLARE @Cancelled TABLE (SeatBookingDetailsID uniqueidentifier PRIMARY KEY);
    INSERT @Cancelled (SeatBookingDetailsID)
    SELECT s.SeatBookingDetailsID
    FROM dbo.SeatBookingDetails s
    INNER JOIN @Customers c ON c.CustomerID = s.CustomerID
    WHERE CONVERT(date, s.BookingDate) >= @Today AND s.CompletionStatus = N'Pending';

    DELETE t FROM dbo.CustomerTimeSelection t
    INNER JOIN @Cancelled x ON x.SeatBookingDetailsID = t.SeatBookingDetailsID;

    -- A live bill row also marks a booking as confirmed, so retire it with the booking.
    UPDATE b SET IsDeleted = 1 FROM dbo.HairCut_Item_Bill_Master b
    INNER JOIN @Cancelled x ON x.SeatBookingDetailsID = b.SeatBookingDetailsID;

    UPDATE s SET BookingStatusMasterID = 1 FROM dbo.SeatBookingDetails s
    INNER JOIN @Cancelled x ON x.SeatBookingDetailsID = s.SeatBookingDetailsID;

    IF OBJECT_ID(N'dbo.OwnerBookingNotification', N'U') IS NOT NULL
        UPDATE n SET CustomerName = N'Deleted customer'
        FROM dbo.OwnerBookingNotification n
        INNER JOIN dbo.SeatBookingDetails s ON s.SeatBookingDetailsID = n.SeatBookingDetailsID
        INNER JOIN @Customers c ON c.CustomerID = s.CustomerID;

    IF OBJECT_ID(N'dbo.CustomerPushDevice', N'U') IS NOT NULL
        DELETE d FROM dbo.CustomerPushDevice d
        INNER JOIN @Customers c ON c.CustomerID = d.CustomerID;

    -- Installations differ in which of these columns exist, so erase the ones present.
    -- If CustomerRegistration holds other personal columns, add them to this list.
    DECLARE @Erase nvarchar(max) = N'';
    SELECT @Erase = @Erase + N', ' + QUOTENAME(name) + N' = N'''''
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.CustomerRegistration')
      AND name IN (N'MobileNo', N'FirstName', N'MiddleName', N'LastName', N'UserName', N'EmailID',
                   N'EmailAddress', N'GeneralAddress', N'PINCode', N'ImageName', N'password', N'UserOTP');

    DECLARE @Sql nvarchar(max) =
        N'UPDATE dbo.CustomerRegistration SET IsDeleted = 1' + @Erase
        + N' WHERE CustomerID = @ID;';
    DECLARE @Next uniqueidentifier;
    SELECT TOP (1) @Next = CustomerID FROM @Customers ORDER BY CustomerID;
    WHILE @Next IS NOT NULL
    BEGIN
        EXEC sys.sp_executesql @Sql, N'@ID uniqueidentifier', @ID = @Next;
        SET @Next = (SELECT TOP (1) CustomerID FROM @Customers WHERE CustomerID > @Next ORDER BY CustomerID);
    END;

    -- CustomerID can be empty in this table, so also erase such rows for the same number.
    IF NULLIF(@MobileNo, N'') IS NOT NULL
    BEGIN
        SET @Sql = N'UPDATE dbo.CustomerRegistration SET IsDeleted = 1' + @Erase
            + N' WHERE CustomerID IS NULL AND MobileNo = @Mobile;';
        EXEC sys.sp_executesql @Sql, N'@Mobile nvarchar(50)', @Mobile = @MobileNo;
    END;

    DELETE r FROM dbo.CustomerAccountDeletionRequest r
    INNER JOIN @Customers c ON c.CustomerID = r.CustomerID;

    SELECT CustomerID FROM @Customers;

    COMMIT TRANSACTION;
END;
GO
