-- Run against the application's database after SalonMaster, SalonOwnerAuthentication
-- and BookingQrWorkflow. Rerunnable; existing customer visibility is preserved.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.SalonOwner', N'U') IS NULL OR COL_LENGTH(N'dbo.SeatBookingDetails', N'CompletionStatus') IS NULL
    THROW 50040, 'Deploy SalonOwnerAuthentication.sql and BookingQrWorkflow.sql first.', 1;
GO
IF COL_LENGTH(N'dbo.SalonMaster', N'IsVisibleToCustomers') IS NULL
    ALTER TABLE dbo.SalonMaster ADD IsVisibleToCustomers bit NOT NULL
        CONSTRAINT DF_SalonMaster_IsVisibleToCustomers DEFAULT(0) WITH VALUES;
GO
-- Existing installations may have a visible-by-default constraint (or no default).
-- New salons must wait for approval; changing the default preserves existing rows.
DECLARE @VisibilityDefault sysname;
SELECT @VisibilityDefault = dc.name
FROM sys.default_constraints dc
INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
WHERE dc.parent_object_id = OBJECT_ID(N'dbo.SalonMaster') AND c.name = N'IsVisibleToCustomers';
IF @VisibilityDefault IS NOT NULL
BEGIN
    DECLARE @DropVisibilityDefault nvarchar(400) = N'ALTER TABLE dbo.SalonMaster DROP CONSTRAINT ' + QUOTENAME(@VisibilityDefault);
    EXEC sys.sp_executesql @DropVisibilityDefault;
