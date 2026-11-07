# Available-location loading — 7 November 2026

The location picker previously began its first request only after dashboard
navigation. Every API request queried SQL, while the dashboard simultaneously
started salon, service, booking and profile requests behind the picker.

## Changes

| Area | Result |
| --- | --- |
| Login | Preloads public locations while the login form is open. It never waits for the preload before signing in. Repeated page lifecycle events are throttled to one preload attempt per minute. |
| Device cache | Keeps the existing five-minute fresh cache and up-to-24-hour saved snapshot with background refresh. Preloading and dashboard validation share the same pending request and stored data. |
| Dashboard | On first use, requests locations first. Salon, service, booking and profile network requests start after manual selection or successful current-location detection. Customers with a saved location continue loading content immediately. |
| API | Shares a successful active-location snapshot for two minutes per process. Concurrent misses share one SQL fetch. The existing JSON array, order, active filter and public signup alias are preserved. |
| Failure handling | SQL commands have a five-second timeout, and location controller work has an eight-second deadline including cache-gate waiting and connection setup. Failures and cancelled fetches are never cached. Existing client recovery can retry HTTP 503 responses. |
| Diagnostics | `Server-Timing` reports controller duration and `location-cache` hit/miss status. Information logs separate SQL connection, query and total times, correlated by trace ID. No location names, customer data, credentials or SQL exception messages are added to timing logs. |

The server cache is in memory: a process restart clears it, and multiple API
instances keep independent snapshots. There are currently no API endpoints that
edit locations. Direct database changes can take up to two minutes to reach a
new server read, plus the existing device cache's freshness window. An offline
client can continue showing its existing saved snapshot for up to 24 hours.

Preloading moves work earlier; it cannot guarantee that a very fast sign-in
finishes after locations arrive. The dashboard subscribes to the same unfinished
request when needed. Server caching reduces SQL work on repeat reads; it does
not remove network round trips, proxy queues or application startup time.

## Deployment and measurement

Deploy the rebuilt API and rebuild/update the Ionic client to receive all of
these changes. No database migration is needed for this implementation. Keep
Information logging enabled for `LocationController` and `LocationService`.
This work does not deploy either application or change production data.

An optional SQL file is provided at
`InfinityHairartsAPI/SQL_Update_LocationLoading_Performance_2026-11-07.sql`.
Run it with the application database selected in SQL Server Management Studio.
The default `@ApplyOptionalIndex = 0` performs verification only and reports
location counts, columns, procedure metadata and index definitions. The file
does not seed locations or change stored procedures.

If query-plan/timing investigation justifies an additional index, set
`@ApplyOptionalIndex = 1`. It creates a non-unique covering index on
`(IsActive, DisplayOrder, LocationName) INCLUDE (LocationMasterID)` when a matching
enabled unfiltered index is missing. Repeated runs reuse matching indexes even
under another name, and an existing incompatible index with the proposed name
causes an error for manual review. Small location tables may gain little from
this optional index. Index creation can take schema locks; choose an appropriate
deployment window. The API cache works without applying this optional change.

From the API repository:

```powershell
./tools/Test-LocationLoadingLatency.ps1 -Samples 3 | Format-Table -AutoSize

# On the API host, use the actual local application binding:
./tools/Test-LocationLoadingLatency.ps1 -OriginBaseUrl http://127.0.0.1:5120/ -Samples 3 |
    Format-Table -AutoSize
```

The script sends anonymous GET requests for locations and health, alternating
them with one connection-capable client per target. It disables cookies and
redirects, retains normal HTTPS certificate checks, and has a ten-second timeout.
HTTP is allowed only for a loopback origin. The first sample may include initial
connection setup. It prints status, round-trip duration, timing headers and a
location count; it does not print location records or submit a login.

Compare successful location responses' `TotalMs` with the `locations;dur=...`
header. A large difference indicates time outside the measured controller work;
it does not identify a specific proxy or hosting cause. On cache misses, use SQL
timing logs to distinguish connection setup from the stored procedure. Compare
public and loopback results, and investigate server startup/queues/network
routing when both locations and health have similar delays.

Also check a fresh installation and a repeat visit on the actual Android device:
confirm that a pending preload does not block login, location choices remain
usable, and other dashboard content appears after selection. Public probes in
the assessment environment returned HTTP 403; no production speedup is claimed.

## Validation

- Angular production build passed, with existing style-budget, Stencil and
  CommonJS warnings.
- 151 focused Angular tests passed across login, dashboard, catalog, connection
  preparation, session recovery, the session interceptor and the complete
  customer journey.
- API build passed with existing warnings.
- `LocationLoadingChecks`: 23 deterministic cache and disposable-local-SQL/HTTP
  checks passed. Twelve simultaneous HTTP requests executed the procedure once.
  Checks cover expiration, concurrent waiting, cancellation, empty results,
  failure recovery, safe 503 responses, blocked SQL deadlines, response contract,
  signup alias, timing headers and log privacy. SQL-file checks verify default
  read-only behavior, repeatable optional indexing, reuse of differently named
  matching indexes, and preservation of location records and the procedure.
- `LogIssueChecks`: 30 existing SQL/HTTP checks passed, including the public
  signup-location alias.
- The latency script passed PowerShell syntax validation and a local HTTP-fixture
  check of its paths, response parsing and `Server-Timing` output.

```powershell
dotnet run --project tests/LocationLoadingChecks/LocationLoadingChecks.csproj
dotnet run --project tests/LogIssueChecks/LogIssueChecks.csproj
```

The location harness creates and drops only a uniquely named
`SalonexaLocationChecks_<guid>` database on a guarded local SQL server. It uses
an injected clock to verify the real two-minute cache policy without waiting two
minutes. No SMS, push workers or configured application database writes run.
