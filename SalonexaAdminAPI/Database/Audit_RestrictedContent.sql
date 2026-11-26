-- READ-ONLY audit: finds categories, services and salon names that already exist and would now be refused
-- by the app's content rules (massage / spa, sexual, offensive, dangerous). It changes nothing.
-- It is a rough first pass with LIKE patterns. Review every row by hand; rename or deactivate offenders
-- through the app or a reviewed UPDATE. Extend the list below as needed.
SET NOCOUNT ON;

DECLARE @terms TABLE (Term nvarchar(40) PRIMARY KEY);
INSERT @terms(Term) VALUES
 (N'massage'),(N'masseu'),(N'spa'),(N'sex'),(N'porn'),(N'escort'),(N'nude'),(N'erotic'),(N'brothel'),(N'happy ending'),
 (N'body to body'),(N'fuck'),(N'shit'),(N'bitch'),(N'bastard'),(N'asshole'),(N'whore'),(N'slut'),(N'cunt'),
 (N'drug'),(N'weed'),(N'cocaine'),(N'heroin'),(N'weapon'),(N'gambl'),(N'casino'),(N'bomb');

-- 1. Categories (shared by every salon).
SELECT 'Category' AS [Kind], g.HairCutItemGroupMaster_ID AS [Id], g.HairCutItemGroup AS [Text], t.Term AS [Matched]
FROM dbo.HairCut_GroupMaster g
JOIN @terms t ON ' ' + g.HairCutItemGroup + ' ' LIKE '%[^a-z]' + t.Term + '%' COLLATE Latin1_General_CI_AI
   OR g.HairCutItemGroup LIKE '%' + t.Term + '%' COLLATE Latin1_General_CI_AI AND LEN(t.Term) >= 5;

-- 2. Services (name and description), with the salon that owns them.
SELECT 'Service' AS [Kind], h.HairCut_Item_ID AS [Id], h.HairCut_Item_Name AS [Text], l.SalonMasterID AS [Salon], t.Term AS [Matched]
FROM dbo.HairCut_Item_List h
JOIN dbo.SalonHairCutItem l ON l.HairCutItemID = h.HairCut_Item_ID
JOIN @terms t ON ' ' + h.HairCut_Item_Name + ' ' LIKE '%[^a-z]' + t.Term + '%' COLLATE Latin1_General_CI_AI
   OR h.HairCut_Item_Name LIKE '%' + t.Term + '%' COLLATE Latin1_General_CI_AI AND LEN(t.Term) >= 5
UNION ALL
SELECT 'Description', l.HairCutItemID, l.Description, l.SalonMasterID, t.Term
FROM dbo.SalonHairCutItem l
JOIN @terms t ON ' ' + COALESCE(l.Description, N'') + ' ' LIKE '%[^a-z]' + t.Term + '%' COLLATE Latin1_General_CI_AI
   OR COALESCE(l.Description, N'') LIKE '%' + t.Term + '%' COLLATE Latin1_General_CI_AI AND LEN(t.Term) >= 5;

-- 3. Salon names and addresses.
SELECT 'Salon' AS [Kind], s.SalonMasterID AS [Id], s.SalonName AS [Text], t.Term AS [Matched]
FROM dbo.SalonMaster s
JOIN @terms t ON ' ' + s.SalonName + ' ' LIKE '%[^a-z]' + t.Term + '%' COLLATE Latin1_General_CI_AI
   OR s.SalonName LIKE '%' + t.Term + '%' COLLATE Latin1_General_CI_AI AND LEN(t.Term) >= 5;
