# Owner new-booking alerts

Run `InfinityHairartsAPI/Database/OwnerBookingNotifications.sql` before deploying
the matching API and Salonexa Admin build. It is rerunnable and does not backfill
historical booking notifications. It supports the project's older SQL Server
using ALTER procedures after conditional creation.

The booking table trigger records one alert per confirmed booking when
`BookingStatusMasterID` transitions to `2`. The inbox and per-device delivery
outbox commit with checkout, so rolled-back checkouts never notify owners.
This also covers legacy confirmation procedures without changing customer API
contracts. The trigger snapshots customer name, date, earliest selected time
and seat count. No SMS, email or customer push behavior is added.

All inbox writes/reads and device registration use the session owner/salon,
validated against active database accounts. Read state is independent for each
owner. Device tokens belong to one current owner and salon. Reassignment or
sign-out increments a generation, preventing delivery of old queued messages.
The worker rechecks device/account ownership before sending and uses a delivery
lease to prevent concurrent workers from processing the same row. Delivery is
retried per device, up to 8 attempts within 24 hours. A crash after FCM accepts
a message can still cause redelivery; the notification ID is its stable tray tag.

Routes under `/api/Admin/notifications`:

- `GET /`: latest 50 items plus total unread count.
- `POST /read`: `{ "throughID": 123 }`, marks this owner's salon alerts up to that
  displayed ID, preserving newer arrivals.
- `POST /register-device`: `{ "token": "<FCM registration token>" }`.
- `POST /unregister-device`: same token body, revokes only the current owner's device.

`POST /api/Admin/logout` also accepts optional `{ "pushToken": "..." }` and
revokes that device before clearing authentication. Existing empty logout
requests remain supported.

Phone delivery uses existing `Fcm:ProjectId` and `Fcm:ServiceAccountJson`
configuration, high priority, and the `owner_bookings` Android channel. Configure
the admin app `com.salonexa.admin` in Firebase project `infinity-hair-arts` and
install that app's `google-services.json` in `SalonexaAdmin/android/app`.
The existing customer JSON registers only `com.infinityhairarts.booking`.
Firebase configuration failures do not prevent inbox notification creation.

Run `dotnet run --project tests/OwnerNotificationChecks -- <absolute appsettings.json>`
from this repository. Checks use a disposable local database and do not send
messages. The frontend setup and manual phone verification steps are documented
in `D:\GitHub\SalonexaAdmin\BOOKING-NOTIFICATIONS.md`.
