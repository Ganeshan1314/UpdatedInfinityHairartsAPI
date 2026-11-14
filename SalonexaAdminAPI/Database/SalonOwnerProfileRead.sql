-- Rerunnable; adds only the profile read procedure, without modifying accounts.
IF OBJECT_ID(N'dbo.GetSalonOwnerProfile', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.GetSalonOwnerProfile AS BEGIN SET NOCOUNT ON; END');
GO
ALTER PROCEDURE dbo.GetSalonOwnerProfile @OwnerID uniqueidentifier, @SalonID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT o.OwnerName, o.UserName, COALESCE(o.EmailAddress, N'') EmailAddress, s.SalonName
    FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID = o.SalonMasterID
    WHERE o.SalonOwnerID = @OwnerID AND o.SalonMasterID = @SalonID
        AND o.IsActive = 1 AND s.IsActive = 1;
END;
GO