END;
ALTER TABLE dbo.SalonMaster ADD CONSTRAINT DF_SalonMaster_IsVisibleToCustomers DEFAULT(0) FOR IsVisibleToCustomers;
GO
IF OBJECT_ID(N'dbo.SuperAdminSalonAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SuperAdminSalonAudit (
        AuditID bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SalonMasterID uniqueidentifier NOT NULL,
        SalonName nvarchar(200) NOT NULL,
        ActorUserName nvarchar(100) NOT NULL,
        IsVisibleToCustomers bit NOT NULL,
        CreatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
GO
IF OBJECT_ID(N'dbo.GetSuperAdminOverview', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSuperAdminOverview AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSuperAdminOverview @FromDate date, @ToDate date
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL OR @ToDate IS NULL OR @FromDate < '20000101' OR @ToDate > '21001231' OR @FromDate > @ToDate
        THROW 50041, 'Invalid dashboard date range.', 1;
    SELECT TotalSalons = (SELECT COUNT(*) FROM dbo.SalonMaster),
        VisibleSalons = (SELECT COUNT(*) FROM dbo.SalonMaster WHERE IsActive = 1 AND IsVisibleToCustomers = 1),
        AwaitingApproval = (SELECT COUNT(*) FROM dbo.SalonMaster WHERE IsActive = 1 AND IsVisibleToCustomers = 0),
        ActiveOwners = (SELECT COUNT(*) FROM dbo.SalonOwner WHERE IsActive = 1),
        Bookings = COUNT(*),
        CompletedBookings = COUNT(CASE WHEN sb.CompletionStatus = N'Completed' THEN 1 END),
        Customers = COUNT(DISTINCT sb.CustomerID),
        BilledAmount = CAST((SELECT COALESCE(SUM(b.BillIDTotalAmount), 0) FROM dbo.HairCut_Item_Bill_Master b
            WHERE b.CreatedDate >= @FromDate AND b.CreatedDate < DATEADD(day, 1, @ToDate)
                AND COALESCE(b.IsDeleted, 0) = 0) AS decimal(18,2))
    FROM dbo.SeatBookingDetails sb
    WHERE sb.BookingStatusMasterID = 2 AND sb.BookingDate >= @FromDate AND sb.BookingDate < DATEADD(day, 1, @ToDate);
    ;WITH Days AS (SELECT DATEADD(day, n - 6, @ToDate) BookingDate FROM (VALUES(0),(1),(2),(3),(4),(5),(6)) v(n))
    SELECT d.BookingDate, COUNT(sb.SeatBookingDetailsID) BookingCount FROM Days d
    LEFT JOIN dbo.SeatBookingDetails sb ON sb.BookingDate >= d.BookingDate AND sb.BookingDate < DATEADD(day, 1, d.BookingDate)
        AND sb.BookingStatusMasterID = 2
    GROUP BY d.BookingDate ORDER BY d.BookingDate;
    SELECT TOP(50) AuditID, SalonMasterID, SalonName, ActorUserName, IsVisibleToCustomers, CreatedUtc
    FROM dbo.SuperAdminSalonAudit ORDER BY AuditID DESC;
END;
GO
IF OBJECT_ID(N'dbo.GetSuperAdminSalons', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSuperAdminSalons AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSuperAdminSalons @Search nvarchar(100) = N'', @Status varchar(10) = 'all', @Page int = 1, @PageSize int = 20
AS
BEGIN
    SET NOCOUNT ON;
    IF @Page < 1 OR @Page > 100000 OR @PageSize < 1 OR @PageSize > 100 OR @Status NOT IN ('all', 'pending', 'visible', 'inactive')
        THROW 50042, 'Invalid salon filter.', 1;
    -- One directory row per salon, even when multiple owner records exist.
    SELECT s.SalonMasterID, s.SalonName, COALESCE(l.LocationName, N'') City,
        COALESCE(s.SalonAddress, N'') SalonAddress, COALESCE(s.ContactNumber, N'') ContactNumber,
        COALESCE(o.OwnerName, N'') OwnerName, COALESCE(o.EmailAddress, N'') EmailAddress,
        s.IsActive, s.IsVisibleToCustomers
    INTO #Salons
    FROM dbo.SalonMaster s
    LEFT JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    OUTER APPLY (SELECT TOP(1) OwnerName, EmailAddress FROM dbo.SalonOwner
        WHERE SalonMasterID = s.SalonMasterID ORDER BY IsActive DESC, CreatedUtc, SalonOwnerID) o
    WHERE (@Search = N'' OR CHARINDEX(@Search, s.SalonName) > 0 OR CHARINDEX(@Search, l.LocationName) > 0
        OR CHARINDEX(@Search, o.OwnerName) > 0 OR CHARINDEX(@Search, o.EmailAddress) > 0)
        AND (@Status = 'all' OR (@Status = 'pending' AND s.IsActive = 1 AND s.IsVisibleToCustomers = 0)
            OR (@Status = 'visible' AND s.IsActive = 1 AND s.IsVisibleToCustomers = 1)
            OR (@Status = 'inactive' AND s.IsActive = 0));
    SELECT COUNT(*) FROM #Salons;
    SELECT * FROM #Salons ORDER BY SalonName, SalonMasterID OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
IF OBJECT_ID(N'dbo.SetSuperAdminSalonVisibility', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.SetSuperAdminSalonVisibility AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.SetSuperAdminSalonVisibility @SalonID uniqueidentifier, @Visible bit, @ExpectedVisible bit, @Actor nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NULLIF(LTRIM(RTRIM(@Actor)), N'') IS NULL OR @Visible IS NULL OR @ExpectedVisible IS NULL
        THROW 50043, 'A validated administrator and visibility choice are required.', 1;
    BEGIN TRANSACTION;
    DECLARE @Active bit, @Current bit, @Name nvarchar(200);
    SELECT @Active = IsActive, @Current = IsVisibleToCustomers, @Name = SalonName
        FROM dbo.SalonMaster WITH (UPDLOCK, HOLDLOCK) WHERE SalonMasterID = @SalonID;
    IF @Name IS NULL
    BEGIN
        ROLLBACK;
        THROW 50044, 'This salon no longer exists.', 1;
    END;
    IF @Current <> @ExpectedVisible
    BEGIN
        ROLLBACK;
        THROW 50046, 'Visibility changed since this salon was loaded. Refresh and try again.', 1;
    END;
    IF @Visible = 1 AND @Active = 0
    BEGIN
        ROLLBACK;
        THROW 50045, 'An inactive salon cannot be approved.', 1;
    END;
    IF @Current <> @Visible
    BEGIN
        UPDATE dbo.SalonMaster SET IsVisibleToCustomers = @Visible, UpdatedUtc = SYSUTCDATETIME() WHERE SalonMasterID = @SalonID;
        INSERT dbo.SuperAdminSalonAudit(SalonMasterID, SalonName, ActorUserName, IsVisibleToCustomers)
            VALUES(@SalonID, @Name, @Actor, @Visible);
    END;
    COMMIT;
END;
GO
-- Customer listings use the same persisted visibility flag as the dashboard.
IF OBJECT_ID(N'dbo.getActiveSalonsByLocation', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.getActiveSalonsByLocation AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.getActiveSalonsByLocation @LocationMasterID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT SalonMasterID, LocationMasterID, SalonName, COALESCE(SalonAddress, N'') SalonAddress,
        COALESCE(ContactNumber, N'') ContactNumber
    FROM dbo.SalonMaster WHERE LocationMasterID = @LocationMasterID AND IsActive = 1 AND IsVisibleToCustomers = 1
    ORDER BY DisplayOrder, SalonName;
END;
GO
