-- =====================================================================================
-- Salonexa Admin API - database update, PART 1 of 2 (2026-11-14)
-- Fixes the admin app's Services tab and service editing, the dashboard date range,
-- owner signup and salon details. Safe to run again.
-- Changes shared with the customer app: dbo.HairCut_Item_List.HairCut_Item_Price becomes
-- decimal(10,2) (same values), and legacy owner services are copied once into the service menu.
-- =====================================================================================
-- BEFORE YOU RUN THIS FILE, take a backup of the database you are connected to:
--
--   BACKUP DATABASE [<your database name>]
--   TO DISK = N'D:\Backups\<your database name>_20261114.bak'
--   WITH COPY_ONLY, INIT, COMPRESSION;
--
-- HOW TO RUN: in SSMS choose the live database in the toolbar, open this file, press F5 once.
-- The first step checks the backup and the prerequisites. If it prints PREFLIGHT FAILED, nothing
-- is changed and the rest of the file is skipped. If you see a red error message in the middle of
-- a run, stop and send the full Messages tab. Every statement is safe to run again.

SET NOCOUNT ON;
SET NOEXEC OFF;
SET CONTEXT_INFO 0x00;
GO
BEGIN TRY
    DECLARE @SkipBackupCheck bit = 0;   -- set to 1 ONLY if you backed up by another method
    DECLARE @problems nvarchar(max) = N'';
    PRINT N'Server: ' + @@SERVERNAME + N'    Database: ' + DB_NAME();
    IF @SkipBackupCheck = 0 AND NOT EXISTS (
        SELECT 1 FROM msdb.dbo.backupset
        WHERE database_name = DB_NAME() AND type = 'D' AND backup_finish_date > DATEADD(HOUR, -24, GETDATE()))
        SET @problems += N'- No full backup of this database in the last 24 hours. Take one first (see the top of this file).' + NCHAR(10);
    IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL SET @problems += N'- Table dbo.SalonMaster is missing.' + NCHAR(10);
    IF OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL SET @problems += N'- Table dbo.SalonOwner is missing.' + NCHAR(10);
    IF OBJECT_ID(N'dbo.LocationMaster', N'U') IS NULL SET @problems += N'- Table dbo.LocationMaster is missing.' + NCHAR(10);
    IF OBJECT_ID(N'dbo.HairCut_Item_List', N'U') IS NULL SET @problems += N'- Table dbo.HairCut_Item_List is missing.' + NCHAR(10);
    IF OBJECT_ID(N'dbo.HairCut_GroupMaster', N'U') IS NULL SET @problems += N'- Table dbo.HairCut_GroupMaster is missing.' + NCHAR(10);
    IF OBJECT_ID(N'dbo.SeatBookingDetails', N'U') IS NULL SET @problems += N'- Table dbo.SeatBookingDetails is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
        SET @problems += N'- dbo.SeatBookingDetails.CompletionStatus is missing (the QR completion update was never installed).' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'OpeningTime') IS NULL SET @problems += N'- dbo.SalonMaster.OpeningTime is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'ClosingTime') IS NULL SET @problems += N'- dbo.SalonMaster.ClosingTime is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'ChairCount') IS NULL SET @problems += N'- dbo.SalonMaster.ChairCount is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'WeeklyHoliday') IS NULL SET @problems += N'- dbo.SalonMaster.WeeklyHoliday is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'Latitude') IS NULL SET @problems += N'- dbo.SalonMaster.Latitude is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'Longitude') IS NULL SET @problems += N'- dbo.SalonMaster.Longitude is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'IsVisibleToCustomers') IS NULL SET @problems += N'- dbo.SalonMaster.IsVisibleToCustomers is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'City') IS NULL SET @problems += N'- dbo.SalonMaster.City is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'Pincode') IS NULL SET @problems += N'- dbo.SalonMaster.Pincode is missing.' + NCHAR(10);
    IF COL_LENGTH(N'dbo.SalonMaster', N'ImagePath') IS NULL SET @problems += N'- dbo.SalonMaster.ImagePath is missing.' + NCHAR(10);
    DECLARE @legacy int = 0;
    IF OBJECT_ID(N'dbo.SalonService', N'U') IS NOT NULL
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.SalonService', N'@n int OUTPUT', @n = @legacy OUTPUT;
    PRINT N'Info: legacy owner services (dbo.SalonService) that will be copied once into the shared service menu: ' + CAST(@legacy AS nvarchar(12));
    PRINT N'Info: these copies also appear in the customer app menu, which is not filtered by salon.';
    IF @problems <> N''
    BEGIN
        PRINT N'PREFLIGHT FAILED - nothing was changed. Fix the following and run the file again:';
        PRINT @problems;
        SET CONTEXT_INFO 0xFA11;
        SET NOEXEC ON;
    END
    ELSE PRINT N'Preflight OK. Starting the update...';
