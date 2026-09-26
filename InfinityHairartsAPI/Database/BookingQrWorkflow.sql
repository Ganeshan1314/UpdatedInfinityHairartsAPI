SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.SeatBookingDetails', N'U') IS NULL
   OR OBJECT_ID(N'dbo.TimeAllocation', N'U') IS NULL
   OR OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL
   OR OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL
BEGIN
    THROW 50000, 'Create the booking, salon, and salon-owner tables before running BookingQrWorkflow.sql.', 1;
END;
GO

IF COL_LENGTH(N'dbo.SeatBookingDetails', N'SalonMasterID') IS NULL
BEGIN
    ALTER TABLE dbo.SeatBookingDetails
        ADD SalonMasterID UNIQUEIDENTIFIER NULL;

    ALTER TABLE dbo.SeatBookingDetails
        ADD CONSTRAINT FK_SeatBookingDetails_SalonMaster
            FOREIGN KEY (SalonMasterID) REFERENCES dbo.SalonMaster (SalonMasterID);

    CREATE INDEX IX_SeatBookingDetails_SalonMaster_BookingDate
        ON dbo.SeatBookingDetails (SalonMasterID, BookingDate);
END;
GO

-- Existing data can be assigned safely when the installation has one active salon.
IF (SELECT COUNT(*) FROM dbo.SalonMaster WHERE IsActive = 1) = 1
BEGIN
    UPDATE dbo.SeatBookingDetails
    SET SalonMasterID =
    (
        SELECT TOP (1) SalonMasterID
        FROM dbo.SalonMaster
        WHERE IsActive = 1
    )
    WHERE SalonMasterID IS NULL;
END;
GO

IF OBJECT_ID(N'dbo.BookingQrCompletion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BookingQrCompletion
    (
        SeatBookingDetailsID UNIQUEIDENTIFIER NOT NULL,
        TimeAllocationID UNIQUEIDENTIFIER NOT NULL,
        SalonOwnerID UNIQUEIDENTIFIER NOT NULL,
        CompletedUtc DATETIME2(0) NOT NULL
            CONSTRAINT DF_BookingQrCompletion_CompletedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_BookingQrCompletion
            PRIMARY KEY (SeatBookingDetailsID, TimeAllocationID)
    );
END;
GO

-- The legacy source tables do not consistently declare their ID columns as
-- primary/unique keys, so SQL Server cannot create foreign keys to them. The
-- completion procedure validates all three IDs before inserting instead.
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.BookingQrCompletion')
      AND name = N'IX_BookingQrCompletion_CompletedUtc'
)
BEGIN
    CREATE INDEX IX_BookingQrCompletion_CompletedUtc
        ON dbo.BookingQrCompletion (CompletedUtc);
END;
GO

