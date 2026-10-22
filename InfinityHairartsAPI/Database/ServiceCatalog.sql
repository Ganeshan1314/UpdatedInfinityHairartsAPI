-- Run in the API database before publishing the API that uses this procedure.
-- The existing getHairCut_Item_List procedure remains specific to combo services.
IF OBJECT_ID(N'dbo.getAllHairCut_Item_List', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.getAllHairCut_Item_List AS BEGIN SET NOCOUNT ON; END;');
GO

ALTER PROCEDURE dbo.getAllHairCut_Item_List
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        COUNT(A.HairCut_Item_ID) AS HairCut_Item_Count,
        A.HairCut_Item_ID,
        A.HairCut_Item_Code,
        A.HairCut_Item_Name,
        A.HairCut_Item_Price,
        A.HairCut_Item_Quantity,
        A.HairCut_Item_CGST,
        A.HairCut_Item_SGST,
        A.HairCut_Item_Taxable,
        A.HairCutItemImage,
        A.HairCutItemImageSystemPath,
        A.HairCutItemImageS3Path,
        A.Created_Date,
        A.Created_By,
        C.HairCutItemGroup
    FROM dbo.HairCut_Item_List A
    INNER JOIN dbo.HairCut_GroupMaster C
        ON C.HairCutItemGroupMaster_ID = A.HairCutItemGroupMaster_ID
    LEFT JOIN dbo.HairCut_Item_Customer_Bill B
        ON B.HairCut_Item_ID = A.HairCut_Item_ID
    GROUP BY
        A.HairCut_Item_ID,
        A.HairCut_Item_Code,
        A.HairCut_Item_Name,
        A.HairCut_Item_Price,
        A.HairCut_Item_Quantity,
        A.HairCut_Item_CGST,
        A.HairCut_Item_SGST,
        A.HairCut_Item_Taxable,
        A.HairCutItemImage,
        A.HairCutItemImageSystemPath,
        A.HairCutItemImageS3Path,
        A.Created_Date,
        A.Created_By,
        C.HairCutItemGroup
    ORDER BY A.HairCut_Item_Price ASC, A.HairCut_Item_Name ASC;
END;
GO
