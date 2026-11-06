# API log review: 5 November 2026

Reviewed the complete `api-2026-11-05.log` supplied from Downloads: 9,504 entries, comprising 8,813 errors, 593 HTTP warnings and 98 information messages. All timestamps in that file use India time (+05:30).

The issues are **not fully resolved on a deployed installation merely by these source changes**. Code fixes and local regression checks are complete. The hosted API and its database were not changed or verified. A read-only audit of the configured local `localhost/InfinityHairArtsAdmin` database still reports missing notification, Super Admin, appointment and timing objects.

| Logged error | Occurrences | Current result |
| --- | ---: | --- |
| Missing `dbo.claimOwnerBookingPush` | 7,386 | The canonical `Database/OwnerBookingNotifications.sql` installs the procedure. It must be installed in the database used by the hosted API. |
| `ClaimOwnerBookingPush` missing `@LeaseID` | 699 | Existing `OwnerBookingNotificationStore.ClaimAsync` supplies a new lease GUID. SQL and worker tests pass. Deploy the current assembly together with the SQL contract. |
| `READPAST` isolation error (SQL 650) | 622 | Reproduced in a disposable database before the change. Fixed by scoping READ COMMITTED to the claim procedure and using `UPDLOCK, READPAST, READCOMMITTEDLOCK`. Tests pass with row versioning both off and on, and with ReadUncommitted, RepeatableRead, Serializable and Snapshot callers. |
| Missing `dbo.getOwnerBookingNotifications` | 85 | Installed by `Database/OwnerBookingNotifications.sql`; current inbox SQL/HTTP checks pass. |
| `GetOwnerBookingNotifications` missing `@OwnerID` | 12 | Existing store supplies `OwnerID` and `SalonID` from the validated owner. SQL/HTTP checks pass. |
| Missing `dbo.GetSuperAdminSalons` | 5 | Existing `Database/SuperAdminDashboard.sql` supplies it. Super Admin SQL/HTTP checks pass; installation remains required where absent. |
| Missing `dbo.GetSuperAdminOverview` | 3 | Same Super Admin migration requirement. |
| `RegisterOwnerPushDevice` too many arguments | 1 | Existing store sends exactly `OwnerID` and `Token`, matching the canonical procedure. Registration checks pass with that migration. |

The old worker and inbox parameter errors reference `OwnerBookingNotifications.cs`, while the later errors reference `OwnerBookingNotificationStore.cs`. This supports a deployment mismatch during the day; it does not establish which version is running now.

The locking change follows [Microsoft's SQL Server table-hint documentation](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17): READPAST has isolation restrictions, and row-versioned READ COMMITTED requires READCOMMITTEDLOCK. ROWLOCK and READCOMMITTEDLOCK are in the same hint group, so the fix replaces ROWLOCK instead of combining them.

| HTTP warning | Occurrences | Result |
| --- | ---: | --- |
| `POST /api/ClientErrors` 404 | 112 | Added the missing endpoint for both apps' existing report batches. It logs bounded, sanitized metadata, returns 204, limits bodies to 64 KiB and batches to 10 reports, and throttles requests by IP. No database migration is needed. |
| `GET /api/Location/salons` 404 | 26 | The pre-existing working-tree directory implementation provides this route. Preserved it; all 34 existing directory checks pass. Deploy its `Database/CustomerSalonDirectory.sql` and current API. |
| `GET /api/Admin/appointments` 404 | 11 | Existing controller and reader provide the route. All 38 appointment checks pass. Install `Database/SalonOwnerAppointmentsRead.sql` where absent. |
| `GET /api/Admin/signup-locations` 404 | 10 | Added an alias to the existing public active-location action. HTTP check confirms both routes return the same locations. |
| `GET /api/Admin/services` 404 | 7 | Existing controller and reader provide the route. All 21 service checks pass. `Database/SalonOwnerServicesRead.sql` is the database dependency. |
| `GET /api/Admin/owner-profile` 404 | 6 | Added authenticated profile retrieval and `Database/SalonOwnerProfileRead.sql`. Returns only the current active owner's name, username, email and salon. The response advertises the app's supported read-only mode; profile edits and credential changes are not introduced. |
| `GET /api/Admin/availability` 404 | 2 | Existing controller provides the route. All 29 timing checks pass. `Database/SalonTimingAvailability.sql` supplies its tables. |
| `GET /api/Admin/appointments/00000000-0000-0000-0000-000000000001/start` 404 | 1 | Unsupported GET using a placeholder booking ID. The frontend's start action uses POST, and this checkout has no start-booking action. The log contains no failed application POST to this route. No booking mutation through GET was added; a start-booking feature remains unimplemented. |
| Other 404s | 361 | Root/static-file requests and probes for PHP, WordPress, environment files, secrets, Git metadata and other unrelated paths. These paths remain unavailable. |
| 401 responses | 57 | Session, login and private-route rejections. Authentication checks pass. The log alone cannot identify why each individual login was rejected; removing authentication would not fix them. |

Deployment steps:

1. Run `InfinityHairartsAPI/Verify_LoggedIssues_2026-11-05.sql` against the **actual hosted API database**. It only reads object definitions, parameter metadata and trigger state. `READY` confirms those metadata checks, not end-to-end delivery.
2. During a coordinated API update, install the applicable scripts from `InfinityHairartsAPI/Database`: `OwnerBookingNotifications.sql` (including the new locking fix), `SuperAdminDashboard.sql`, `SalonOwnerAppointmentsRead.sql`, `SalonTimingAvailability.sql`, `SalonOwnerProfileRead.sql` and `CustomerSalonDirectory.sql`. They require the existing salon, owner, booking and related base schema. If the active-location or owner-service checks are missing, their existing scripts are `LocationMaster.sql` and `SalonOwnerServicesRead.sql`. Review their prerequisites before running them.
3. Deploy the current API assembly and restart it with the matching SQL contracts. The project deliberately excludes SQL scripts, server connection settings and Firebase credentials from publishing; copying the API alone does not install the database updates.
4. Rerun the verification SQL, then check the authenticated inbox, device registration, Super Admin screens, directory, services, appointments, timings and read-only owner profile. Confirm new client reports are accepted, and inspect fresh server logs. A controlled real push is still needed to verify Firebase/device delivery; fixture tests never send live pushes.

Validation completed: **293 passing checks** across notification checks in both database modes (37 + 37), log-related routes (15), appointments (38), services (21), timing availability (29), customer directory (34), and Super Admin HTTP/account SQL/dashboard SQL checks (43 + 19 + 20). The API builds successfully. Existing compiler warnings remain, and NuGet advisory retrieval was unavailable in this environment; builds used cached packages.

Reproduce the focused checks from the API repository:

```powershell
dotnet run --project tests/OwnerNotificationChecks --no-restore
dotnet run --project tests/OwnerNotificationChecks --no-restore -- --row-versioning
dotnet run --project tests/LogIssueChecks --no-restore
dotnet run --project tests/LogIssueChecks --no-restore -- --audit
```

The first three commands use disposable databases and a fake push sender. `--audit` only reads the configured local database and exits with code 1 if any deployment check is not ready. Its local-only guard is intentional; use the read-only SQL directly to audit a hosted database.

Only files inside the API repository were edited. Existing directory work and publish-profile changes were preserved.