CREATE OR ALTER PROCEDURE dbo.completeBookingFromQr
    @SeatBookingDetailsID UNIQUEIDENTIFIER,
    @TimeAllocationID UNIQUEIDENTIFIER,
    @SalonOwnerID UNIQUEIDENTIFIER,
    @SalonMasterID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.SalonOwner
        WHERE SalonOwnerID = @SalonOwnerID
          AND SalonMasterID = @SalonMasterID
          AND IsActive = 1
    )
    BEGIN
        THROW 50013, 'This admin account is not authorized for the selected salon.', 1;
    END;

    DECLARE @BookingDate DATE;
    DECLARE @BookingTime TIME;
    DECLARE @CustomerName NVARCHAR(301);

    SELECT
        @BookingDate = CONVERT(DATE, SB.BookingDate),
        @BookingTime = TA.FullTiming,
        @CustomerName = COALESCE(
            NULLIF(LTRIM(RTRIM(CONCAT(CR.FirstName, N' ', CR.LastName))), N''),
            N'Guest customer')
    FROM dbo.SeatBookingDetails SB
    INNER JOIN dbo.CustomerTimeSelection CTS
        ON CTS.SeatBookingDetailsID = SB.SeatBookingDetailsID
       AND CTS.TimeAllocationID = @TimeAllocationID
    INNER JOIN dbo.TimeAllocation TA
        ON TA.TimeAllocationID = CTS.TimeAllocationID
    LEFT JOIN dbo.CustomerRegistration CR
        ON CR.CustomerID = SB.CustomerID
    WHERE SB.SeatBookingDetailsID = @SeatBookingDetailsID
      AND SB.BookingStatusMasterID = 2
      AND
      (
          SB.SalonMasterID = @SalonMasterID
          OR
          (
              SB.SalonMasterID IS NULL
              AND (SELECT COUNT(*) FROM dbo.SalonMaster WHERE IsActive = 1) = 1
          )
      );

    IF @BookingDate IS NULL OR @BookingTime IS NULL
    BEGIN
        THROW 50010, 'This QR code does not match a confirmed booking for your salon.', 1;
    END;

    DECLARE @AppointmentStart DATETIME2(0) = DATEADD(
        SECOND,
        DATEDIFF(SECOND, CAST('00:00:00' AS TIME), @BookingTime),
        CAST(@BookingDate AS DATETIME2(0)));

    IF @AppointmentStart > GETDATE()
    BEGIN
        THROW 50011, 'This appointment is still Upcoming. It can be completed at or after the booked time.', 1;
    END;

    BEGIN TRANSACTION;

    DECLARE @WasAlreadyCompleted BIT = 0;
    DECLARE @CompletedUtc DATETIME2(0);

    SELECT @CompletedUtc = CompletedUtc
    FROM dbo.BookingQrCompletion WITH (UPDLOCK, HOLDLOCK)
    WHERE SeatBookingDetailsID = @SeatBookingDetailsID
      AND TimeAllocationID = @TimeAllocationID;

    IF @CompletedUtc IS NULL
    BEGIN
        SET @CompletedUtc = SYSUTCDATETIME();

        INSERT INTO dbo.BookingQrCompletion
        (
            SeatBookingDetailsID,
            TimeAllocationID,
            SalonOwnerID,
            CompletedUtc
        )
        VALUES
        (
            @SeatBookingDetailsID,
            @TimeAllocationID,
            @SalonOwnerID,
            @CompletedUtc
        );
    END
    ELSE
    BEGIN
        SET @WasAlreadyCompleted = 1;
    END;

    COMMIT TRANSACTION;

    SELECT
        SeatBookingDetailsID = @SeatBookingDetailsID,
        TimeAllocationID = @TimeAllocationID,
        CustomerName = @CustomerName,
        BookingDate = @BookingDate,
        BookingTime = @BookingTime,
        CompletedUtc = @CompletedUtc,
        WasAlreadyCompleted = @WasAlreadyCompleted;
END;
GO

CREATE OR ALTER PROCEDURE dbo.getCustomerBookingInformation
    @CustomerID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        BM.UniqueBillID,
        BM.SeatBookingDetailsID,
        CT.TimeAllocationID,
        TA.FullTiming,
        SB.BookingDate,
        SB.SeatCount,
        Completion.CompletedUtc,
        ISNULL((
            SELECT SUM(A.HairCut_Item_Total)
            FROM dbo.HairCut_Item_Customer_Bill A
            WHERE A.UniqueBillID = BM.UniqueBillID
        ), 0) AS TotalAmount,
        CR.MobileNo,
        CR.GeneralAddress,
        SUBSTRING((
            SELECT N',' + HL.HairCut_Item_Name AS [data()]
            FROM dbo.HairCut_Item_Customer_Bill A
            INNER JOIN dbo.HairCut_Item_List HL
                ON HL.HairCut_Item_ID = A.HairCut_Item_ID
            WHERE A.UniqueBillID = BM.UniqueBillID
            FOR XML PATH('')
        ), 2, 9999) AS ItemName
    FROM dbo.HairCut_Item_Bill_Master BM
    INNER JOIN dbo.SeatBookingDetails SB
        ON SB.SeatBookingDetailsID = BM.SeatBookingDetailsID
    INNER JOIN dbo.CustomerRegistration CR
        ON CR.CustomerID = SB.CustomerID
    LEFT JOIN dbo.CustomerTimeSelection CT
        ON CT.SeatBookingDetailsID = BM.SeatBookingDetailsID
    LEFT JOIN dbo.TimeAllocation TA
        ON TA.TimeAllocationID = CT.TimeAllocationID
    LEFT JOIN dbo.BookingQrCompletion Completion
        ON Completion.SeatBookingDetailsID = BM.SeatBookingDetailsID
       AND Completion.TimeAllocationID = CT.TimeAllocationID
    WHERE SB.CustomerID = @CustomerID
    ORDER BY SB.BookingDate DESC, TA.FullTiming DESC, BM.UniqueBillID DESC;
END;
GO
