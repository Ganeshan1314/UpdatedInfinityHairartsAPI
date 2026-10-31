/*
Dashboard counts for the selected From/To range - 28 October 2026.
Run after SQL_Update_DashboardDateRange_2026-10-27.sql in InfinityHairArtsAdmin.
Both dates are inclusive. Customers use confirmed booking dates; active services
use creation dates in India time. Activity and billed services use the same range.
Only the dashboard procedure changes; existing application records are unchanged.
Source: Database/SalonOwnerDashboard.sql.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
IF @@TRANCOUNT <> 0
    THROW 51010, 'Run this file without an open transaction.', 1;
IF DB_NAME() <> N'InfinityHairArtsAdmin'
    THROW 51010, 'Select InfinityHairArtsAdmin before running this file.', 1;
IF OBJECT_ID(N'dbo.GetSalonOwnerDashboard', N'P') IS NULL
    THROW 50003, 'Apply the existing dashboard migrations first.', 1;
IF COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
    THROW 50003, 'Apply the QR completion migration first.', 1;
IF COL_LENGTH(N'dbo.SalonService', N'CreatedUtc') IS NULL
    THROW 50003, 'The salon service creation timestamp is required.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    EXEC sys.sp_executesql N'ALTER PROCEDURE dbo.GetSalonOwnerDashboard @OwnerID uniqueidentifier, @SalonID uniqueidentifier,
    @Date date = NULL, @FromDate date = NULL, @ToDate date = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1)
        THROW 50013, ''Please sign in again.'', 1;
    DECLARE @RangeStart date = COALESCE(@FromDate, @ToDate, @Date, CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME())));
    DECLARE @RangeEnd date = COALESCE(@ToDate, @FromDate, @Date, @RangeStart);
    IF @RangeStart < ''20000101'' OR @RangeEnd > ''21001231'' OR @RangeStart > @RangeEnd
        THROW 50002, ''Choose dates between 2000 and 2100, with From date on or before To date.'', 1;
    -- Service creation timestamps are UTC; date controls represent India calendar days.
    DECLARE @ServiceStartUtc datetime2 = DATEADD(minute, -330, CAST(@RangeStart AS datetime2));
    DECLARE @ServiceEndUtc datetime2 = DATEADD(minute, -330, DATEADD(day, 1, CAST(@RangeEnd AS datetime2)));
    -- CompletionStatus is saved atomically with the appointment QR audit row.
    SELECT TodayBookings = COUNT(*),
        TodaySeats = COALESCE(SUM(sb.SeatCount), 0),
        PendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N''Pending'' THEN 1 END),
        CompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N''Completed'' THEN 1 END),
        TotalBookings = COUNT(*),
        TotalPendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N''Pending'' THEN 1 END),
        TotalCompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N''Completed'' THEN 1 END),
        TodayRevenue = (SELECT COALESCE(SUM(b.BillIDTotalAmount), 0) FROM dbo.HairCut_Item_Bill_Master b
            JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
            WHERE sb.SalonMasterID = @SalonID AND b.CreatedDate >= @RangeStart AND b.CreatedDate < DATEADD(day, 1, @RangeEnd) AND COALESCE(b.IsDeleted, 0) = 0),
        TotalCustomers = COUNT(DISTINCT sb.CustomerID),
        ActiveServices = (SELECT COUNT(*) FROM dbo.SalonService WHERE SalonMasterID = @SalonID AND IsActive = 1
            AND CreatedUtc >= @ServiceStartUtc AND CreatedUtc < @ServiceEndUtc),
        UpcomingBookings = COUNT(CASE WHEN sb.BookingDate >= CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME()))
            AND sb.CompletionStatus = N''Pending'' THEN 1 END)
    FROM dbo.SeatBookingDetails sb
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingStatusMasterID = 2
        AND sb.BookingDate >= @RangeStart AND sb.BookingDate < DATEADD(day, 1, @RangeEnd);
    SELECT TOP(30) sb.SeatBookingDetailsID,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(cr.FirstName, '' '', cr.LastName))), ''''), ''Guest customer'') AS CustomerName,
        COALESCE(cr.MobileNo, '''') AS MobileNo, sb.BookingDate,
        MIN(ta.FullTiming) AS StartTime, COALESCE(sb.SeatCount, 0) AS SeatCount,
        sb.CompletionStatus AS BookingStatus
    FROM dbo.SeatBookingDetails sb
    LEFT JOIN dbo.CustomerRegistration cr ON cr.CustomerID = sb.CustomerID
    LEFT JOIN dbo.CustomerTimeSelection ct ON ct.SeatBookingDetailsID = sb.SeatBookingDetailsID
    LEFT JOIN dbo.TimeAllocation ta ON ta.TimeAllocationID = ct.TimeAllocationID
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingDate >= @RangeStart AND sb.BookingDate < DATEADD(day, 1, @RangeEnd) AND sb.BookingStatusMasterID = 2
    GROUP BY sb.SeatBookingDetailsID, cr.FirstName, cr.LastName, cr.MobileNo, sb.BookingDate, sb.SeatCount, sb.CompletionStatus
    ORDER BY sb.BookingDate, MIN(ta.FullTiming), sb.SeatBookingDetailsID;
    ;WITH Dates AS (
        SELECT @RangeStart AS BookingDate
        UNION ALL
        SELECT DATEADD(day, 1, BookingDate) FROM Dates WHERE BookingDate < @RangeEnd
    )
    SELECT d.BookingDate, COUNT(sb.SeatBookingDetailsID) AS BookingCount FROM Dates d
    LEFT JOIN dbo.SeatBookingDetails sb ON sb.SalonMasterID = @SalonID AND sb.BookingDate = d.BookingDate AND sb.BookingStatusMasterID = 2
    GROUP BY d.BookingDate ORDER BY d.BookingDate OPTION (MAXRECURSION 0);
    SELECT TOP(4) h.HairCut_Item_ID AS HairCutItemID, MAX(h.HairCut_Item_Name) AS ServiceName,
        SUM(cb.HairCut_Item_Quantity) AS BookingCount, SUM(cb.HairCut_Item_Total) AS Revenue
    FROM dbo.HairCut_Item_Customer_Bill cb
    JOIN dbo.HairCut_Item_List h ON h.HairCut_Item_ID = cb.HairCut_Item_ID
    JOIN dbo.HairCut_Item_Bill_Master b ON b.UniqueBillID = cb.UniqueBillID
    JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
    WHERE sb.SalonMasterID = @SalonID AND cb.Created_Date >= @RangeStart AND cb.Created_Date < DATEADD(day, 1, @RangeEnd)
        AND COALESCE(cb.IsDeleted, 0) = 0 AND COALESCE(b.IsDeleted, 0) = 0
    GROUP BY h.HairCut_Item_ID ORDER BY SUM(cb.HairCut_Item_Quantity) DESC;
    -- The admin screen needs these result sets alongside all six summary values.
    SELECT s.SalonMasterID, s.SalonName, s.LocationMasterID, l.LocationName,
        COALESCE(s.SalonAddress, N'''') AS SalonAddress, COALESCE(s.ContactNumber, N'''') AS ContactNumber,
        CONVERT(varchar(5), s.OpeningTime, 108) AS OpeningTime,
        CONVERT(varchar(5), s.ClosingTime, 108) AS ClosingTime,
        s.ChairCount, s.WeeklyHoliday, s.Latitude, s.Longitude,
        COALESCE(s.City, N'''') AS City, COALESCE(s.Pincode, N'''') AS Pincode,
        COALESCE(s.ImagePath, N'''') AS ImagePath, COALESCE(o.EmailAddress, N'''') AS OwnerEmailAddress,
        s.IsVisibleToCustomers
    FROM dbo.SalonOwner o
    JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
    JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1;
    SELECT SalonServiceID, ServiceName, Category, Price, DurationMinutes,
        COALESCE(Description, N'''') AS Description, COALESCE(ImagePath, N'''') AS ImagePath, IsActive
    FROM dbo.SalonService WHERE SalonMasterID = @SalonID ORDER BY IsActive DESC, ServiceName;
END;';
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
SELECT N'Dashboard range counts update completed successfully' AS Result, DB_NAME() AS DatabaseName;
