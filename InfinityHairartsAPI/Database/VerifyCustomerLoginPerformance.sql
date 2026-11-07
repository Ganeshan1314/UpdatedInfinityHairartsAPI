-- Read-only: run in the target application database before applying an index.
-- Checks the leading key, not just the index name. No customer data is returned.
SELECT c.name AS ColumnName, t.name AS DataType, c.max_length AS MaxLengthBytes
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.CustomerRegistration')
  AND c.name IN (N'CustomerID', N'MobileNo', N'IsDeleted');

SELECT i.name AS IndexName, i.is_disabled, i.has_filter, i.filter_definition,
       c.name AS LeadingColumn
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.CustomerRegistration') AND ic.key_ordinal = 1;

SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID(N'dbo.CustomerRegistration')
      AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.has_filter = 0
      AND ic.key_ordinal = 1 AND c.name = N'MobileNo'
) THEN N'PRESENT: review the actual login execution plan for index usage.'
  ELSE N'MISSING: review and apply CustomerLoginIndex.sql in this database.' END AS MobileLookupIndexStatus;
