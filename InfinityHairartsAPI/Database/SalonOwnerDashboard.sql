SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.getSalonOwnerDashboard', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE dbo.getSalonOwnerDashboard AS BEGIN SET NOCOUNT ON; END');
END;
GO

ALTER PROCEDURE dbo.getSalonOwnerDashboard
    @SalonMasterID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.SalonMaster
        WHERE SalonMasterID = @SalonMasterID
          AND IsActive = 1
    )
    BEGIN
        THROW 50002, 'The selected salon does not exist or is inactive.', 1;
    END;

    IF COL_LENGTH(N'dbo.SeatBookingDetails', N'SalonMasterID') IS NULL
       AND (SELECT COUNT(*) FROM dbo.SalonMaster WHERE IsActive = 1) > 1
    BEGIN
        THROW 50003, 'Bookings must be linked to SalonMasterID before using a multi-salon dashboard.', 1;
    END;

    DECLARE @Today DATE = CONVERT(DATE, GETDATE());
    DECLARE @BookedStatusID INT =
    (
        SELECT TOP (1) BookingStatusMasterID
        FROM dbo.BookingStatusMaster
        WHERE BookingStatus = N'Booked'
    );

    SELECT
        TodayBookings =
        (
            SELECT COUNT(*)
            FROM dbo.SeatBookingDetails
            WHERE BookingDate = @Today
              AND BookingStatusMasterID = @BookedStatusID
        ),
        TodaySeats =
        (
            SELECT COALESCE(SUM(SeatCount), 0)
            FROM dbo.SeatBookingDetails
            WHERE BookingDate = @Today
              AND BookingStatusMasterID = @BookedStatusID
        ),
        TodayRevenue =
        (
            SELECT COALESCE(SUM(BillIDTotalAmount), 0)
            FROM dbo.HairCut_Item_Bill_Master
            WHERE CONVERT(DATE, CreatedDate) = @Today
              AND COALESCE(IsDeleted, 0) = 0
        ),
        TotalCustomers =
        (
            SELECT COUNT(*)
            FROM dbo.CustomerRegistration
            WHERE COALESCE(IsDeleted, 0) = 0
        ),
        ActiveServices =
        (
            SELECT COUNT(*)
            FROM dbo.HairCut_Item_List
            WHERE COALESCE(IsDeleted, 0) = 0
        ),
        UpcomingBookings =
        (
            SELECT COUNT(*)
            FROM dbo.SeatBookingDetails
            WHERE BookingDate >= @Today
              AND BookingStatusMasterID = @BookedStatusID
        );

    SELECT TOP (6)
        SB.SeatBookingDetailsID,
        CustomerName = COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(CR.FirstName, N' ', CR.LastName))), N''), N'Guest customer'),
        MobileNo = COALESCE(CR.MobileNo, N''),
        SB.BookingDate,
        StartTime = MIN(TA.FullTiming),
        SeatCount = COALESCE(SB.SeatCount, 0),
        BookingStatus = COALESCE(BSM.BookingStatus, N'Booked')
    FROM dbo.SeatBookingDetails SB
    LEFT JOIN dbo.CustomerRegistration CR
        ON CR.CustomerID = SB.CustomerID
    LEFT JOIN dbo.BookingStatusMaster BSM
        ON BSM.BookingStatusMasterID = SB.BookingStatusMasterID
    LEFT JOIN dbo.CustomerTimeSelection CTS
        ON CTS.SeatBookingDetailsID = SB.SeatBookingDetailsID
    LEFT JOIN dbo.TimeAllocation TA
        ON TA.TimeAllocationID = CTS.TimeAllocationID
    WHERE SB.BookingDate >= @Today
      AND SB.BookingStatusMasterID = @BookedStatusID
    GROUP BY
        SB.SeatBookingDetailsID,
        CR.FirstName,
        CR.LastName,
        CR.MobileNo,
        SB.BookingDate,
        SB.SeatCount,
        BSM.BookingStatus
    ORDER BY SB.BookingDate, MIN(TA.FullTiming), SB.SeatBookingDetailsID;

    ;WITH DateOffsets AS
    (
        SELECT OffsetValue
        FROM (VALUES (0), (1), (2), (3), (4), (5), (6)) AS DayValues(OffsetValue)
    ),
    DashboardDates AS
    (
        SELECT DATEADD(DAY, OffsetValue - 6, @Today) AS BookingDate
        FROM DateOffsets
    )
    SELECT
        D.BookingDate,
        BookingCount = COUNT(SB.SeatBookingDetailsID)
    FROM DashboardDates D
    LEFT JOIN dbo.SeatBookingDetails SB
        ON SB.BookingDate = D.BookingDate
       AND SB.BookingStatusMasterID = @BookedStatusID
    GROUP BY D.BookingDate
    ORDER BY D.BookingDate;

    SELECT TOP (4)
        HairCutItemID = HIL.HairCut_Item_ID,
        ServiceName = COALESCE(HIL.HairCut_Item_Name, N'Unnamed service'),
        BookingCount = COALESCE(SUM(HCB.HairCut_Item_Quantity), 0),
        Revenue = COALESCE(SUM(HCB.HairCut_Item_Total), 0)
    FROM dbo.HairCut_Item_Customer_Bill HCB
    INNER JOIN dbo.HairCut_Item_List HIL
        ON HIL.HairCut_Item_ID = HCB.HairCut_Item_ID
    WHERE HCB.Created_Date >= DATEADD(DAY, -30, @Today)
      AND COALESCE(HCB.IsDeleted, 0) = 0
      AND COALESCE(HIL.IsDeleted, 0) = 0
    GROUP BY HIL.HairCut_Item_ID, HIL.HairCut_Item_Name
    ORDER BY SUM(HCB.HairCut_Item_Quantity) DESC, SUM(HCB.HairCut_Item_Total) DESC;
END;
GO