END TRY
BEGIN CATCH
    PRINT N'PREFLIGHT FAILED - nothing was changed: ' + ERROR_MESSAGE();
    SET CONTEXT_INFO 0xFA11;
    SET NOEXEC ON;
END CATCH
GO
-- ===== 1/4 Service editing: view, procedures, legacy migration (Database/SalonOwnerServices.sql) =====
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
GO
-- ===== 2/4 Owner dashboard (Database/SalonOwnerDashboard.sql) =====
SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
    THROW 50003, 'Run BookingQrWorkflow.sql before SalonOwnerDashboard.sql.', 1;
IF OBJECT_ID(N'dbo.SalonOwnerServiceCatalogue', N'V') IS NULL OR OBJECT_ID(N'dbo.GetSalonOwnerServices', N'P') IS NULL
    THROW 50003, 'Run SalonOwnerServices.sql before SalonOwnerDashboard.sql.', 1;
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
        CompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Completed' THEN 1 END),
        TotalBookings = COUNT(*),
        TotalPendingBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Pending' THEN 1 END),
        TotalCompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Completed' THEN 1 END),
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
    -- Same editable catalogue as GET /api/Admin/services (Database/SalonOwnerServices.sql).
    EXEC dbo.GetSalonOwnerServices @OwnerID = @OwnerID, @SalonID = @SalonID;
END;
GO
GO
-- ===== 3/4 Owner signup (Database/SalonOwnerSignup.sql) =====
-- Salon owner signup (2026-11-14). Back up the database first. Re-runnable.
-- Defines dbo.GetSalonOwnerSignupLocations and dbo.RegisterSalonOwner exactly as installed on the
-- existing database, so a new server can be built from source control.
-- Needs the salon, location and salon-owner schemas (SalonMaster.sql, LocationMaster.sql,
-- SalonOwnerAuthentication.sql) and the salon columns used by SalonOwnerDashboard.sql.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL OR OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL OR OBJECT_ID(N'dbo.LocationMaster', N'U') IS NULL
    THROW 50003, 'Install the salon, location and salon-owner schemas before SalonOwnerSignup.sql.', 1;
GO
IF COL_LENGTH(N'dbo.SalonMaster', N'IsVisibleToCustomers') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'City') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Pincode') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Latitude') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Longitude') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'ImagePath') IS NULL
    THROW 50003, 'SalonMaster is missing the City, Pincode, map, photo or visibility columns.', 1;
