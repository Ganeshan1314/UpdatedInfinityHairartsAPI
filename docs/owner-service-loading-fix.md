# Owner service loading

The admin app requests `GET /api/Admin/services`. On 5 November 2026 the
production endpoint returned HTTP 404, while `/api/Admin/dashboard` returned
HTTP 401 to an anonymous request. The API source also lacked the service read
route. This is a missing API operation, rather than a styling problem.

`SalonOwnerServicesController` now exposes that read operation using the existing
protected owner sign-in and `dbo.GetSalonOwnerServices`. The owner and salon IDs
come from the validated ticket, never from query parameters. The database checks
both accounts are active and returns the complete salon catalogue independently
of dashboard dates. Linked haircut items keep their existing nullable duration
and read-only metadata. Existing service write procedures and booking logic are
unchanged.

Publish the updated API project, retaining the server's configuration,
credentials, uploaded images and `App_Data`. If needed, run
`InfinityHairartsAPI/Database/SalonOwnerServicesRead.sql` in the API database
first. It is rerunnable and restores only the catalogue read procedure; it does
not insert, update, delete or assign services. The existing salon/service tables
must already be installed. The same script is available in the admin project at
`deployment/api/SQL_Update_ServiceLoading_2026-11-05.sql`.

After publication, anonymous `GET /api/Admin/services` must return 401 instead of
404. A signed-in owner should see only their salon's services after pressing
**Try again** or **Refresh**. No admin app update is required for this fix.

Validation: 21 SQL/HTTP regression checks passed using a disposable local database;
94 existing admin dashboard and service-client tests passed. Tests include owner
isolation, inactive services, duplicate links, deleted haircut items, empty lists,
database access revocation and missing-procedure errors. The API builds successfully.
No hosted API deployment or application-database writes were performed.
