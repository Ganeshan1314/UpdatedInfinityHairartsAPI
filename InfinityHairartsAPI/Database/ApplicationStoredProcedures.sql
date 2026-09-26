CREATE OR ALTER PROCEDURE dbo.getCustomerProfileByMobileNumber
    @MobileNo NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        FirstName,
        EmailAddress,
        GeneralAddress,
        ImageName,
        MobileNo,
        CustomerID
    FROM dbo.CustomerRegistration
    WHERE MobileNo = @MobileNo;
END;
GO

CREATE OR ALTER PROCEDURE dbo.getLatestActiveHairCutItemBillMasterPrimary
    @CustomerID UNIQUEIDENTIFIER,
    @SeatBookingDetailsID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (1)
        UniqueBillID,
        CreatedBy,
        CreatedDate,
        CusotmerBillCount
    FROM dbo.HairCut_Item_Bill_Master_Primary
    WHERE CreatedBy = @CustomerID
      AND SeatBookingDetailsID = @SeatBookingDetailsID
      AND ISNULL(IsDeleted, 0) = 0
    ORDER BY CreatedDate DESC;
END;
GO

CREATE OR ALTER PROCEDURE dbo.insertHairCutItemBillMasterPrimary
    @UniqueBillID UNIQUEIDENTIFIER,
    @SeatBookingDetailsID UNIQUEIDENTIFIER,
    @CustomerBillID NVARCHAR(100),
    @BillIDFormat NVARCHAR(100),
    @CusotmerBillCount INT,
    @CreatedBy UNIQUEIDENTIFIER,
    @CreatedDate DATETIME
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.HairCut_Item_Bill_Master_Primary
    (
        UniqueBillID,
        SeatBookingDetailsID,
        CustomerBillID,
        BillIDFormat,
        PaymentType,
        CusotmerBillCount,
        CreatedBy,
        CreatedDate
    )
    VALUES
    (
        @UniqueBillID,
        @SeatBookingDetailsID,
        @CustomerBillID,
        @BillIDFormat,
        'ONLINE',
        @CusotmerBillCount,
        @CreatedBy,
        @CreatedDate
    );
END;
GO

CREATE OR ALTER PROCEDURE dbo.getClientSideCartItemsByBill
    @CustomerID UNIQUEIDENTIFIER,
    @UniqueBillID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        A.HairCut_Item_ID,
        COUNT(A.HairCut_Item_ID) AS HairCut_Item_Quantity,
        SUM(COUNT(A.HairCut_Item_ID)) OVER() AS TotalCartItemCount,
        B.HairCut_Item_Name,
        B.HairCut_Item_Price,
        SUM(A.HairCut_Item_Total) AS HairCut_Item_Total,
        SUM(SUM(A.HairCut_Item_Total)) OVER() AS HairCutItemSubTotal,
        B.HairCutItemImage,
        B.HairCutItemImageSystemPath
    FROM dbo.HairCut_Item_Customer_Bill_Primary A
    INNER JOIN dbo.HairCut_Item_List B
        ON B.HairCut_Item_ID = A.HairCut_Item_ID
    INNER JOIN dbo.HairCut_Item_Bill_Master_Primary M
        ON M.UniqueBillID = A.UniqueBillID
    WHERE A.Created_By = @CustomerID
      AND A.UniqueBillID = @UniqueBillID
      AND ISNULL(M.IsNextBillClicked, '') <> 'Clicked'
    GROUP BY
        A.HairCut_Item_ID,
        B.HairCut_Item_Name,
        B.HairCut_Item_Price,
        A.HairCut_Item_Total,
        B.HairCutItemImage,
        B.HairCutItemImageSystemPath;
END;
GO

CREATE OR ALTER PROCEDURE dbo.getBookedTimeAllocationsByDate
    @BookingDate DATE,
    @CurrentSeatBookingDetailsID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DISTINCT CT.TimeAllocationID
    FROM dbo.CustomerTimeSelection CT
    INNER JOIN dbo.SeatBookingDetails SB
        ON SB.SeatBookingDetailsID = CT.SeatBookingDetailsID
    WHERE CONVERT(DATE, SB.BookingDate) = @BookingDate
      AND SB.SeatBookingDetailsID <> @CurrentSeatBookingDetailsID
      AND SB.BookingStatusMasterID = 2;
