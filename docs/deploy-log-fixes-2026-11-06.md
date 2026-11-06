# November 6 API log fixes

The release is staged at `PublishAPI/LogFixes-2026-11-06`. Copy the **contents of its `app` folder** to the server's `D:\HostedApps\InfinityHairArtsAPI` folder during the API update. This server folder is not present on the development machine; preparing the package does not update the hosted API.

Changes in this release:

- Both existing log-related routes are included: `POST /api/ClientErrors` and public `GET /api/Location/salons`.
- Service images are served only from `Images/HairCutItemListImages` using supported image MIME types. All 28 existing original image files are copied into the release.
- Missing GUID-named service photos temporarily redirect to a neutral SVG saying “Service image unavailable.” The redirect is not cached; restoring an original file immediately serves that original. Non-image paths remain unavailable.
- All three publish profiles now set both `PublishUrl` and `PublishDir` to `D:\HostedApps\InfinityHairArtsAPI\`, matching the confirmed hosted path. Existing server files are retained.
- `GET /api/Login/health` reports build `updated-api-20261106-log-fixes`, allowing stale deployments to be detected.

These originals were not found in the repositories, available publish backups, Downloads or Git history:

- `2e84001c-3dd3-4734-94e5-11085e968f7f.jpg`
- `1a7548b5-7b4e-4d02-af7b-3b006602cbba.jpg`

The release handles their requests with the placeholder. It does not recreate or overwrite the original photographs. Restore genuine copies to the server's `Images/HairCutItemListImages` folder when available.

Deployment:

1. Run the included read-only `Database/Verify_LoggedIssues_2026-11-05.sql` against the database configured for the hosted API. For the salon directory, apply `Database/CustomerSalonDirectory.sql` if needed. It requires the existing location/salon schema and `SalonMaster.IsVisibleToCustomers` supplied by `SuperAdminDashboard.sql`. The included scripts also cover the notification, profile, appointment, service and timing dependencies from the prior review; apply only the applicable migrations after reviewing their prerequisites. The package does not automatically change a database.
2. Stop the API's IIS application pool, or use the server's normal maintenance procedure. Copy the files inside `app` to `D:\HostedApps\InfinityHairArtsAPI`. Preserve existing `appsettings*.json`, Firebase credentials, `App_Data` keys, uploaded customer/service images and other server-specific files. The package excludes settings and credentials. If the existing `web.config` contains custom server environment variables or IIS settings, retain them when comparing it with the generated `app/web.config`.
3. Restart the same IIS application pool, then run the included verification script from PowerShell on a machine that can reach the API:

   ```powershell
   powershell.exe -NoProfile -File .\Test-LogIssueDeployment.ps1 -BaseUrl https://infinityhairarts.com/
   ```

4. Expect ten passing checks. The script validates the exact build, the public directory response, ClientErrors route validation, existing photos and missing-photo handling. It does not change bookings, accounts or push registrations. It sends an empty client-report batch, which should receive HTTP 400; this proves the endpoint is installed without creating a client report.

Validation completed:

- 61 focused SQL, HTTP, image and release-package checks passed against a disposable database, including byte-for-byte verification of all 28 packaged images.
- The actual published API was started on loopback; all ten deployment-script checks passed against it. Firebase was disabled and no live pushes were sent.
- Publishing completed successfully. Existing nullable/obsolete-API compiler warnings and an unavailable NuGet advisory feed remain outside this change.
- A live check of `https://infinityhairarts.com/api/Login/health` succeeded, but the build check failed: the public server was still running an older build at verification time. Server deployment is required before claiming the live issues are fixed.

To rebuild the package's application locally without publishing directly to the server folder:

```powershell
dotnet publish InfinityHairartsAPI/InfinityHairartsAPI.csproj --no-restore -c Release -p:PublishProfile=FolderProfile2 -p:PublishDir=D:\GitHub\UpdatedInfinityHairartsAPI\PublishAPI\LogFixes-2026-11-06\app\
dotnet run --project tests/LogIssueChecks --no-restore -- --published D:\GitHub\UpdatedInfinityHairartsAPI\PublishAPI\LogFixes-2026-11-06\app
```
