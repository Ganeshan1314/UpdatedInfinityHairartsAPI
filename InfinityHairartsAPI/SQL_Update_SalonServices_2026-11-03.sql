-- Salon-scoped haircut catalogue add/edit and matching dashboard.
-- Run after SQL_Update_BookingStatusTabs_2026-10-31.sql.
-- Generated from Database/SalonOwnerServices.sql and Database/SalonOwnerDashboard.sql.
-- Owner services use the salon-linked customer haircut menu for reads and writes.
-- Re-runnable: preserve original item IDs, photos, billing rows and legacy services.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL OR OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL
    OR OBJECT_ID(N'dbo.HairCut_Item_List', N'U') IS NULL OR OBJECT_ID(N'dbo.HairCut_GroupMaster', N'U') IS NULL
    THROW 50003, 'Install the salon owner and haircut catalogue schemas first.', 1;
GO
IF OBJECT_ID(N'dbo.SalonService', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalonService (
        SalonServiceID uniqueidentifier NOT NULL CONSTRAINT PK_SalonService PRIMARY KEY,
        SalonMasterID uniqueidentifier NOT NULL REFERENCES dbo.SalonMaster(SalonMasterID),
        ServiceName nvarchar(150) NOT NULL, Category nvarchar(80) NOT NULL,
        Price decimal(10,2) NOT NULL CHECK (Price >= 0),
        DurationMinutes int NOT NULL CHECK (DurationMinutes BETWEEN 5 AND 480),
        Description nvarchar(1000) NULL, ImagePath nvarchar(200) NOT NULL,
        IsActive bit NOT NULL DEFAULT 1, CreatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(0) NULL,
        CONSTRAINT UQ_SalonService_Name UNIQUE(SalonMasterID, ServiceName)
    );
END;
IF OBJECT_ID(N'dbo.SalonHairCutItem', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalonHairCutItem (
        HairCutItemID uniqueidentifier NOT NULL CONSTRAINT PK_SalonHairCutItem PRIMARY KEY,
        SalonMasterID uniqueidentifier NOT NULL REFERENCES dbo.SalonMaster(SalonMasterID),
        LinkedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_SalonHairCutItem_Salon ON dbo.SalonHairCutItem(SalonMasterID);
END;
GO
-- The legacy source used int prices, which silently rounded decimal form prices.
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.HairCut_Item_List')
    AND name=N'HairCut_Item_Price' AND system_type_id=TYPE_ID(N'int'))
    ALTER TABLE dbo.HairCut_Item_List ALTER COLUMN HairCut_Item_Price decimal(10,2) NULL;
IF COL_LENGTH(N'dbo.SalonHairCutItem', N'DurationMinutes') IS NULL
    ALTER TABLE dbo.SalonHairCutItem ADD DurationMinutes int NULL CHECK (DurationMinutes BETWEEN 5 AND 480);
IF COL_LENGTH(N'dbo.SalonHairCutItem', N'Description') IS NULL
    ALTER TABLE dbo.SalonHairCutItem ADD Description nvarchar(1000) NULL;
IF COL_LENGTH(N'dbo.SalonHairCutItem', N'IsActive') IS NULL
    ALTER TABLE dbo.SalonHairCutItem ADD IsActive bit NOT NULL CONSTRAINT DF_SalonHairCutItem_IsActive DEFAULT 1;
IF COL_LENGTH(N'dbo.SalonHairCutItem', N'CreatedUtc') IS NULL
    ALTER TABLE dbo.SalonHairCutItem ADD CreatedUtc datetime2(0) NULL;
IF COL_LENGTH(N'dbo.SalonHairCutItem', N'UpdatedUtc') IS NULL
    ALTER TABLE dbo.SalonHairCutItem ADD UpdatedUtc datetime2(0) NULL;
GO
-- Move legacy owner services into the same catalogue, once, without deleting their
-- original rows. A cross-salon ID conflict stops deployment instead of reassigning it.
BEGIN TRY
    BEGIN TRANSACTION;
    IF EXISTS(SELECT 1 FROM dbo.SalonService s JOIN dbo.SalonHairCutItem l ON l.HairCutItemID=s.SalonServiceID
        WHERE l.SalonMasterID<>s.SalonMasterID)
        THROW 50015, 'A legacy service ID is linked to another salon. Resolve the conflict before deploying.', 1;
    IF EXISTS(SELECT 1 FROM dbo.SalonService s JOIN dbo.HairCut_Item_List h ON h.HairCut_Item_ID=s.SalonServiceID
        WHERE NOT EXISTS(SELECT 1 FROM dbo.SalonHairCutItem l WHERE l.HairCutItemID=s.SalonServiceID))
        THROW 50015, 'A legacy service ID conflicts with an unassigned haircut item.', 1;
    INSERT dbo.HairCut_GroupMaster(HairCutItemGroupMaster_ID, HairCutItemGroup, OrderID)
    SELECT NEWID(), categories.Category,
        (SELECT COALESCE(MAX(OrderID),1) FROM dbo.HairCut_GroupMaster) + ROW_NUMBER() OVER(ORDER BY categories.Category)
    FROM (SELECT DISTINCT Category FROM dbo.SalonService) categories
    WHERE NOT EXISTS(SELECT 1 FROM dbo.HairCut_GroupMaster g WHERE LTRIM(RTRIM(g.HairCutItemGroup))=categories.Category);
    INSERT dbo.HairCut_Item_List(HairCut_Item_ID, HairCut_Item_Code, HairCut_Item_Name, HairCut_Item_Price,
        HairCut_Item_Quantity, HairCut_Item_CGST, HairCut_Item_SGST, HairCut_Item_Taxable,
        HairCutItemImage, HairCutItemGroupMaster_ID, Created_Date, IsDeleted)
    SELECT s.SalonServiceID,
        (SELECT COALESCE(MAX(HairCut_Item_Code),0) FROM dbo.HairCut_Item_List) + ROW_NUMBER() OVER(ORDER BY s.SalonServiceID),
        s.ServiceName, s.Price, N'1', 0, 0, s.Price, s.ImagePath,
        (SELECT TOP(1) g.HairCutItemGroupMaster_ID FROM dbo.HairCut_GroupMaster g
         WHERE LTRIM(RTRIM(g.HairCutItemGroup))=s.Category ORDER BY g.HairCutItemGroupMaster_ID),
        s.CreatedUtc, CASE WHEN s.IsActive=1 THEN 0 ELSE 1 END
    FROM dbo.SalonService s WHERE NOT EXISTS(SELECT 1 FROM dbo.HairCut_Item_List h WHERE h.HairCut_Item_ID=s.SalonServiceID);
    INSERT dbo.SalonHairCutItem(HairCutItemID, SalonMasterID, DurationMinutes, Description, IsActive, CreatedUtc, UpdatedUtc)
    SELECT SalonServiceID, SalonMasterID, DurationMinutes, Description, IsActive, CreatedUtc, UpdatedUtc
    FROM dbo.SalonService s WHERE NOT EXISTS(SELECT 1 FROM dbo.SalonHairCutItem l WHERE l.HairCutItemID=s.SalonServiceID);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
IF OBJECT_ID(N'dbo.SalonOwnerServiceCatalogue', N'V') IS NULL
    EXEC(N'CREATE VIEW dbo.SalonOwnerServiceCatalogue AS SELECT 1 AS Placeholder');
GO
ALTER VIEW dbo.SalonOwnerServiceCatalogue
AS
    SELECT l.SalonMasterID, h.HairCut_Item_ID AS SalonServiceID,
        LTRIM(RTRIM(h.HairCut_Item_Name)) AS ServiceName,
        COALESCE(NULLIF(LTRIM(RTRIM(g.HairCutItemGroup)), N''), N'Services') AS Category,
        CAST(h.HairCut_Item_Price AS decimal(10,2)) AS Price, l.DurationMinutes,
        COALESCE(l.Description, N'') AS Description,
        CASE WHEN NULLIF(LTRIM(RTRIM(h.HairCutItemImage)), N'') IS NULL THEN N''
             WHEN h.HairCutItemImage LIKE N'/ServiceImages/%' THEN h.HairCutItemImage
             ELSE N'/Images/HairCutItemListImages/' + LTRIM(RTRIM(h.HairCutItemImage)) END AS ImagePath,
        CAST(CASE WHEN l.IsActive=1 AND COALESCE(h.IsDeleted,0)=0 THEN 1 ELSE 0 END AS bit) AS IsActive,
        CAST(CASE WHEN (SELECT COUNT(*) FROM dbo.HairCut_Item_List same WHERE same.HairCut_Item_ID=h.HairCut_Item_ID)=1
             THEN 1 ELSE 0 END AS bit) AS CanEdit,
        COALESCE(l.CreatedUtc, h.Created_Date, l.LinkedUtc) AS CreatedUtc
    FROM dbo.HairCut_Item_List h
    JOIN dbo.SalonHairCutItem l ON l.HairCutItemID=h.HairCut_Item_ID
    LEFT JOIN dbo.HairCut_GroupMaster g ON g.HairCutItemGroupMaster_ID=h.HairCutItemGroupMaster_ID
    WHERE COALESCE(h.IsDeleted,0)=0 OR l.IsActive=0;
GO
IF OBJECT_ID(N'dbo.GetSalonOwnerServices', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerServices AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerServices @OwnerID uniqueidentifier, @SalonID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID=o.SalonMasterID
        WHERE o.SalonOwnerID=@OwnerID AND o.SalonMasterID=@SalonID AND o.IsActive=1 AND s.IsActive=1)
        THROW 50013, 'Please sign in again.', 1;
    SELECT SalonServiceID, ServiceName, Category, Price, DurationMinutes, Description, ImagePath, IsActive, CanEdit
    FROM dbo.SalonOwnerServiceCatalogue WHERE SalonMasterID=@SalonID
    ORDER BY IsActive DESC, Price, ServiceName, SalonServiceID;
END;
GO
IF OBJECT_ID(N'dbo.SaveSalonOwnerService', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.SaveSalonOwnerService AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.SaveSalonOwnerService
    @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @ServiceID uniqueidentifier,
    @ServiceName nvarchar(150), @Category nvarchar(80), @Price decimal(10,2), @DurationMinutes int,
    @Description nvarchar(1000), @ImagePath nvarchar(200)=NULL, @IsActive bit, @IsNew bit
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        -- Serialize saves within a salon to prevent concurrent duplicate names.
        IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s WITH (UPDLOCK,HOLDLOCK)
            ON s.SalonMasterID=o.SalonMasterID
            WHERE o.SalonOwnerID=@OwnerID AND o.SalonMasterID=@SalonID AND o.IsActive=1 AND s.IsActive=1)
            THROW 50013, 'Please sign in again.', 1;
        SET @ServiceName=LTRIM(RTRIM(@ServiceName)); SET @Category=LTRIM(RTRIM(@Category));
        IF NULLIF(@ServiceName,N'') IS NULL OR NULLIF(@Category,N'') IS NULL OR @Price IS NULL OR @Price<0
            OR @DurationMinutes IS NULL OR @DurationMinutes NOT BETWEEN 5 AND 480 OR @IsActive IS NULL OR @IsNew IS NULL
            THROW 50015, 'Please check your service details.', 1;
        IF @IsNew=0 AND NOT EXISTS(SELECT 1 FROM dbo.SalonOwnerServiceCatalogue
            WHERE SalonServiceID=@ServiceID AND SalonMasterID=@SalonID AND CanEdit=1)
            THROW 50016, 'Service not found in your salon.', 1;
        IF @IsNew=1 AND (EXISTS(SELECT 1 FROM dbo.HairCut_Item_List WHERE HairCut_Item_ID=@ServiceID)
            OR EXISTS(SELECT 1 FROM dbo.SalonHairCutItem WHERE HairCutItemID=@ServiceID))
            THROW 50015, 'This service ID is already used.', 1;
        IF EXISTS(SELECT 1 FROM dbo.SalonOwnerServiceCatalogue
            WHERE SalonMasterID=@SalonID AND ServiceName=@ServiceName AND SalonServiceID<>@ServiceID)
            THROW 50017, 'A service with this name already exists in your salon.', 1;
        IF @IsNew=1 AND NULLIF(@ImagePath,N'') IS NULL THROW 50015, 'Upload a service image.', 1;
        DECLARE @GroupID uniqueidentifier;
        SELECT TOP(1) @GroupID=HairCutItemGroupMaster_ID FROM dbo.HairCut_GroupMaster WITH (UPDLOCK,HOLDLOCK)
        WHERE LTRIM(RTRIM(HairCutItemGroup))=@Category ORDER BY HairCutItemGroupMaster_ID;
        IF @GroupID IS NULL
        BEGIN
            SET @GroupID=NEWID();
            DECLARE @GroupOrder int=(SELECT COALESCE(MAX(OrderID),1)+1 FROM dbo.HairCut_GroupMaster WITH (UPDLOCK,HOLDLOCK));
            INSERT dbo.HairCut_GroupMaster(HairCutItemGroupMaster_ID,HairCutItemGroup,OrderID) VALUES(@GroupID,@Category,@GroupOrder);
        END;
        DECLARE @ImageName nvarchar(200)=CASE WHEN @ImagePath LIKE N'/Images/HairCutItemListImages/%'
            THEN SUBSTRING(@ImagePath,LEN(N'/Images/HairCutItemListImages/')+1,200) ELSE @ImagePath END;
        IF @IsNew=1
        BEGIN
            DECLARE @Code int=(SELECT COALESCE(MAX(HairCut_Item_Code),0)+1 FROM dbo.HairCut_Item_List WITH (UPDLOCK,HOLDLOCK));
            INSERT dbo.HairCut_Item_List(HairCut_Item_ID,HairCut_Item_Code,HairCut_Item_Name,HairCut_Item_Price,
                HairCut_Item_Quantity,HairCut_Item_CGST,HairCut_Item_SGST,HairCut_Item_Taxable,
                HairCutItemImage,HairCutItemGroupMaster_ID,Created_Date,Created_By,IsDeleted)
            VALUES(@ServiceID,@Code,@ServiceName,@Price,N'1',0,0,@Price,@ImageName,@GroupID,SYSUTCDATETIME(),@OwnerID,1-@IsActive);
            INSERT dbo.SalonHairCutItem(HairCutItemID,SalonMasterID,DurationMinutes,Description,IsActive,CreatedUtc)
            VALUES(@ServiceID,@SalonID,@DurationMinutes,@Description,@IsActive,SYSUTCDATETIME());
        END
        ELSE
        BEGIN
            -- Preserve existing tax proportions when the price changes. Billing history is untouched.
            UPDATE h SET HairCut_Item_Name=@ServiceName, HairCut_Item_Price=@Price,
                HairCut_Item_CGST=CASE WHEN @Price=h.HairCut_Item_Price THEN h.HairCut_Item_CGST WHEN h.HairCut_Item_Price>0 THEN ROUND(COALESCE(h.HairCut_Item_CGST,0)/h.HairCut_Item_Price*@Price,2) ELSE 0 END,
                HairCut_Item_SGST=CASE WHEN @Price=h.HairCut_Item_Price THEN h.HairCut_Item_SGST WHEN h.HairCut_Item_Price>0 THEN ROUND(COALESCE(h.HairCut_Item_SGST,0)/h.HairCut_Item_Price*@Price,2) ELSE 0 END,
                HairCut_Item_Taxable=CASE WHEN @Price=h.HairCut_Item_Price THEN h.HairCut_Item_Taxable ELSE @Price
                    - CASE WHEN h.HairCut_Item_Price>0 THEN ROUND(COALESCE(h.HairCut_Item_CGST,0)/h.HairCut_Item_Price*@Price,2) ELSE 0 END
                    - CASE WHEN h.HairCut_Item_Price>0 THEN ROUND(COALESCE(h.HairCut_Item_SGST,0)/h.HairCut_Item_Price*@Price,2) ELSE 0 END END,
                HairCutItemImage=COALESCE(@ImageName,h.HairCutItemImage), HairCutItemGroupMaster_ID=@GroupID, IsDeleted=1-@IsActive
            FROM dbo.HairCut_Item_List h JOIN dbo.SalonHairCutItem l ON l.HairCutItemID=h.HairCut_Item_ID
            WHERE h.HairCut_Item_ID=@ServiceID AND l.SalonMasterID=@SalonID;
            IF @@ROWCOUNT<>1 THROW 50016, 'Service not found in your salon.', 1;
            UPDATE dbo.SalonHairCutItem SET DurationMinutes=@DurationMinutes,Description=@Description,
                IsActive=@IsActive,UpdatedUtc=SYSUTCDATETIME() WHERE HairCutItemID=@ServiceID AND SalonMasterID=@SalonID;
        END;
        SELECT SalonServiceID,ServiceName,Category,Price,DurationMinutes,Description,ImagePath,IsActive,CanEdit
        FROM dbo.SalonOwnerServiceCatalogue WHERE SalonServiceID=@ServiceID AND SalonMasterID=@SalonID;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
    THROW 50003, 'Run BookingQrWorkflow.sql before SalonOwnerDashboard.sql.', 1;
GO

IF OBJECT_ID(N'dbo.GetSalonOwnerDashboard', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerDashboard AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerDashboard @OwnerID uniqueidentifier, @SalonID uniqueidentifier,
    @Date date = NULL, @FromDate date = NULL, @ToDate date = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1)
        THROW 50013, 'Please sign in again.', 1;
    DECLARE @RangeStart date = COALESCE(@FromDate, @ToDate, @Date, CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME())));
    DECLARE @RangeEnd date = COALESCE(@ToDate, @FromDate, @Date, @RangeStart);
    IF @RangeStart < '20000101' OR @RangeEnd > '21001231' OR @RangeStart > @RangeEnd
        THROW 50002, 'Choose dates between 2000 and 2100, with From date on or before To date.', 1;
    -- Service creation timestamps are UTC; date controls represent India calendar days.
    DECLARE @ServiceStartUtc datetime2 = DATEADD(minute, -330, CAST(@RangeStart AS datetime2));
    DECLARE @ServiceEndUtc datetime2 = DATEADD(minute, -330, DATEADD(day, 1, CAST(@RangeEnd AS datetime2)));
    -- CompletionStatus is saved atomically with the appointment QR audit row.
    SELECT TodayBookings = COUNT(*),
        TodaySeats = COALESCE(SUM(sb.SeatCount), 0),
        PendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Pending' THEN 1 END),
        InProgressBookings = COUNT(CASE WHEN sb.CompletionStatus = N'InProgress' THEN 1 END),
        CompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Completed' THEN 1 END),
        TotalBookings = COUNT(*),
        TotalPendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Pending' THEN 1 END),
        TotalInProgressBookings = COUNT(CASE WHEN sb.CompletionStatus = N'InProgress' THEN 1 END),
        TotalCompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Completed' THEN 1 END),
        CompletedRevenue = (SELECT COALESCE(SUM(b.BillAmount), 0)
            FROM dbo.BookingBillAmounts b
            JOIN dbo.SeatBookingDetails completed ON completed.SeatBookingDetailsID = b.SeatBookingDetailsID
            WHERE completed.SalonMasterID = @SalonID AND completed.BookingStatusMasterID = 2
                AND completed.CompletionStatus = N'Completed'
                AND completed.BookingDate >= @RangeStart AND completed.BookingDate < DATEADD(day, 1, @RangeEnd)),
        TodayRevenue = (SELECT COALESCE(SUM(b.BillIDTotalAmount), 0) FROM dbo.HairCut_Item_Bill_Master b
            JOIN dbo.SeatBookingDetails sb ON sb.SeatBookingDetailsID = b.SeatBookingDetailsID
            WHERE sb.SalonMasterID = @SalonID AND b.CreatedDate >= @RangeStart AND b.CreatedDate < DATEADD(day, 1, @RangeEnd) AND COALESCE(b.IsDeleted, 0) = 0),
        TotalCustomers = COUNT(DISTINCT sb.CustomerID),
        ActiveServices = (SELECT COUNT(*) FROM dbo.SalonOwnerServiceCatalogue WHERE SalonMasterID = @SalonID AND IsActive = 1
            AND CreatedUtc >= @ServiceStartUtc AND CreatedUtc < @ServiceEndUtc),
        UpcomingBookings = COUNT(CASE WHEN sb.BookingDate >= CONVERT(date, DATEADD(minute, 330, SYSUTCDATETIME()))
            AND sb.CompletionStatus = N'Pending' THEN 1 END)
    FROM dbo.SeatBookingDetails sb
    WHERE sb.SalonMasterID = @SalonID AND sb.BookingStatusMasterID = 2
        AND sb.BookingDate >= @RangeStart AND sb.BookingDate < DATEADD(day, 1, @RangeEnd);
    SELECT TOP(30) sb.SeatBookingDetailsID,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(cr.FirstName, ' ', cr.LastName))), ''), 'Guest customer') AS CustomerName,
        COALESCE(cr.MobileNo, '') AS MobileNo, sb.BookingDate,
        MIN(ta.FullTiming) AS StartTime, COALESCE(sb.SeatCount, 0) AS SeatCount,
        sb.CompletionStatus AS BookingStatus,
        CASE WHEN sb.CompletionStatus = N'Completed' THEN
            (SELECT bill.BillAmount FROM dbo.BookingBillAmounts bill
             WHERE bill.SeatBookingDetailsID = sb.SeatBookingDetailsID)
        END AS BillAmount
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
    EXEC dbo.GetSalonOwnerServices @OwnerID=@OwnerID, @SalonID=@SalonID;
END;
GO
