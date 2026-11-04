# Appointment status tabs

Deploy `InfinityHairartsAPI/SQL_Update_BookingStatusTabs_2026-10-31.sql` to the
API database after the QR completion migration, then deploy the matching API
and rebuild Salonexa Admin. This rerunnable script preserves booking records,
existing completions and recorded service starts. It combines the status schema
and procedures from `Database/BookingStatusTabs.sql`, the amount view from
`Database/BookingAmounts.sql`, the dashboard procedure
from `Database/SalonOwnerDashboard.sql`, and the updated QR completion procedure
from `Database/BookingQrWorkflow.sql`.

The appointment book has **Pending**, **In progress** and **Completed** tabs.
Before dates are applied, each tab shows the latest 10 confirmed appointments
for the signed-in salon, ordered by appointment date and time descending. Tab
counts cover that salon's full booking history. No date selection is required.
Dates filter the list only after **Apply**; **Latest 10** clears the date filter.
Filtered lists include both boundary dates and use chronological order. Both
modes have 10 bookings per page and support paging to older results. Counts are
independent of the overview's 30-row preview.

`GET /api/Admin/appointments?fromDate=2026-10-01&toDate=2026-10-31&status=Pending&page=1`
returns `items`, `counts` (`pending`, `inProgress`, `completed`), `totalCount`,
`page` and `pageSize`. Internal status values are `Pending`, `InProgress` and
`Completed`. The owner and salon come from the authenticated session.

`GET /api/Admin/appointments?status=Completed&page=1` loads the latest Completed
appointments without a date filter. Dates must be supplied together or omitted
together; supplying only one date returns 400. The overview analytics retain
their existing date-based behavior.

Completed appointments include nullable `billAmount`. `dbo.BookingBillAmounts`
sums non-deleted service line totals for each non-deleted bill, using the saved
bill-header amount only when the bill has no service lines. This aligns with
customer booking history and supports legacy headers that retain a zero total.
Deleted service lines contribute zero and never trigger the header fallback.
Bill totals are grouped by booking ID. Null means no bill; zero means a recorded
zero-amount bill. Pending and InProgress rows do not show amounts.
The dashboard summary includes nullable `completedRevenue`, which
totals those bills for confirmed, completed bookings whose **appointment dates**
are in the selected range. Bill creation dates do not limit that completed total.
The UI labels it **Completed amount**. Multiple appointment slots never multiply
a bill amount, and repeated QR completion does not add another amount. These
fields describe billed amounts; no payment collection operation is added.

Rerun the updated `SQL_Update_BookingStatusTabs_2026-10-31.sql`, then deploy the
matching API/admin build to enable the amount fields. The legacy `todayRevenue`
API field keeps its original bill-creation-date semantics for compatibility.
An older API that omits `completedRevenue` displays a dash for that dashboard
card instead of reporting an invented zero.

**Start service** calls `POST /api/Admin/appointments/{bookingID}/start`. It is
allowed at or after the first selected appointment time in India time. It saves
`CompletionStatus = InProgress`, `StartedUtc` and `StartedByOwnerID`. Repeat or
concurrent starts preserve the first timestamp. Other salons' bookings,
unconfirmed reservations and completed visits cannot be started.

Completing all selected appointment QRs moves a booking to **Completed**.
Completing only some QRs preserves **In progress** for a started booking.
Existing direct QR completion remains supported; a booking does not have to be
started manually before it can be completed. Completion and start procedures
serialize updates to the same booking. Reservation/billing status remains `2`.

The admin screen retains the selected status when refreshing or applying a new
date range, and shows bookings as cards on narrow phone screens. **Complete
with QR** opens the existing overview QR scanner. Failed start requests display
the API message and leave the selected booking unchanged.

Validation: run the API's `tests/BookingQrCompletionChecks` with the absolute
local `appsettings.json` path. It creates/removes a disposable database, exercises
the actual SQL and HTTP endpoints, and does not send push/SMS messages. The
expanded checks cover status counts, pagination beyond 30 bookings, salon
isolation, early starts, simultaneous starts, partial/final QR completion and
migration reruns. No application database or hosted API is updated by these tests.

Validated on 31 October 2026: 354 frontend tests and 91 SQL/HTTP QR, status and amount
checks passed. Frontend lint, production web build, API build, Android sync and
Android debug APK build passed. Browser visual QA was unavailable in the session;
component tests verified date-free recent lists, Apply/Latest 10 filtering, tab
selection, counts, pagination requests, keyboard navigation, starting service and
completed amounts. Amount checks cover legacy zero headers, service-line prices,
deleted lines/bills, multi-slot bookings, missing bills, zero bills and bill dates
outside the selected appointment range.
The application database and hosted API have
not been migrated or deployed by this change.
