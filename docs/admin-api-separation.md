# Salonexa Admin API: separate host (2026-11-14)

`SalonexaAdminAPI/` is a second web project in `InfinityHairartsAPI.sln`. It serves only the
Salonexa Admin app (salon owners and super admin). It has its own `Program.cs`, configuration,
session cookie, publish folder and app pool, so publishing or restarting it never touches the
customer `InfinityHairartsAPI` host. **No file under `InfinityHairartsAPI/` was changed.**
A full copy from before the split is in `D:\GitHub\Backups\UpdatedInfinityHairartsAPI_backup_2026-11-14`.

## What is in the admin host

| Route | Controller |
|---|---|
| `POST login`, `GET session`, `POST logout`, `GET dashboard`, `POST complete-booking-qr` under `api/Admin` | `AdminController` |
| `api/Admin/services` (GET, POST, PUT `{id}`) | `SalonOwnerServicesController` |
| `GET api/Admin/appointments`, `POST api/Admin/appointments/{id}/start` | `SalonOwnerAppointmentsController` |
| `api/Admin/availability`, `api/Admin/notifications` | `SalonTimingAvailabilityController`, `OwnerBookingNotificationsController` |
| `GET`/`PUT api/Admin/owner-profile`, `POST api/Admin/change-username`, `POST api/Admin/change-password` | `OwnerProfileController` |
| `GET`/`PUT api/Admin/salon` | `AdminSalonController` |
| `POST api/Admin/signup` (multipart, rate limited to 10 attempts per 10 minutes per address) | `AdminRegistrationController` |
| `GET api/AppUpdates/salonexa-admin/android` (mandatory-update policy) | `AppUpdatesController` |
| `api/Admin/signup-locations`, `api/Location/getLocations` | `AdminLocationController` |
| `api/SuperAdmin/*` | `SuperAdminController` |
| `api/ClientErrors` | `ClientErrorsController` |

Left out on purpose: customer booking, login, salon directory and reminder code, and the old
unauthenticated `Admin/InsertNewHairCutTpyes`, `UpdateNewHairCutTpyes`, `deleteHairCutItemDetails`,
`GetListHairCutTpyes` and `getHairCutItemDetails` endpoints (the admin app does not call them).

The admin project holds its own copies of the shared pieces it needs (persistent sign-in, push sender,
`SalonClock`, models). Fixes to those must be made in both places until the customer host drops its
admin routes.

## Deploy as a separate site

1. Create a separate IIS site and app pool (new URL, for example `admin-api.<your domain>`).
2. `dotnet publish SalonexaAdminAPI -c Release -o PublishAdminAPI`
3. On the server, create `appsettings.json` from `SalonexaAdminAPI/appsettings.example.json`.
   Publish never overwrites it.
   - `ConnectionStrings:dbconnection`: same database as the customer API (see "Database" below).
   - `ServiceImages:Directory`: **set this to the folder the customer API serves
     `/Images/HairCutItemListImages` from**, or photos that owners upload will not appear in the
     customer app. Give the admin app pool write access to that folder.
   - `SalonImages:Directory`: where signup salon photos are saved and served as `/SalonImages/...`
     (defaults to a `SalonImages` folder in the admin host). Give the app pool write access.
   - `AdminAppUpdates:Android:LatestVersionCode` / `LatestVersion`: optional override of the
     mandatory-update policy that ships in `admin-app-updates.json` (currently code 7 / 1.1.2). The
     installed app is code 10 / 1.1.5, so only raise this after that release is live on Google Play
     for everyone, or users on older builds are forced to update to a build they cannot get.
   - `Fcm`: copy the Firebase service-account file next to the admin host if owner push alerts are wanted.
   - `Cors:AllowedOrigins`: add any hosted web origin of the admin app. The Capacitor origins are built in.
4. Browse to `/swagger` to confirm the host is up.
5. In the admin app, change `apiUrl` in `src/environments/environment.ts` and `environment.prod.ts`
   to the new site's `/api/` URL, then rebuild the app.

## Database

Admin and customer apps read and write the same bookings, services and salons, so they use the same
database. What is separate is the deployment: admin-only SQL lives in `SalonexaAdminAPI/Database/`,
and the admin host does not need any customer SQL.

- Back up first, then run these once, in order. All can be rerun:
  1. `InfinityHairartsAPI/SQL_Update_ServiceEditing_2026-11-07.sql` (or `SalonexaAdminAPI/Database/SalonOwnerServices.sql`
     then `SalonOwnerDashboard.sql`) for service editing.
  2. `SalonexaAdminAPI/Database/SalonOwnerSignup.sql` and `SalonOwnerSalonProfile.sql`. They script the signup
     and salon procedures that were only installed on the existing database, never in source control. On a
     database that already has them, the scripts recreate them unchanged.
  3. `SalonexaAdminAPI/Database/SalonOwnerAppointmentStart.sql`. **This is the only script that changes a table
     the customer app also uses.** `dbo.SeatBookingDetails` gets two nullable columns (`StartedUtc`,
     `StartedByOwnerID`) and its `CompletionStatus` check constraint also allows `InProgress`. No existing row
     changes. The customer API never reads `CompletionStatus`, but run it after hours and with a backup.
- Never rerun `SalonexaAdmin/deployment/api/SQL_Update_ServiceLoading_2026-11-05.sql`; it makes
  menu-linked services read-only again ("On your menu" with no Edit button).
- Owner push alerts: the worker claims rows from a leased queue, so it is safe while both hosts run.
  Set `OwnerNotifications:PushWorkerEnabled` to `false` on the admin host to turn it off there.

## Verify

```powershell
dotnet run --project tests/AdminApiServicesChecks -c Release   # 65 service create/edit/photo checks
dotnet run --project tests/AdminApiOwnerFlowChecks -c Release  # 60 signup, salon, account, start and update-policy checks
```

Both run against a disposable local database and temp folder and delete them afterwards.

## Where the ported routes came from

Signup, `GET`/`PUT api/Admin/salon` and the salon/signup procedures existed only on the old live
server (recovered from its build) and in the existing database, never in this repository. They are
rewritten here against those same procedures. `owner-profile`, `change-username`, `change-password`
and the app-update policy come from `SalonexaAdmin/deployment/api/`, together with its newer
`SalonOwnerPersistentSignIn.cs`, which ties server sessions to the credential stamp so a password or
username change signs out every other device (verified by the owner-flow checks). Appointment start
comes from the unmerged `changes/owner-api-updates-2026-11-04` branch, with its procedure renamed
`dbo.StartSalonOwnerBooking`.

## Behaviour to know about

- The privacy-policy tick at signup is validated but, as before, not stored.
- New salons start hidden from customers (`IsVisibleToCustomers = 0`) until a super admin approves them.
- A malformed request body from a signed-out user gets 400 instead of 401, because ASP.NET validates the
  body before the action runs. A well-formed signed-out request gets 401.

## Local run

`dotnet run --project SalonexaAdminAPI` uses `https://localhost:44350` / `http://localhost:5150`.
The owner cookie is Secure, so sign-in from a browser needs the HTTPS URL
(`dotnet dev-certs https --trust` once on the machine).
