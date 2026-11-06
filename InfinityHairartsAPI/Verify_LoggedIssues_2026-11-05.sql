-- Read-only deployment check. Run against the database used by the hosted API.
SET NOCOUNT ON;
DECLARE @Expected TABLE(ProcedureName sysname, ParameterName sysname, TypeName sysname);
INSERT @Expected VALUES
('ClaimOwnerBookingPush', '@LeaseID', 'uniqueidentifier'),
('FinishOwnerBookingPush', '@DeliveryID', 'bigint'),
('FinishOwnerBookingPush', '@LeaseID', 'uniqueidentifier'),
('FinishOwnerBookingPush', '@Success', 'bit'),
('FinishOwnerBookingPush', '@InvalidToken', 'bit'),
('FinishOwnerBookingPush', '@Error', 'nvarchar'),
('GetOwnerBookingNotifications', '@OwnerID', 'uniqueidentifier'),
('GetOwnerBookingNotifications', '@SalonID', 'uniqueidentifier'),
('RegisterOwnerPushDevice', '@OwnerID', 'uniqueidentifier'),
('RegisterOwnerPushDevice', '@Token', 'nvarchar'),
('GetSuperAdminOverview', '@FromDate', 'date'),
('GetSuperAdminOverview', '@ToDate', 'date'),
('GetSuperAdminSalons', '@Search', 'nvarchar'),
('GetSuperAdminSalons', '@Status', 'varchar'),
('GetSuperAdminSalons', '@Page', 'int'),
('GetSuperAdminSalons', '@PageSize', 'int'),
('GetSalonOwnerProfile', '@OwnerID', 'uniqueidentifier'),
('GetSalonOwnerProfile', '@SalonID', 'uniqueidentifier');

SELECT e.ProcedureName AS [Check],
    CASE WHEN OBJECT_ID(N'dbo.' + e.ProcedureName, N'P') IS NULL THEN 'MISSING'
         WHEN EXISTS (SELECT 1 FROM @Expected x LEFT JOIN sys.parameters p
             ON p.object_id = OBJECT_ID(N'dbo.' + x.ProcedureName, N'P') AND p.name = x.ParameterName
             WHERE x.ProcedureName = e.ProcedureName AND (p.parameter_id IS NULL OR TYPE_NAME(p.user_type_id) <> x.TypeName))
             OR (SELECT COUNT(*) FROM sys.parameters p WHERE p.object_id = OBJECT_ID(N'dbo.' + e.ProcedureName, N'P'))
                 <> (SELECT COUNT(*) FROM @Expected x WHERE x.ProcedureName = e.ProcedureName)
             THEN 'PARAMETER MISMATCH'
         ELSE 'READY' END AS [Status]
FROM @Expected e GROUP BY e.ProcedureName
UNION ALL
SELECT 'ClaimOwnerBookingPush locking',
    CASE WHEN OBJECT_DEFINITION(OBJECT_ID(N'dbo.ClaimOwnerBookingPush', N'P')) LIKE '%READCOMMITTEDLOCK%'
         AND OBJECT_DEFINITION(OBJECT_ID(N'dbo.ClaimOwnerBookingPush', N'P')) LIKE '%SET TRANSACTION ISOLATION LEVEL READ COMMITTED%'
         THEN 'READY' ELSE 'UPDATE REQUIRED' END
UNION ALL
SELECT name, CASE WHEN OBJECT_ID(N'dbo.' + name, N'P') IS NULL THEN 'MISSING' ELSE 'READY' END
FROM (VALUES ('getActiveLocations'), ('GetCustomerSalonDirectory'), ('GetSalonOwnerAppointments'),
    ('GetSalonOwnerServices')) required(name)
UNION ALL
SELECT name, CASE WHEN OBJECT_ID(N'dbo.' + name, N'U') IS NULL THEN 'MISSING' ELSE 'READY' END
FROM (VALUES ('SalonTimingAvailability'), ('SalonBookableTime'), ('OwnerBookingNotification'),
    ('OwnerBookingNotificationRead'), ('OwnerPushDevice'), ('OwnerBookingPushDelivery')) required(name)
UNION ALL
SELECT 'TR_SeatBookingDetails_OwnerNotification',
    CASE WHEN OBJECT_ID(N'dbo.TR_SeatBookingDetails_OwnerNotification', N'TR') IS NULL THEN 'MISSING'
         WHEN OBJECTPROPERTY(OBJECT_ID(N'dbo.TR_SeatBookingDetails_OwnerNotification'), 'ExecIsTriggerDisabled') = 1 THEN 'DISABLED'
         ELSE 'READY' END;
