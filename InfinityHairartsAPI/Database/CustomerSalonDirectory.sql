-- Apply after LocationMaster.sql, SalonMaster.sql and SuperAdminDashboard.sql.
-- Public cards must respect the administrator's visibility approval.
SET XACT_ABORT ON;
GO
IF COL_LENGTH(N'dbo.SalonMaster', N'IsVisibleToCustomers') IS NULL
    THROW 50001, 'Apply SuperAdminDashboard.sql before CustomerSalonDirectory.sql.', 1;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SalonMaster') AND name = N'IX_SalonMaster_CustomerDirectory')
    CREATE INDEX IX_SalonMaster_CustomerDirectory
    ON dbo.SalonMaster (SalonName, SalonMasterID)
    INCLUDE (LocationMasterID, SalonAddress, ContactNumber)
    WHERE IsActive = 1 AND IsVisibleToCustomers = 1;
GO
IF OBJECT_ID(N'dbo.GetCustomerSalonDirectory', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetCustomerSalonDirectory AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetCustomerSalonDirectory
    @Search nvarchar(100) = N'',
    @PageSize int = 20,
    @AfterSalonName nvarchar(200) = NULL,
    @AfterSalonMasterID uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageSize < 1 OR @PageSize > 20
        THROW 50002, 'Page size must be between 1 and 20.', 1;

    -- Escape LIKE metacharacters so user text is a literal substring.
    DECLARE @Pattern nvarchar(402) = N'%' + REPLACE(REPLACE(REPLACE(REPLACE(
        @Search, N'~', N'~~'), N'%', N'~%'), N'_', N'~_'), N'[', N'~[') + N'%';

    SELECT TOP (@PageSize + 1)
        s.SalonMasterID, s.LocationMasterID, s.SalonName, l.LocationName,
        COALESCE(s.SalonAddress, N'') AS SalonAddress,
        COALESCE(s.ContactNumber, N'') AS ContactNumber
    FROM dbo.SalonMaster s
    INNER JOIN dbo.LocationMaster l ON l.LocationMasterID = s.LocationMasterID
    WHERE s.IsActive = 1 AND s.IsVisibleToCustomers = 1 AND l.IsActive = 1
        AND (@Search = N'' OR s.SalonName LIKE @Pattern ESCAPE N'~'
            OR l.LocationName LIKE @Pattern ESCAPE N'~')
        AND (@AfterSalonName IS NULL OR s.SalonName > @AfterSalonName
            OR (s.SalonName = @AfterSalonName AND s.SalonMasterID > @AfterSalonMasterID))
    ORDER BY s.SalonName, s.SalonMasterID;
END;
GO
