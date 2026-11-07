# Customer login performance — 6 November 2026

The Ionic login waits for `POST /api/Login/customerLoginButtomClick` before
opening the dashboard. The changes reduce database work and bound unsuccessful
attempts while keeping the mobile-number login and existing response contract.

## Application changes

| Operation | Before | After |
| --- | --- | --- |
| Existing customer | Lookup plus address update; two connection opens | One customer-ID lookup; one connection open; no profile write |
| New customer | Lookup and insert on separate connections | Lookup and existing insert procedure on one connection |
| SQL I/O | Synchronous, unlimited command timeout | Asynchronous, cancellation-aware, five-second command timeout |
| API login budget | No explicit deadline | Eight seconds across session loading, database operations and session commit |
| Initial Ionic sign-in | No explicit request timeout; sends cached address | Ten-second timeout, retry message, mobile number only |
| Timing | No login-specific breakdown | Connection, lookup, insert and database/API totals, correlated by request trace ID |

Session recovery already uses ten-second request timeouts and remains unchanged.
Push registration continues independently of dashboard navigation. A failed login
does not mark the session ready or automatically replay the request.

The lookup selects only `CustomerID` and preserves the legacy procedure's
`IsDeleted = 0 OR IsDeleted IS NULL` rule. The locally inspected schema uses
`MobileNo nvarchar(25)`; the lookup parameter uses that type and size. New
customers still use the existing `customerLoginButtomClick` insert procedure,
including an initial address supplied by older clients. Existing customers'
addresses are preserved even if older clients submit empty or stale addresses.
Profile editing continues through its existing endpoint.

Database timing includes failed/cancelled stages. API timing is also returned as
`Server-Timing: customer-login;dur=...` in milliseconds. It covers the controller
work, not upstream proxy queueing, response serialization or network transfer.
Logs omit mobile numbers, addresses, customer IDs, request bodies and SQL error
messages. Failure logs contain only error type, SQL error number and trace ID.
Keep Information-level logging enabled for `LoginService` and `LoginController`
to capture the timing entries.

## Database index verification

Run `InfinityHairartsAPI/Database/VerifyCustomerLoginPerformance.sql` against the
target application database. It is read-only and reports column types and usable
leading mobile-number indexes without returning customer records.

The configured local database had no leading `MobileNo` index during inspection.
The existing `Database/CustomerLoginIndex.sql` was already present in the working
tree; it was preserved and tested twice in a disposable database. Apply that
login-specific script to the target database if verification reports the index
missing. Review the actual execution plan afterward: a small table can legitimately
use a scan, and an installed index alone does not prove that a particular query
uses it. No index or schema was changed in the configured application database.

## Hosting investigation

Earlier public checks measured approximately 3.20 seconds for health and 4.08
seconds for an anonymous session check, with HTTPS connection setup around 0.2
seconds. These endpoints do not query the customer database, so database changes
alone cannot explain or eliminate that observed wait. Those two samples do not
establish the precise hosting/proxy cause or prove a cold start.

A later validation of the new read-only script returned:

| Endpoint | First sample | Second sample | HTTP status |
| --- | --- | --- | --- |
| Health | 9,633 ms | 637.3 ms | 200 |
| Anonymous session | 1,521.4 ms | 625.7 ms | 401 |

The script reuses a connection-capable HTTP client. This variation can include
connection setup, proxy behavior and application startup; it is not evidence of
a deployed code improvement. The public API had not been updated by this work.

From the API repository, compare repeated public and origin timings:

```powershell
./tools/Test-CustomerLoginLatency.ps1 -Samples 3 | Format-Table -AutoSize

# On the API host, substitute the application's actual local binding:
./tools/Test-CustomerLoginLatency.ps1 -OriginBaseUrl http://127.0.0.1:5120/ -Samples 3 |
    Format-Table -AutoSize
```

The script uses anonymous GET requests only, checks health and session status,
disables redirects/cookies, retains HTTPS certificate checks and applies a
ten-second timeout. HTTP is allowed only for a loopback origin. A 401 from the
session endpoint is expected. It does not submit a customer login.

If the public path is consistently slower, inspect proxy/network routing. If
the origin is also slow, correlate the login timing logs with server CPU, memory,
request queues and SQL blocking. If only requests after an application-pool
restart or idle period are slow, review IIS Application Initialization,
`AlwaysRunning` and `preloadEnabled` for this API's pool/site. The local IIS
configuration did not identify the public API's deployment; no IIS settings were
changed. Apply hosting changes only on the actual API host after identifying
the cause.

