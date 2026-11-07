/*
  Available-location performance verification / optional index - 2026-11-07

  Select the application database in SQL Server Management Studio before running.
  The location preloading, two-minute API cache and timing improvements require
  no SQL migration. Deploying this script is optional.

  DEFAULT: verification only. No data, procedure or index changes.
  OPTIONAL: change @ApplyOptionalIndex to 1 to add a non-unique covering index
  for dbo.getActiveLocations when an equivalent usable index is missing.
  A table with only a few locations may gain little; check the actual execution
  plan and SQL timing before enabling this option. Index creation can acquire
  schema locks, so schedule it appropriately for the target database.

  Does not seed/reactivate locations or alter login, booking or salon procedures.
  Repeatable: equivalent enabled indexes are reused; existing indexes are never
  dropped, rebuilt or replaced. API/device cache freshness still applies to edits.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ApplyOptionalIndex bit = 0; -- Set to 1 only to install the optional index.
DECLARE @IndexName sysname = N'IX_LocationMaster_Active_DisplayOrder_Name';
DECLARE @LocationTableId int = OBJECT_ID(N'dbo.LocationMaster', N'U');
DECLARE @LocationProcedureId int = OBJECT_ID(N'dbo.getActiveLocations', N'P');

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 51001, 'Select the application database before running this script.', 1;

IF @LocationTableId IS NULL
    THROW 51002, 'dbo.LocationMaster was not found. Check the selected application database.', 1;

IF COL_LENGTH(N'dbo.LocationMaster', N'LocationMasterID') IS NULL
    OR COL_LENGTH(N'dbo.LocationMaster', N'LocationName') IS NULL
    OR COL_LENGTH(N'dbo.LocationMaster', N'IsActive') IS NULL
    OR COL_LENGTH(N'dbo.LocationMaster', N'DisplayOrder') IS NULL
    THROW 51003, 'LocationMaster is missing an expected location-query column. Review the schema.', 1;

IF @LocationProcedureId IS NULL
    THROW 51004, 'dbo.getActiveLocations was not found. Review the database deployment.', 1;

-- Match a usable unfiltered index by its keys/coverage, including indexes with
-- another name. Clustered-key columns are implicitly available in rowstore
-- nonclustered indexes and need not be explicitly repeated as INCLUDE columns.
DECLARE @SupportingIndexes TABLE (IndexName sysname NOT NULL);
INSERT @SupportingIndexes (IndexName)
SELECT i.name
FROM sys.indexes i
WHERE i.object_id = @LocationTableId
  AND i.type IN (1, 2)
  AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.has_filter = 0
  AND EXISTS (
      SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
        AND ic.key_ordinal = 1 AND c.name = N'IsActive'
  )
  AND EXISTS (
      SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
        AND ic.key_ordinal = 2 AND ic.is_descending_key = 0 AND c.name = N'DisplayOrder'
  )
  AND EXISTS (
      SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
        AND ic.key_ordinal = 3 AND ic.is_descending_key = 0 AND c.name = N'LocationName'
  )
  AND (
      i.type = 1
      OR EXISTS (
          SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
            AND c.name = N'LocationMasterID'
      )
      OR EXISTS (
          SELECT 1 FROM sys.indexes ci
          JOIN sys.index_columns ic ON ic.object_id = ci.object_id AND ic.index_id = ci.index_id
          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
          WHERE ci.object_id = i.object_id AND ci.type = 1 AND ci.is_disabled = 0
            AND ic.key_ordinal > 0 AND c.name = N'LocationMasterID'
      )
  );

DECLARE @IndexAction nvarchar(200);
IF EXISTS (SELECT 1 FROM @SupportingIndexes)
    SET @IndexAction = N'Existing supporting index reused; no index changes.';
ELSE IF @ApplyOptionalIndex = 0
    SET @IndexAction = N'Verification only; optional index was not requested.';
ELSE
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @LocationTableId AND name = @IndexName)
        THROW 51005, 'The optional index name already exists with an unsupported definition or state. Review it manually.', 1;

    CREATE NONCLUSTERED INDEX IX_LocationMaster_Active_DisplayOrder_Name
        ON dbo.LocationMaster (IsActive, DisplayOrder, LocationName)
        INCLUDE (LocationMasterID);

    INSERT @SupportingIndexes (IndexName) VALUES (@IndexName);
    SET @IndexAction = N'Created the optional location-query index.';
END;

SELECT DB_NAME() AS DatabaseName,
       N'No SQL migration is required for the location API/client improvements.' AS RequiredMigration,
       @ApplyOptionalIndex AS OptionalIndexRequested,
       @IndexAction AS IndexAction;

-- Counts only; location records and customer details are not returned.
SELECT COUNT_BIG(*) AS TotalLocations,
       COUNT_BIG(CASE WHEN IsActive = 1 THEN 1 END) AS ActiveLocations
FROM dbo.LocationMaster;

SELECT c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType,
       c.max_length AS MaxLengthBytes, c.is_nullable AS IsNullable
FROM sys.columns c
WHERE c.object_id = @LocationTableId
  AND c.name IN (N'LocationMasterID', N'LocationName', N'IsActive', N'DisplayOrder')
ORDER BY c.column_id;

SELECT SCHEMA_NAME(p.schema_id) + N'.' + p.name AS ProcedureName,
       p.modify_date AS ProcedureModifiedAt,
       (SELECT COUNT(*) FROM sys.parameters sp WHERE sp.object_id = p.object_id AND sp.parameter_id > 0) AS InputParameterCount
FROM sys.procedures p
WHERE p.object_id = @LocationProcedureId;

SELECT IndexName AS SupportingIndex FROM @SupportingIndexes ORDER BY IndexName;

SELECT i.name AS IndexName, i.is_disabled AS IsDisabled, i.has_filter AS HasFilter,
       ic.key_ordinal AS KeyPosition, ic.is_included_column AS IsIncludedColumn,
       c.name AS ColumnName
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = @LocationTableId AND i.type IN (1, 2)
ORDER BY i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id;

PRINT 'Verification completed. An index alone does not establish a speedup; review the actual query plan and API/SQL timings.';
