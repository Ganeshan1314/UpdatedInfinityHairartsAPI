/*
  Customer login performance update - 2026-11-06

  Select the Salonexa application database in SQL Server Management Studio
  before running this file. Do not run against master.

  Creates only the existing, non-unique MobileNo index when it is missing,
  then reports its status. No customer data or stored procedures are changed.
  Non-unique indexing preserves compatibility with legacy duplicate numbers.
  Safe to run again: an existing index with this name is left intact.
*/
SET NOCOUNT ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50001, 'Select the Salonexa application database before running this file.', 1;

IF OBJECT_ID(N'dbo.CustomerRegistration', N'U') IS NULL
    THROW 50002, 'dbo.CustomerRegistration was not found. Check the selected database.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.CustomerRegistration')
      AND name = N'IX_CustomerRegistration_MobileNo'
)
BEGIN
    CREATE INDEX IX_CustomerRegistration_MobileNo
    ON dbo.CustomerRegistration (MobileNo)
    INCLUDE (CustomerID, FirstName, EmailAddress, GeneralAddress, ImageName);

    PRINT 'Created IX_CustomerRegistration_MobileNo.';
END
ELSE
    PRINT 'IX_CustomerRegistration_MobileNo already exists; see verification below.';

-- Verify that the named index is enabled and starts with MobileNo.
SELECT DB_NAME() AS DatabaseName,
       i.name AS IndexName,
       i.is_disabled AS IsDisabled,
       c.name AS LeadingColumn,
       CASE WHEN i.is_disabled = 0 AND i.is_hypothetical = 0
                 AND i.has_filter = 0 AND c.name = N'MobileNo'
            THEN N'READY'
            ELSE N'REVIEW: index is disabled, filtered, or has an unexpected definition'
       END AS IndexStatus
FROM sys.indexes i
LEFT JOIN sys.index_columns ic
    ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
LEFT JOIN sys.columns c
    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.CustomerRegistration')
  AND i.name = N'IX_CustomerRegistration_MobileNo';
