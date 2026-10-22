# Android login after closing and reopening

Updated for the IIS DPAPI error reported after the initial deployment. The
previous build selected user-scoped Windows DPAPI; this build explicitly selects
machine-scoped DPAPI so an unloaded IIS user profile does not prevent session
or login cookie encryption. Keys remain encrypted at rest. Preserve the existing
key directory, restrict access to it, and grant the IIS app-pool account
read/write permission. Recycle the pool after replacing the binaries.

The Android app already restores login using the server cookie and flushes
cookies to disk when paused. The live API still exhibits the older temporary
session behavior. On 21 October 2026 both configured hosts accepted Android CORS
requests but returned no persistent-cookie cleanup or session cache protection
when sent a synthetic invalid cookie. No real account credentials were used.

The updated API issues the HTTP-only, Secure `Salonexa.Owner` cookie with a
seven-day sliding lifetime. It restores an active owner's session after app
relaunch or an API restart. Password changes, sign-out, account deactivation
and expiration still revoke access. No password is stored in the app.

## Package contents

- `server/`: published .NET 8 API application files.
- `SQL_Update_QrCompletion_2026-10-21.sql`: the database update required by the QR
  completion/dashboard changes included in this API build. It supports reruns.
- `check-owner-session.mjs`: a deployment check that needs Node.js and never
  reads or submits a real account's cookies or credentials.

Connection settings, Firebase credentials, uploaded images and encryption keys
are excluded from this package. Retain the server's existing copies.

## Apply on the API server

1. Back up the current API folder/database. If the QR completion update is not
   already installed, select `InfinityHairArtsAdmin` in SSMS and run the included
   SQL file. The authentication change itself needs no new SQL tables.
2. Stop the site's IIS application pool. Copy the contents of `server/` into the
   existing API application directory, replacing the application binaries.
   Preserve existing configuration, Firebase files, uploaded images and
   `App_Data/DataProtectionKeys`. Keep the same `Authentication:KeyRingPath` if
   configured. Do not delete the deployment directory first.
3. Start the application pool. On a machine with Node.js, run:

   ```powershell
   node .\check-owner-session.mjs https://103.75.157.142/api/
   ```

   Every check must pass. The server's response header must contain
   `X-Salonexa-Owner-Auth: persistent-cookie-v1`.
4. Sign in once in the Android app so the server can issue the new persistent
   cookie. Close the app completely and reopen it. The dashboard should restore
   automatically while the cookie remains valid. Test API restarts too.

The admin source already accepts third-party API cookies and flushes them on
pause in `MainActivity`. An older APK predating those native changes must also
be rebuilt and installed. Rebuilding only the frontend cannot update the server.

The folder publish profiles in the API source now use
`DeleteExistingFiles=false` to preserve the key ring/configuration during future
publishes. Deleting encryption keys invalidates saved login cookies.

## Verification

All 73 frontend authentication/restoration tests and 35 HTTP/API checks passed.
The release publish succeeded. The published binary was started in an isolated
loopback smoke test with dummy configuration; its persistent-cookie version,
invalid-cookie cleanup, anonymous access rejection and session cache protection
were verified. No real accounts, database or notification services were used.
The package has not been deployed to the public API, and physical Android
close/reopen verification remains to be done after that deployment.