GO
IF OBJECT_ID(N'dbo.GetSalonOwnerSignupLocations', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerSignupLocations AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerSignupLocations
AS
BEGIN
    SET NOCOUNT ON;

    SELECT LocationMasterID, LocationName
    FROM dbo.LocationMaster
    WHERE IsActive = 1
    ORDER BY DisplayOrder, LocationName;
END;
GO
IF OBJECT_ID(N'dbo.RegisterSalonOwner', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.RegisterSalonOwner AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.RegisterSalonOwner
    @SalonOwnerID uniqueidentifier,
    @SalonMasterID uniqueidentifier,
    @OwnerName nvarchar(150),
    @UserName nvarchar(100),
    @PasswordHash nvarchar(512),
    @SalonName nvarchar(200),
    @LocationMasterID uniqueidentifier,
    @EmailAddress nvarchar(254),
    @ContactNumber nvarchar(20),
    @SalonAddress nvarchar(500) = NULL,
    @City nvarchar(150) = NULL,
    @Pincode nvarchar(20) = NULL,
    @Latitude float = NULL,
    @Longitude float = NULL,
    @ImagePath nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Reject malformed postal details before opening a transaction or creating a city.
    IF @City IS NOT NULL
    BEGIN
        SET @City = LTRIM(RTRIM(@City));
        IF @City = N'' OR @Pincode IS NULL OR LEN(@Pincode) <> 6
            OR @Pincode LIKE N'%[^0-9]%' OR LEFT(@Pincode, 1) = N'0'
        BEGIN
            SELECT 1 AS Result; -- InvalidDetails
            RETURN;
        END;
    END;

    -- Result codes match the SignupResult enum in the API.
    BEGIN TRY
        BEGIN TRANSACTION;

        IF EXISTS (SELECT 1 FROM dbo.SalonOwner WITH (UPDLOCK, HOLDLOCK)
                   WHERE UserName = @UserName)
        BEGIN
            ROLLBACK TRANSACTION;
            SELECT 3 AS Result; -- DuplicateUserName
            RETURN;
        END;

        -- Names are reserved across every city, regardless of approval or active status.
        -- Check before resolving a city so rejected registrations create no city records.
        SET @SalonName = LTRIM(RTRIM(@SalonName));
        IF EXISTS (SELECT 1 FROM dbo.SalonMaster WITH (UPDLOCK, HOLDLOCK)
                   WHERE LTRIM(RTRIM(SalonName)) COLLATE Latin1_General_100_CI_AS
                       = @SalonName COLLATE Latin1_General_100_CI_AS)
        BEGIN
            COMMIT TRANSACTION;
            SELECT 4 AS Result; -- DuplicateSalon
            RETURN;
        END;

        IF @City IS NOT NULL
        BEGIN
            DECLARE @CityLocation nvarchar(150) = @City;
            IF LOWER(@City) IN (N'trichy', N'tiruchirappalli', N'tiruchirapalli', N'tiruchchirappalli')
                SET @CityLocation = N'Trichy';
            SET @LocationMasterID = NULL;
            SELECT TOP (1) @LocationMasterID = LocationMasterID
            FROM dbo.LocationMaster WITH (UPDLOCK, HOLDLOCK)
            WHERE LocationName = @CityLocation
                OR (@CityLocation = N'Trichy' AND LOWER(LocationName) IN
                    (N'trichy', N'tiruchirappalli', N'tiruchirapalli', N'tiruchchirappalli'))
            ORDER BY CASE WHEN LocationName = @CityLocation THEN 0 ELSE 1 END, LocationMasterID;
            IF @LocationMasterID IS NULL
            BEGIN
                SET @LocationMasterID = NEWID();
                INSERT dbo.LocationMaster(LocationMasterID, LocationName, IsActive, DisplayOrder, CreatedUtc)
                VALUES(@LocationMasterID, @CityLocation, 1, 0, SYSUTCDATETIME());
            END
            ELSE
                UPDATE dbo.LocationMaster SET IsActive = 1, UpdatedUtc = SYSUTCDATETIME()
                WHERE LocationMasterID = @LocationMasterID AND IsActive = 0;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.LocationMaster WITH (HOLDLOCK)
                       WHERE LocationMasterID = @LocationMasterID AND IsActive = 1)
        BEGIN
            ROLLBACK TRANSACTION;
            SELECT 2 AS Result; -- InvalidLocation
            RETURN;
        END;

        INSERT INTO dbo.SalonMaster
            (SalonMasterID, LocationMasterID, SalonName, SalonAddress, ContactNumber, IsActive, IsVisibleToCustomers, DisplayOrder, CreatedUtc,
             City, Pincode, Latitude, Longitude, ImagePath)
        VALUES
            (@SalonMasterID, @LocationMasterID, @SalonName, @SalonAddress, @ContactNumber, 1, 0, 0, SYSUTCDATETIME(),
             @City, @Pincode, @Latitude, @Longitude, @ImagePath);

        INSERT INTO dbo.SalonOwner
            (SalonOwnerID, SalonMasterID, OwnerName, UserName, PasswordHash, EmailAddress, IsActive, CreatedUtc)
        VALUES
            (@SalonOwnerID, @SalonMasterID, @OwnerName, @UserName, @PasswordHash, @EmailAddress, 1, SYSUTCDATETIME());

        COMMIT TRANSACTION;
        SELECT 0 AS Result; -- Created
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

        IF ERROR_NUMBER() IN (2601, 2627)
            SELECT 5 AS Result; -- DuplicateDetails
        ELSE
            THROW;
    END CATCH;
END;
GO
GO
-- ===== 4/4 Salon details (Database/SalonOwnerSalonProfile.sql) =====
-- Salon details for the signed-in owner (2026-11-14). Back up the database first. Re-runnable.
-- Defines dbo.GetSalonOwnerProfile and dbo.SaveSalonOwnerProfile exactly as installed on the existing
-- database, so a new server can be built from source control.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.SalonMaster', N'U') IS NULL OR OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL OR OBJECT_ID(N'dbo.LocationMaster', N'U') IS NULL
    THROW 50003, 'Install the salon, location and salon-owner schemas before SalonOwnerSalonProfile.sql.', 1;
GO
IF COL_LENGTH(N'dbo.SalonMaster', N'OpeningTime') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'ClosingTime') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'ChairCount') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'WeeklyHoliday') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Latitude') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Longitude') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'City') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'Pincode') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'ImagePath') IS NULL OR COL_LENGTH(N'dbo.SalonMaster', N'IsVisibleToCustomers') IS NULL
    THROW 50003, 'SalonMaster is missing the opening hours, chair, map, photo or visibility columns.', 1;
