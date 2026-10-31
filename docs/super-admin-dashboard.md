# Application-owner dashboard

The application owner uses `/super-admin/login` in the admin frontend.
`/api/SuperAdmin` is a separate authentication boundary from `/api/Admin`.
Salon-owner registration and cookies cannot grant access to these endpoints.

Deploy `InfinityHairartsAPI/Database/SuperAdminDashboard.sql` after the owner
authentication and booking QR schema. The script is rerunnable and uses the same
SQL Server-compatible procedure creation pattern as the existing migrations.
It preserves existing visibility flags; if the column is missing it adds
`IsVisibleToCustomers` with a default of false. It also updates
`getActiveSalonsByLocation` to exclude hidden/inactive salons from customer listings.
Review and approve existing hidden salons through the dashboard after deployment.

Run `dotnet run --project tools/SuperAdminPassword/SuperAdminPassword.csproj` in an
interactive terminal. Enter a new password of 12–256 characters twice; input is
hidden. Save the generated hash as the API server environment variable
`SuperAdmin__PasswordHash`. Set `SuperAdmin__UserName` and optionally
`SuperAdmin__DisplayName` on that server, then restart the API. No account is
enabled until username and hash are configured. Do not store these values in
frontend configuration or source control. The login uses the entered password,
not the generated hash. Usernames are case-sensitive.

The API issues `Salonexa.SuperAdmin`, a secure HTTP-only cookie using a separate
authentication scheme and the existing persisted data-protection key ring. It has
an absolute eight-hour lifetime. Every request revalidates the configured account
and credential version. Updating/removing configuration revokes existing tickets;
restart for environment-variable changes. The API requires HTTPS and uses the
existing CORS origin allowlist. Browser mutations require `X-Salonexa-Request: 1`.
Sign-in is limited to five attempts per minute per remote IP.

Endpoints:

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/api/SuperAdmin/login` | Configured application-owner credentials |
| POST | `/api/SuperAdmin/logout` | Clear the super admin cookie |
| GET | `/api/SuperAdmin/session` | Validate and return the current display identity |
| GET | `/api/SuperAdmin/dashboard?fromDate=YYYY-MM-DD&toDate=YYYY-MM-DD` | Platform counts, date-range totals, seven-day bookings and latest 50 audit events |
| GET | `/api/SuperAdmin/salons?search=&status=all&page=1` | Salon directory, 20 per page; status is all, pending, visible or inactive |
| PUT | `/api/SuperAdmin/salons/{id}/visibility` | JSON: `isVisibleToCustomers` and `expectedVisible` booleans |

Overview salon/owner counts are current platform totals. Confirmed bookings,
completed bookings and distinct booking customers use both inclusive booking-date
boundaries. Billed amount uses bill creation dates and excludes deleted bills;
it does not confirm payment. The chart always covers seven days ending on To date.
Directory searches match salon name, location name, primary owner name and email.
The directory shows one owner per salon and does not return credential hashes.

The visibility procedure locks the salon row, checks the expected prior state,
rejects approval for inactive salons, and updates both the salon and an audit
snapshot in one transaction. Client-provided actor names are ignored. Unchanged
visibility does not add duplicate audit events. This control changes customer
discovery; it preserves existing bookings and owner access. Both never-approved
and previously hidden salons share the pending filter because the existing model
stores a single visibility boolean.

Validation on 27 October 2026: API build, 35 real HTTP authentication/access checks
and 14 SQL checks passed. Run `dotnet run --project
tests/SuperAdminChecks/SuperAdminChecks.csproj` for HTTP checks. Add `-- --database`
to also create, test and remove a disposable local SQL Server database using the
API's local connection settings. Database checks refuse nonlocal hosts. The tests
do not enable a real super admin account or deploy the production SQL migration.
