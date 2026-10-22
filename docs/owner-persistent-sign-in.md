# Android owner sign-in after reopening

The owner API previously authenticated through `.InfinityHairArts.Session` and an
in-memory session cache. Capacitor removes temporary cookies during startup, so
closing/reopening Android could discard authentication even though the frontend
still had the owner's display details. An API restart also discarded the session cache.

Successful owner login now issues `Salonexa.Owner`, an encrypted, HTTP-only, Secure
cookie with `SameSite=None` and a sliding seven-day lifetime. Owner API requests can
restore their server session using this cookie without resubmitting a password.
Restoration verifies that the owner and salon are still active and the password
version still matches. Logout deletes the cookie and owner session values. A valid
older session is upgraded when `/api/Admin/session` is checked.

Deploy the updated API. No SQL migration is needed. Sign in once after deployment
to obtain the persistent cookie. The Android source already accepts API cookies and
flushes its cookie store when the app pauses; installations predating those native
changes also need the updated Android build.

Keep `App_Data/DataProtectionKeys` across API deployments, or set
`Authentication:KeyRingPath` to a persistent directory outside the deployment tree.
The API hosting identity needs read/write access. All instances must share the key
ring and application name. On Windows the keys use machine DPAPI; moving between
machines needs compatible shared key protection. Keys are excluded from Git and
publish output so publishing does not overwrite an existing key ring.

The IIS correction explicitly calls
`ProtectKeysWithDpapi(protectToLocalMachine: true)`. The previous parameterless
call selected user-scoped DPAPI, which can throw `CryptographicException` under
an IIS application-pool account without a loaded user profile. That prevented
ASP.NET session cookies from being created and caused HTTP 500 errors, including
on `/api/ClientErrors`. Machine-scoped DPAPI keeps keys encrypted at rest without
requiring the IIS account's user profile. Keep the key directory on the same
server, grant the app-pool identity read/write access, and limit other accounts'
access to it. Existing keys should be preserved.

Microsoft reference: [Data Protection key encryption at rest](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-encryption-at-rest).

All three folder publish profiles set `DeleteExistingFiles=false`. Their previous
setting could erase the server's key ring and configuration while publishing.
Deploy by updating application files in place, preserving `App_Data`, configuration,
credentials and uploaded images. The API returns
`X-Salonexa-Owner-Auth: persistent-cookie-v1` on Admin responses so the deployed
authentication implementation can be checked without account credentials.

On 21 October 2026 both configured public hosts returned an unauthenticated 401
without `Cache-Control: no-store` and did not clear a synthetic invalid
`Salonexa.Owner` cookie. That differs from the updated middleware's behavior and
indicates that the persistent authentication update still needs deployment.
The admin repository's `npm run check:owner-session` checks the deployment using
only a synthetic invalid cookie and no account credentials. After deployment,
sign in once to replace the old temporary cookie, then close and reopen Android.

Validated on 21 October 2026: all 35 loopback checks in `tests/OwnerSignInChecks`
and 73 focused frontend sign-in/restoration tests passed.
The fixture uses actual HTTP controllers, middleware and cookies, with an in-memory
account repository and fresh API hosts. A physical Android reopen check and live
deployment remain separate from this local verification.