END;
GO

CREATE OR ALTER PROCEDURE dbo.getCustomerTimeSelections
    @CustomerID UNIQUEIDENTIFIER,
    @SeatBookingDetailsID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        TimeSelectionID,
        SeatBookingDetailsID,
        CustomerID,
        TimeAllocationID
    FROM dbo.CustomerTimeSelection
    WHERE CustomerID = @CustomerID
      AND SeatBookingDetailsID = @SeatBookingDetailsID
    ORDER BY CreatedOn ASC;
END;
GO

CREATE OR ALTER PROCEDURE dbo.countRecentConflictingTimeAllocation
    @TimeAllocationID UNIQUEIDENTIFIER,
    @CustomerID UNIQUEIDENTIFIER,
    @SeatBookingDetailsID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT COUNT(1)
    FROM dbo.CustomerTimeSelection CT
    INNER JOIN dbo.SeatBookingDetails SB
        ON SB.SeatBookingDetailsID = CT.SeatBookingDetailsID
    INNER JOIN dbo.SeatBookingDetails MySB
        ON MySB.SeatBookingDetailsID = @SeatBookingDetailsID
    WHERE CT.TimeAllocationID = @TimeAllocationID
      AND CT.CustomerID <> @CustomerID
      AND CAST(SB.BookingDate AS DATE) = CAST(MySB.BookingDate AS DATE)
      AND SB.CreatedOn >= DATEADD(MINUTE, -10, GETDATE());
END;
GO

CREATE OR ALTER PROCEDURE dbo.getCheckoutSelectedSlots
    @SeatBookingDetailsID UNIQUEIDENTIFIER,
    @CustomerID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT SB.BookingDate, CT.TimeAllocationID
    FROM dbo.SeatBookingDetails SB
    INNER JOIN dbo.CustomerTimeSelection CT
        ON CT.SeatBookingDetailsID = SB.SeatBookingDetailsID
    WHERE SB.SeatBookingDetailsID = @SeatBookingDetailsID
      AND SB.CustomerID = @CustomerID
    ORDER BY SB.BookingDate, CT.TimeAllocationID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.acquireBookingSlotLock
    @Resource NVARCHAR(255)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Result INT;
    EXEC @Result = sys.sp_getapplock
        @Resource = @Resource,
        @LockMode = 'Exclusive',
        @LockOwner = 'Transaction',
        @LockTimeout = 10000;

    SELECT @Result;
END;
GO

CREATE OR ALTER PROCEDURE dbo.countConfirmedCheckoutSlotConflicts
    @SeatBookingDetailsID UNIQUEIDENTIFIER,
    @CustomerID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT COUNT(1)
    FROM dbo.CustomerTimeSelection CurrentSelection
    INNER JOIN dbo.SeatBookingDetails CurrentBooking
        ON CurrentBooking.SeatBookingDetailsID = CurrentSelection.SeatBookingDetailsID
    INNER JOIN dbo.CustomerTimeSelection ConfirmedSelection
        ON ConfirmedSelection.TimeAllocationID = CurrentSelection.TimeAllocationID
       AND ConfirmedSelection.SeatBookingDetailsID <> CurrentSelection.SeatBookingDetailsID
    INNER JOIN dbo.SeatBookingDetails ConfirmedBooking
        ON ConfirmedBooking.SeatBookingDetailsID = ConfirmedSelection.SeatBookingDetailsID
       AND ConfirmedBooking.BookingDate = CurrentBooking.BookingDate
    WHERE CurrentSelection.SeatBookingDetailsID = @SeatBookingDetailsID
      AND CurrentBooking.CustomerID = @CustomerID
      AND ConfirmedBooking.BookingStatusMasterID = 2;
END;
GO

CREATE OR ALTER PROCEDURE dbo.deleteClientSideCartItemsByBill
    @UniqueBillID UNIQUEIDENTIFIER,
    @CustomerID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.HairCut_Item_Customer_Bill_Primary
    WHERE UniqueBillID = @UniqueBillID
      AND Created_By = @CustomerID;
END;
GO
