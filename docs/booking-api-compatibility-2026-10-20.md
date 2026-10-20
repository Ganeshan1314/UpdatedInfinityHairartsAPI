# Appointment times: API deployment correction

The connected phone has app 1.2.21 (version code 29), installed by Google Play. Its web assets match the signed release.
The public API returned 404 for GET /api/Login/session and GET /api/Appoinment/time-selection.
The project published from UpdatedInfinityHairartsAPI lacked these routes and the authoritative Draft/version response required by that app.
Date rows alone can therefore appear while the app cannot confirm appointment times.

This project now supplies phone-number-only login with customer session creation/restoration/revocation, a saved-time endpoint,
Draft/version responses on availability, versioned time selection/review/checkout and validation for one-person salon bookings.
Existing free-slot processing, active-salon checks, cart cleanup, owner APIs, Firebase notifications, reminders and file logging are preserved.

## Deployment

1. Back up the production database and API folder. Stop/drain API booking traffic during the coordinated update.
2. In SSMS select the API's InfinityHairArtsAdmin database. Run the complete InfinityHairartsAPI/SQL_Update_Booking_2026-10-19.sql file.
   Run Verify_Booking_Schema.sql afterward: all seven rows must say READY. This migration preserves existing booking rows.
3. Publish InfinityHairartsAPI/InfinityHairartsAPI.csproj in Release, or deploy the complete prepared runtime folder.
   Keep the server's appsettings.json, Firebase service-account file, uploaded images and IIS settings. Production connection settings
   must point at the migrated database. Configuration and credential files are excluded from the publish output.
   Do not deploy only InfinityHairartsAPI.dll: use the complete output, including FirebaseAdmin and matching dependency files.
4. Restart the API/IIS application pool. Sessions use server memory, so customers should log in again after restart.
5. Open https://infinityhairarts.com/api/Login/health. Expect status=ok, apiContract=mobile-login-reservations-v1,
   build=updated-api-20261020. A bare status=ok response means the previous deployment is still running.
6. An anonymous GET to /api/Login/session and /api/Appoinment/time-selection should return 401, never 404.
   Sign in on the phone, select a salon and date, and verify available times, review and booking confirmation.

The installed app 1.2.21 supports this contract; another Android release is not required for this server fix.
Newer frontend loading-error recovery changes in the Ionic source are separate from the already-signed 1.2.21 bundle.

## Verification

Release build succeeds. The guarded local SQL/HTTP integration harness passed 28 checks; the existing availability harness passed 12 scenarios.
The tests use isolated local fixtures and send no SMS/push messages. Local verification does not deploy or validate the production database.