## Validation

- Angular: 73 focused login, session recovery and customer-flow tests passed.
- Angular production build passed; existing style-budget, Stencil and CommonJS
  warnings remain.
- API build passed with existing warnings.
- `tests/CustomerLoginChecks`: 29 HTTP/SQL checks passed using a disposable local
  database, including new/existing login, soft deletion, address preservation,
  authenticated session recovery, blocked SQL timeout, explicit retry, logout,
  response contract, timing headers and log privacy.
- Customer profile validation: 79 existing checks passed.
- Read-only index verification ran successfully against the configured local
  database. The latency script passed PowerShell syntax validation and a public
  two-sample check of both endpoints (200 health / 401 anonymous session).

Five warm HTTP login samples in the tiny disposable local fixture averaged about
0.7 ms (range 0.6–0.9 ms). This is not a production benchmark or a before/after
speedup claim. Production performance must be measured after deploying both the
API and rebuilt Ionic client and checking the target database index. The initial
implementation did not deploy to production; subsequent live checks are below.

Re-run the focused API checks from the API repository:

```powershell
dotnet run --project tests/CustomerLoginChecks/CustomerLoginChecks.csproj
```

The harness accepts only a local configured SQL server, creates a database named
`SalonexaLoginChecks_<guid>`, and removes that database in cleanup. It does not
start notification workers or modify customers in the configured application
database. The existing booking compatibility fixture now seeds its address before
sign-in, matching the updated rule that login preserves profile details.

## Follow-up: live login and connection preparation

After the user confirmed deployment and supplied a test account, live sign-in,
session verification and sign-out succeeded. The measured login processing time
was reported by the deployed `Server-Timing` header:

| Request condition | Full public request | Login controller duration |
| --- | --- | --- |
| Existing connection after a health request | 622.8 ms | 3.4 ms |
| Login as the first request on a fresh connection | 4,192.2 ms | 2.3 ms |

The latter request completed client-side HTTPS setup in about 406 ms. The timing
gap occurs outside the measured controller work; it does not establish whether
the delay is at IIS, the proxy, routing or another untimed stage. Both test
sessions were signed out. No profile values were submitted or modified.

The browser's CORS preflight was also tested using the app's `https://localhost`
origin and POST headers. The first OPTIONS request took 2,884.5 ms and the second
166.9 ms. Both returned 204, allowed credentials and the expected origin, but
neither returned `Access-Control-Max-Age`.

Additional changes now prepared:

- `src/index.html` opens an early credential-compatible preconnection to the API
  origin while the app loads.
- `ApiConnectionWarmupService` starts a simple credentialed GET to `Login/health`
  when the login page opens, is reused, or regains connectivity. This overlaps
  optional connection setup with entering the number. It does not fetch a
  customer profile, submit a phone number or alter authentication.
- The warm-up bypasses app interceptors, sends no custom headers, and therefore
  avoids starting an extra CORS preflight or session recovery. It is limited to
  one attempt per minute, shares an in-flight attempt, skips offline work and
  cancels after five seconds. Failure is ignored. No login code waits for it.
- The existing `AllowIonic` CORS policy sets a ten-minute preflight max age, so
  browsers can reuse an approval for matching requests. The origin allowlist,
  credentials setting and endpoint authentication checks are preserved. This
  does not remove the first required preflight.

These changes need a rebuilt/released Ionic client and a republished API. They
require no additional SQL update. They may reduce the time after tapping Continue
when connection setup finishes while the user fills the form; they do not make
the network itself faster or guarantee a particular duration. If the user submits
immediately or the connection has expired, the initial wait can still occur.

The direct origin IP/IIS binding remains unavailable: the supplied
`https://infinityhairarts.com/api/` is the public proxy URL. The underlying hosting
delay is still unresolved. No IIS or Cloudflare setting was changed.

Validation for this follow-up: 83 focused Angular tests passed, covering pending,
failed, timed-out, throttled and offline connection preparation and a successful
login while warm-up is still pending. The API build and production frontend build
passed; existing frontend style-budget, Stencil and CommonJS warnings remain.
The eight service checks were rerun successfully after refining their assertions.

References:

- [Chrome guidance on preconnecting to API origins](https://developer.chrome.com/docs/modern-web-guidance/guides/performance/performance)
- [ASP.NET Core CORS preflight caching](https://learn.microsoft.com/en-us/aspnet/core/security/cors#preflight-requests)
