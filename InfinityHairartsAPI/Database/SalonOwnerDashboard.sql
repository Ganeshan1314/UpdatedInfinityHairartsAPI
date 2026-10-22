SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
    THROW 50003, 'Run BookingQrWorkflow.sql before SalonOwnerDashboard.sql.', 1;
GO

IF OBJECT_ID(N'dbo.GetSalonOwnerDashboard', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerDashboard AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerDashboard @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @Date date
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1)
        THROW 50013, 'Please sign in again.', 1;
    -- CompletionStatus is saved atomically with the appointment QR audit row.
    SELECT TodayBookings = COUNT(CASE WHEN sb.BookingDate = @Date THEN 1 END),
        TodaySeats = COALESCE(SUM(CASE WHEN sb.BookingDate = @Date THEN sb.SeatCount ELSE 0 END), 0),
        PendingBookings = COUNT(CASE WHEN sb.BookingDate = @Date AND sb.CompletionStatus = N'Pending' THEN 1 END),
        CompletedBookings = COUNT(CASE WHEN sb.BookingDate = @Date AND sb.CompletionStatus = N'Completed' THEN 1 END),
        TotalBookings = COUNT(*),
        TotalPendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Pending' THEN 1 END),
        TotalCompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Completed' THEN 1 END),
        TodayRevenue = (SELECT COALESCE(SUM(b.BillIDTotalAmount), 0) FROM dbo.HairCut_Item_Bill_Master b
            JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
            WHERE sb.SalonMasterID = @SalonID AND b.CreatedDate >= @Date AND b.CreatedDate < DATEADD(day, 1, @Date) AND COALESCE(b.IsDeleted, 0) = 0),
        TotalCustomers = (SELECT COUNT(DISTINCT CustomerID) FROM dbo.SeatBookingDetails WHERE SalonMasterID = @SalonID AND BookingStatusMasterID = 2),
        ActiveServices = (SELECT COUNT(*) FROM dbo.SalonService WHERE SalonMasterID = @SalonID AND IsActive = 1),
        UpcomingBookings = COUNT(CASE WHEN sb.BookingDate >= @Date AND sb.CompletionStatus = N'Pending' THEN 1 END)
    FROM dbo.SeatBookingDetails sb
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingStatusMasterID = 2;
    SELECT TOP(30) sb.SeatBookingDetailsID,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(cr.FirstName, ' ', cr.LastName))), ''), 'Guest customer') AS CustomerName,
        COALESCE(cr.MobileNo, '') AS MobileNo, sb.BookingDate,
        MIN(ta.FullTiming) AS StartTime, COALESCE(sb.SeatCount, 0) AS SeatCount,
        sb.CompletionStatus AS BookingStatus
    FROM dbo.SeatBookingDetails sb
    LEFT JOIN dbo.CustomerRegistration cr ON cr.CustomerID = sb.CustomerID
    LEFT JOIN dbo.CustomerTimeSelection ct ON ct.SeatBookingDetailsID = sb.SeatBookingDetailsID
    LEFT JOIN dbo.TimeAllocation ta ON ta.TimeAllocationID = ct.TimeAllocationID
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingDate = @Date AND sb.BookingStatusMasterID = 2
    GROUP BY sb.SeatBookingDetailsID, cr.FirstName, cr.LastName, cr.MobileNo, sb.BookingDate, sb.SeatCount, sb.CompletionStatus
    ORDER BY MIN(ta.FullTiming), sb.SeatBookingDetailsID;
    ;WITH Dates AS (SELECT DATEADD(day, v.n - 6, @Date) AS BookingDate FROM (VALUES(0),(1),(2),(3),(4),(5),(6)) v(n))
    SELECT d.BookingDate, COUNT(sb.SeatBookingDetailsID) AS BookingCount FROM Dates d
    LEFT JOIN dbo.SeatBookingDetails sb ON sb.SalonMasterID = @SalonID AND sb.BookingDate = d.BookingDate AND sb.BookingStatusMasterID = 2
    GROUP BY d.BookingDate ORDER BY d.BookingDate;
    SELECT TOP(4) h.HairCut_Item_ID AS HairCutItemID, MAX(h.HairCut_Item_Name) AS ServiceName,
        SUM(cb.HairCut_Item_Quantity) AS BookingCount, SUM(cb.HairCut_Item_Total) AS Revenue
    FROM dbo.HairCut_Item_Customer_Bill cb
    JOIN dbo.HairCut_Item_List h ON h.HairCut_Item_ID = cb.HairCut_Item_ID
    JOIN dbo.HairCut_Item_Bill_Master b ON b.UniqueBillID = cb.UniqueBillID
    JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
    WHERE sb.SalonMasterID = @SalonID AND cb.Created_Date >= DATEADD(day, -29, @Date) AND cb.Created_Date < DATEADD(day, 1, @Date)
        AND COALESCE(cb.IsDeleted, 0) = 0 AND COALESCE(b.IsDeleted, 0) = 0
    GROUP BY h.HairCut_Item_ID ORDER BY SUM(cb.HairCut_Item_Quantity) DESC;
    -- The admin screen needs these result sets alongside all six summary values.
    SELECT s.SalonMasterID, s.SalonName, s.LocationMasterID, l.LocationName,
        COALESCE(s.SalonAddress, N'') AS SalonAddress, COALESCE(s.ContactNumber, N'') AS ContactNumber,
        CONVERT(varchar(5), s.OpeningTime, 108) AS OpeningTime,
        CONVERT(varchar(5), s.ClosingTime, 108) AS ClosingTime,
        s.ChairCount, s.WeeklyHoliday, s.Latitude, s.Longitude,
        COALESCE(s.City, N'') AS City, COALESCE(s.Pincode, N'') AS Pincode,
        COALESCE(s.ImagePath, N'') AS ImagePath, COALESCE(o.EmailAddress, N'') AS OwnerEmailAddress,
        s.IsVisibleToCustomers
    FROM dbo.SalonOwner o
    JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
    JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1;
    SELECT SalonServiceID, ServiceName, Category, Price, DurationMinutes,
        COALESCE(Description, N'') AS Description, COALESCE(ImagePath, N'') AS ImagePath, IsActive
    FROM dbo.SalonService WHERE SalonMasterID = @SalonID ORDER BY IsActive DESC, ServiceName;
END;
GO
