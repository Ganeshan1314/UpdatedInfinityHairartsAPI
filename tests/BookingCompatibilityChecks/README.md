# Booking compatibility checks

From the repository root:

```powershell
dotnet run --project tests/BookingCompatibilityChecks -- InfinityHairartsAPI/appsettings.json
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-SeatAvailability.ps1
```

The integration checks refuse any SQL server except localhost, `.` or `(local)` and require the database `InfinityHairArtsAdmin`.
Apply the booking SQL migration to that local database first. Tests create customer, salon, slot and bill fixtures and remove them in a finally block.
They start an isolated HTTP host without SMS/push services or notification workers. Existing customer records are not used.

Coverage: deployment marker, anonymous routes, phone login/session restoration, explicit null draft, salon/date validation,
availability with draft/version, saving/retrying selections, stale review, competing customers, checkout retry, cart cleanup,
logout/replayed cookies and fixture cleanup. The existing availability harness covers free slots alongside other bookings.
