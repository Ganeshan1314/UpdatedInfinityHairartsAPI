-- Restores the read operation used by GET /api/Admin/services.
-- No services, salon links, prices, booking rules or write procedures are changed.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.GetSalonOwnerServices', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerServices AS BEGIN SET NOCOUNT ON; END;');
GO
ALTER PROCEDURE dbo.GetSalonOwnerServices
    @OwnerID uniqueidentifier, @SalonID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (
        SELECT 1 FROM dbo.SalonOwner o
        JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
        WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID
          AND o.IsActive = 1 AND s.IsActive = 1
    )
        THROW 50013, 'Please sign in again.', 1;

    SELECT SalonServiceID, ServiceName, Category, Price, DurationMinutes,
        COALESCE(Description, N'') AS Description, COALESCE(ImagePath, N'') AS ImagePath,
        IsActive, CAST(1 AS bit) AS CanEdit
    FROM dbo.SalonService WHERE SalonMasterID = @SalonID
    UNION ALL
    SELECT h.HairCut_Item_ID, LTRIM(RTRIM(h.HairCut_Item_Name)),
        COALESCE(NULLIF(LTRIM(RTRIM(g.HairCutItemGroup)), N''), N'Services'),
        CAST(h.HairCut_Item_Price AS decimal(10,2)), NULL, N'',
        CASE WHEN NULLIF(LTRIM(RTRIM(h.HairCutItemImage)), N'') IS NULL THEN N''
             ELSE N'/Images/HairCutItemListImages/' + LTRIM(RTRIM(h.HairCutItemImage)) END,
        CAST(1 AS bit), CAST(0 AS bit)
    FROM dbo.HairCut_Item_List h
    JOIN dbo.SalonHairCutItem linked
      ON linked.HairCutItemID = h.HairCut_Item_ID AND linked.SalonMasterID = @SalonID
    LEFT JOIN dbo.HairCut_GroupMaster g
      ON g.HairCutItemGroupMaster_ID = h.HairCutItemGroupMaster_ID
    WHERE COALESCE(h.IsDeleted, 0) = 0
      AND NOT EXISTS (
          SELECT 1 FROM dbo.SalonService s
          WHERE s.SalonServiceID = h.HairCut_Item_ID AND s.SalonMasterID = @SalonID
      )
    ORDER BY IsActive DESC, Price, ServiceName, SalonServiceID;
END;
GO
