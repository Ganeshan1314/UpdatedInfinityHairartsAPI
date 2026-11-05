# Appointment loading fix

On 5 November 2026 the production request
`GET /api/Admin/appointments?status=Pending&page=1` returned HTTP 404. The API
source also lacked that read operation. The frontend requested the correct
route, but also hid the appointment book while dashboard analytics loaded or
failed. Appointments now render independently of overview loading/errors.
Refresh and pull-to-refresh reload the appointment list directly. Date controls
remain usable while overview analytics are waiting. Background overview refresh
after applying dates is preserved. Owner/salon changes reset the appointment
request scope.

`SalonOwnerAppointmentsController` now exposes that route using the existing
protected owner authentication. It calls `dbo.GetSalonOwnerAppointments` with
owner and salon IDs from the validated ticket. Query parameters cannot select
another salon. The procedure checks that both the owner and salon are active.

The response matches the existing app: `items`, `counts`, `totalCount`, `page`
and `pageSize`. The page size is ten. Without dates it lists the newest confirmed
appointments across all history. With both dates it uses inclusive boundaries
and chronological order. Counts cover all three saved statuses in the same
scope. Draft/cancelled bookings and other salons are excluded. Multiple selected
times produce one appointment using its earliest time. Completed amounts sum
non-deleted bills; missing bills remain null and zero-value bills remain zero.

This fix only restores appointment reads. It does not change bookings, statuses,
status constraints, start-service actions or QR-completion rules. An In progress
tab with no saved InProgress bookings correctly returns an empty list.

Deployment:

1. Run `InfinityHairartsAPI/Database/SalonOwnerAppointmentsRead.sql` in the API
   database. The identical admin-project copy is
   `deployment/api/SQL_Update_AppointmentLoading_2026-11-05.sql`. This rerunnable
   script only creates/updates the read procedure. Existing owner, booking,
   customer, time-selection and bill tables must already be installed.
2. Publish the updated API, keeping server configuration, credentials, uploaded
   images, logs, release policy and `App_Data`. Restart the IIS application pool.
3. An anonymous GET `/api/Admin/appointments` must return 401 instead of 404.
   A signed-in owner can then reopen Appointments and press Try again or Refresh.
4. Build the updated admin app and install/deploy it to receive the independent
   appointment loading and refresh fix. Android web assets are updated with
   `npm.cmd run build` followed by `npx.cmd cap copy android`; rebuild/install the
   Android app afterwards.

The prepared API package also contains the preceding service-loading fix. It
retains other work already present in the API workspace.

Validation: 38 SQL/HTTP checks passed using a disposable local database, including
owner isolation, active-account checks, status/date validation, date boundaries,
pagination, multi-time/multi-bill duplication, zero/missing bills, empty lists,
cache prevention and missing-migration errors. All 108 appointment, dashboard
and service-client tests passed, including regressions for failed/waiting
analytics and pull-to-refresh completion. The API builds successfully.
No application database or hosted API was modified.
