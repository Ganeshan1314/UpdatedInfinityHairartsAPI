# QR booking completion

Scanning fills the QR value. Selecting **Complete booking** posts it to
`POST /api/Admin/complete-booking-qr` using the signed-in owner's cookie.
The supported QR value is
`infinityhairarts:booking:v1:<SeatBookingDetailsID>:<TimeAllocationID>`.

The SQL procedure validates the active owner, salon, confirmed booking, selected
appointment time and India appointment start time. One transaction saves:

- `dbo.BookingQrCompletion`: one record per booking/time, the completing owner,
  and `CompletedUtc`.
- `dbo.SeatBookingDetails.CompletionStatus`: `Pending` or `Completed`.
- `dbo.SeatBookingDetails.CompletedUtc`: UTC time when all selected appointments
  have been completed.

For a booking with multiple appointment times, every selected time must have a
QR completion before the booking becomes Completed. Repeated scans preserve the
original timestamp and return `wasAlreadyCompleted: true`. The QR response also
includes `bookingStatus`; the app displays success after the API response and
refreshes the dashboard. A partly completed booking explains that appointment
QRs remain. Failed writes roll back both the status and the QR record.

`BookingStatusMasterID` remains `2` (Booked). It represents reservation/billing
confirmation and is used by customer availability queries; visit completion is
stored separately to preserve existing booking and capacity behavior. The
dashboard reads the saved CompletionStatus for the selected date and salon.

The dashboard summary returns `pendingBookings` and `completedBookings` for the
selected date, plus `totalBookings`, `totalPendingBookings` and
`totalCompletedBookings` across all dates. These counts read confirmed bookings
(`BookingStatusMasterID = 2`) from `SeatBookingDetails` for the signed-in salon;
they are independent of the 30-row appointment preview. A booking with several
appointments counts once, and stays Pending until every appointment QR is
completed. Counts include future confirmed bookings. Changing the selected date
does not change the totals across all dates. Salons with no bookings return zeros.

For an installation that already has the QR completion update, run
`InfinityHairartsAPI/SQL_Update_BookingCounts_2026-10-22.sql` in SSMS against
`InfinityHairArtsAdmin`, then deploy the matching API and admin build. This
transactional, rerunnable script updates only the dashboard procedure. New
installations can use the updated combined QR completion script below. An older
API that omits the new fields displays a dash for those counts in the admin app.

The six overview cards use the selected date for Bookings, Completed, Pending
and Billed amount. Salon customers counts distinct customers with confirmed
bookings across the salon; Active services counts active rows in `SalonService`.
The billed amount sums nondeleted `HairCut_Item_Bill_Master` records created on
the selected date, scoped through their booking's salon. The dashboard procedure
also returns the salon profile and service collection expected by the admin
screen, and the API includes the selected date. Deploy the matching API and SQL
update together because the procedure now returns six result sets.

Validated on 22 October 2026: 40 SQL/HTTP checks and 52 focused frontend tests
passed, including all six card values, deleted bills, multiple bills per booking,
distinct customers, inactive services, date changes and salon isolation.

## Deployment

1. In SSMS select the API's `InfinityHairArtsAdmin` database. Run the entire
   `InfinityHairartsAPI/SQL_Update_QrCompletion_2026-10-21.sql` file in a new query
   window. The script is transactional and supports reruns. It preserves existing
   bookings and backfills their status from existing matching QR completions.
2. Deploy the matching API build from this repository and rebuild the admin app.

The SQL update is generated from the completion batches in
`Database/BookingQrWorkflow.sql` and `Database/SalonOwnerDashboard.sql`. It does
not reassign bookings to salons or replace customer booking-history procedures.
Apply SQL before deploying the API. Source changes alone do not update a hosted
database or API.

## Verification

Run the checks with an absolute path to a local configuration file:

```powershell
dotnet run --project tests/BookingQrCompletionChecks -- D:\GitHub\UpdatedInfinityHairartsAPI\InfinityHairartsAPI\appsettings.json
```

Checks create a uniquely named temporary database on local SQL Server and remove
it afterward; the configured application's database is unchanged by the checks.
They exercise the actual controller, service and deployment SQL, including
authentication, invalid/early/other-salon scans, persisted status/timestamps,
duplicates, multiple times, concurrent scans, rollback, dashboard reload and
migration reruns. No SMS or push notifications run.

Validated on 21 October 2026: 26 SQL/HTTP persistence checks and 50 focused
frontend tests passed. API Release build, admin production build and frontend
lint passed. The SQL update was applied to the configured local
`InfinityHairArtsAdmin` database. The hosted API/database were not deployed.
