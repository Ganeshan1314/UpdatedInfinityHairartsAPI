-- Run Database/SalonOwnerServices.sql first in the API database.
-- The user confirmed that the existing haircut menu belongs only to Infinity Hair Arts.
-- This adds salon links; it does not copy, rename or modify haircut/billing records.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.SalonHairCutItem', N'U') IS NULL
        THROW 50003, 'Run Database/SalonOwnerServices.sql first.', 1;

    -- Set an explicit ID here if more than one salon has the same name.
    DECLARE @InfinitySalonID uniqueidentifier = NULL;
    IF @InfinitySalonID IS NULL
    BEGIN
        IF (SELECT COUNT(*) FROM dbo.SalonMaster WHERE LTRIM(RTRIM(SalonName)) = N'Infinity Hair Arts') <> 1
            THROW 50015, 'Set @InfinitySalonID to the confirmed Infinity Hair Arts salon ID, then run again.', 1;
        SELECT @InfinitySalonID = SalonMasterID FROM dbo.SalonMaster
        WHERE LTRIM(RTRIM(SalonName)) = N'Infinity Hair Arts';
    END;
    IF NOT EXISTS(SELECT 1 FROM dbo.SalonMaster WHERE SalonMasterID = @InfinitySalonID AND IsActive = 1)
        THROW 50015, 'The selected Infinity Hair Arts salon must be active.', 1;

    INSERT dbo.SalonHairCutItem(HairCutItemID, SalonMasterID)
    SELECT DISTINCT h.HairCut_Item_ID, @InfinitySalonID
    FROM dbo.HairCut_Item_List h
    WHERE COALESCE(h.IsDeleted, 0) = 0
      AND NOT EXISTS(SELECT 1 FROM dbo.SalonHairCutItem linked WITH (UPDLOCK, HOLDLOCK)
          WHERE linked.HairCutItemID = h.HairCut_Item_ID);

    COMMIT TRANSACTION;
    SELECT SalonMasterID = @InfinitySalonID, LinkedServices = COUNT(*)
    FROM dbo.SalonHairCutItem WHERE SalonMasterID = @InfinitySalonID;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
