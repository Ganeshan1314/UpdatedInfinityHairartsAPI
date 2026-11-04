# Salon haircut services: add, edit and reload

`GET /api/Admin/services` reads `HairCut_Item_List` through `SalonHairCutItem`.
`POST /api/Admin/services` inserts a haircut item and links it to the signed-in
salon in one transaction. `PUT /api/Admin/services/{serviceId}` updates that
salon's existing haircut item, retaining its ID and original creation date.
The API derives both owner and salon IDs from the authenticated server session;
request fields and query strings cannot select another salon.

Name and price are stored in `HairCut_Item_List`. Category selects or creates a
`HairCut_GroupMaster` entry and updates only the item's category reference;
it never renames a shared category. New categories receive an `OrderID` for
compatibility with the customer menu. Duration, description, active status and
edit timestamps live on the salon link. Existing unknown durations stay null
until the owner provides a value. The editor requires 5-480 whole minutes.
The legacy integer price column is upgraded to decimal(10,2).

The migration copies older `SalonService` rows into the haircut menu once,
retaining their IDs, timestamps, descriptions and image paths. It does not delete
the old rows and subsequent reruns never overwrite menu edits. New owner writes
use the haircut table, not `SalonService`. ID conflicts across salons stop the
migration rather than reassigning a record. Unassigned haircut items remain
hidden until explicitly linked. Ambiguous duplicate source IDs remain read-only.

Writes accept multipart fields `serviceName`, `category`, `price`,
`durationMinutes`, `description`, `isActive`, and optional `image`, with header
`X-Salonexa-Request: 1`. New items require a photo; edits may retain the existing
photo, including an older item with no photo. Uploads require matching JPG, PNG
or WebP content types/signatures and must be at most 5 MB. Images are stored
under `Images/HairCutItemListImages/<random filename>` and served from the same
URL prefix. Uploaded filenames are never used as filesystem paths. Failed SQL
writes roll back both the item and salon link and remove the unused new image.
Replacing a photo preserves the old file for existing references.

Deactivation sets the source `IsDeleted` flag and link `IsActive=0`; the owner
continues to see the inactive item and can reactivate it. Independently deleted
source rows whose links remain active stay excluded. Duplicate service names
within a salon return HTTP 409; different salons may use the same name.
Name-only edits preserve existing tax amounts. Price edits preserve their existing
proportions and recalculate the taxable remainder. New items start with zero tax
amounts and taxable amount equal to price; there is no tax-rate field in this form.
Historical bill rows and booked amounts are never rewritten.

The matching dashboard uses this same catalogue for its service list and its
India-date-filtered active-service count. Existing public customer menu endpoints
retain their current contracts; this change scopes the authenticated owner
catalogue and writes, not the separate legacy global menu endpoint.

## Deployment

1. Back up the target database. After the existing booking-status migration, run
   `InfinityHairartsAPI/SQL_Update_SalonServices_2026-11-03.sql`. This combines
   `Database/SalonOwnerServices.sql` and `Database/SalonOwnerDashboard.sql`.
   Stop execution on any SQL error; do not continue deploying a partial migration.
2. If original Infinity Hair Arts items are not linked yet, run
   `SQL_Link_InfinityHairArts_Services_2026-11-01.sql`. It assigns only unlinked
   items to the uniquely named Infinity Hair Arts salon. Never use it to assign
   another salon's menu; supply explicit item/salon links for that salon.
3. Deploy the matching API release. Retain server connection settings, persistent
   authentication keys and existing image folders. The API must be able to write
   `Images/HairCutItemListImages`. Retain `ServiceImages` for migrated older photos.
4. Install/upload the matching rebuilt Android app. An AAB alone cannot update
   server behavior; the API and SQL migration are required too.
5. Sign in, add a service, edit its name/price, reload the list, and confirm the
   matching haircut row and salon link. Verify another salon cannot edit it.

The implementation was checked using disposable local SQL databases and upload
folders. Application data and the hosted API/database were not changed.

## Verification

```powershell
dotnet run --no-restore --project tests/OwnerServicesChecks --configuration Release -- D:\GitHub\UpdatedInfinityHairartsAPI\InfinityHairartsAPI\appsettings.json
dotnet run --no-restore --project tests/BookingQrCompletionChecks --configuration Release -- D:\GitHub\UpdatedInfinityHairartsAPI\InfinityHairartsAPI\appsettings.json
```

Checks cover actual HTTP saves and SQL persistence, authentication, salon
isolation, decimal prices, original item IDs, missing/replacement photos,
category references, availability/reactivation, duplicates, rollback, migration
reruns, legacy data preservation, dashboard counts and booking regressions.
