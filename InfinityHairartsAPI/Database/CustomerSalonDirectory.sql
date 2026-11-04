-- Run after LocationMaster.sql, SalonMaster.sql and SuperAdminDashboard.sql.
-- Re-runnable. This migration never changes salon visibility or approval.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET QUOTED_IDENTIFIER ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
IF COL_LENGTH(N'dbo.SalonMaster', N'IsVisibleToCustomers') IS NULL
    THROW 51000, 'Deploy SuperAdminDashboard.sql before CustomerSalonDirectory.sql.', 1;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SalonMaster') AND name = N'IX_SalonMaster_CustomerDirectory')
    CREATE INDEX IX_SalonMaster_CustomerDirectory
        ON dbo.SalonMaster(DisplayOrder, SalonName, SalonMasterID)
        INCLUDE(LocationMasterID, SalonAddress, ContactNumber)
        WHERE IsActive = 1 AND IsVisibleToCustomers = 1;
GO
IF OBJECT_ID(N'dbo.getCustomerSalons', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.getCustomerSalons AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.getCustomerSalons
    @Search nvarchar(100) = N'', @PageSize int = 20,
    @AfterDisplayOrder int = NULL, @AfterSalonName nvarchar(200) = NULL,
    @AfterSalonID uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageSize < 1 OR @PageSize > 50
        THROW 51000, 'Page size must be between 1 and 50.', 1;
    -- Fetch one extra row to detect the next page without an expensive total count.
    -- Keyset pagination avoids scanning all earlier pages as the directory grows.
    SELECT TOP (@PageSize + 1) s.SalonMasterID, s.LocationMasterID, s.SalonName,
        l.LocationName, COALESCE(s.SalonAddress, N'') SalonAddress,
        COALESCE(s.ContactNumber, N'') ContactNumber, s.DisplayOrder
    FROM dbo.SalonMaster s
    INNER JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    WHERE s.IsActive = 1 AND s.IsVisibleToCustomers = 1 AND l.IsActive = 1
        AND (@Search = N'' OR CHARINDEX(@Search, s.SalonName) > 0 OR CHARINDEX(@Search, l.LocationName) > 0)
        AND (@AfterSalonID IS NULL OR s.DisplayOrder > @AfterDisplayOrder
            OR (s.DisplayOrder = @AfterDisplayOrder AND s.SalonName > @AfterSalonName)
            OR (s.DisplayOrder = @AfterDisplayOrder AND s.SalonName = @AfterSalonName AND s.SalonMasterID > @AfterSalonID))
    ORDER BY s.DisplayOrder, s.SalonName, s.SalonMasterID;
END;
GO
