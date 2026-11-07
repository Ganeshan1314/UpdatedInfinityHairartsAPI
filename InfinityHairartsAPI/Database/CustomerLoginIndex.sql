-- Speeds up customer sign-in and profile lookups, which filter by MobileNo.
-- Safe to run more than once. Non-unique on purpose, so it cannot fail if
-- legacy data already contains duplicate numbers.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CustomerRegistration')
                 AND name = N'IX_CustomerRegistration_MobileNo')
    CREATE INDEX IX_CustomerRegistration_MobileNo
    ON dbo.CustomerRegistration (MobileNo)
    INCLUDE (CustomerID, FirstName, EmailAddress, GeneralAddress, ImageName);
GO