GO
IF OBJECT_ID(N'dbo.GetSalonOwnerProfile', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerProfile AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerProfile @OwnerID uniqueidentifier, @SalonID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.SalonMasterID, s.SalonName, s.LocationMasterID, l.LocationName,
        COALESCE(s.SalonAddress, '') AS SalonAddress, COALESCE(s.ContactNumber, '') AS ContactNumber,
        CONVERT(varchar(5), s.OpeningTime, 108) AS OpeningTime,
        CONVERT(varchar(5), s.ClosingTime, 108) AS ClosingTime, s.ChairCount, s.WeeklyHoliday, s.Latitude, s.Longitude,
        COALESCE(s.City, '') AS City, COALESCE(s.Pincode, '') AS Pincode,
        COALESCE(s.ImagePath, '') AS ImagePath, COALESCE(o.EmailAddress, '') AS OwnerEmailAddress,
        s.IsVisibleToCustomers
    FROM dbo.SalonOwner o
    JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
    JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1;
END;
GO
IF OBJECT_ID(N'dbo.SaveSalonOwnerProfile', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.SaveSalonOwnerProfile AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.SaveSalonOwnerProfile
    @OwnerID uniqueidentifier, @SalonID uniqueidentifier, @SalonName nvarchar(200), @LocationMasterID uniqueidentifier,
    @SalonAddress nvarchar(500), @ContactNumber nvarchar(20), @OpeningTime time(0), @ClosingTime time(0),
    @ChairCount int, @WeeklyHoliday tinyint = NULL, @Latitude decimal(9,6) = NULL, @Longitude decimal(9,6) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID AND o.IsActive = 1 AND s.IsActive = 1)
        THROW 50013, 'Please sign in again.', 1;
    IF NOT EXISTS(SELECT 1 FROM dbo.LocationMaster WHERE LocationMasterID = @LocationMasterID AND IsActive = 1)
        THROW 50015, 'Select an active location.', 1;
    IF NULLIF(LTRIM(RTRIM(@SalonName)), '') IS NULL OR @OpeningTime IS NULL OR @ClosingTime IS NULL
        OR @ClosingTime <= @OpeningTime OR @ChairCount NOT BETWEEN 1 AND 100
        OR (@WeeklyHoliday IS NOT NULL AND @WeeklyHoliday > 6)
        THROW 50015, 'Please check your salon details.', 1;
    IF (@Latitude IS NULL AND @Longitude IS NOT NULL) OR (@Latitude IS NOT NULL AND @Longitude IS NULL)
        OR @Latitude NOT BETWEEN -90 AND 90 OR @Longitude NOT BETWEEN -180 AND 180
        THROW 50015, 'Select a valid map pin.', 1;
    UPDATE dbo.SalonMaster SET SalonName = @SalonName, LocationMasterID = @LocationMasterID,
        SalonAddress = @SalonAddress, ContactNumber = @ContactNumber, OpeningTime = @OpeningTime,
        ClosingTime = @ClosingTime, ChairCount = @ChairCount, WeeklyHoliday = @WeeklyHoliday, Latitude = @Latitude, Longitude = @Longitude, UpdatedUtc = SYSUTCDATETIME()
    WHERE SalonMasterID = @SalonID;
    EXEC dbo.GetSalonOwnerProfile @OwnerID, @SalonID;
END;
GO
GO
SET NOEXEC OFF;
GO
SET NOCOUNT ON;
IF SUBSTRING(CONTEXT_INFO(), 1, 2) = 0xFA11
BEGIN
    PRINT N'Nothing was changed because the preflight failed (see the message near the top).';
    SET NOEXEC ON;
END
PRINT N'--- Verification (every line must say ok) ---';
SELECT item AS [Database object], CASE WHEN OBJECT_ID(item) IS NULL THEN N'MISSING' ELSE N'ok' END AS [Status]
FROM (VALUES
    (N'dbo.SalonOwnerServiceCatalogue'), (N'dbo.GetSalonOwnerServices'), (N'dbo.SaveSalonOwnerService'),
    (N'dbo.GetSalonOwnerDashboard'), (N'dbo.GetSalonOwnerSignupLocations'), (N'dbo.RegisterSalonOwner'),
    (N'dbo.GetSalonOwnerProfile'), (N'dbo.SaveSalonOwnerProfile')) AS v(item);
SELECT N'HairCut_Item_Price column type' AS [Check], TYPE_NAME(system_type_id) + N'(' + CAST(precision AS nvarchar(5)) + N',' + CAST(scale AS nvarchar(5)) + N')' AS [Value]
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.HairCut_Item_List') AND name = N'HairCut_Item_Price';
-- Smoke test (read only): the service list for the first active owner. Expect Edit-able rows (CanEdit = 1).
DECLARE @o uniqueidentifier, @s uniqueidentifier;
SELECT TOP (1) @o = SalonOwnerID, @s = SalonMasterID FROM dbo.SalonOwner WHERE IsActive = 1 ORDER BY CreatedUtc;
IF @o IS NOT NULL EXEC dbo.GetSalonOwnerServices @OwnerID = @o, @SalonID = @s;
ELSE PRINT N'No active salon owner found to smoke-test with.';
PRINT N'Part 1 finished. If any status above says MISSING, or a red error appeared earlier, stop and send the Messages tab.';
GO
SET NOEXEC OFF;
GO
