# Owner service editing (2026-11-07)

## Problem

In Salonexa Admin, **Services** showed no **Edit service** button, and saving a
new service failed.

- `GET /api/Admin/services` (`Database/SalonOwnerServicesRead.sql`) returned the
  salon's existing haircut menu items with `CanEdit = 0`. The admin app hides
  Edit when `canEdit` is false.
- `main` had no `POST /api/Admin/services` or `PUT /api/Admin/services/{id}`.
  The write path existed only on the unmerged branch
  `changes/owner-api-updates-2026-11-04`.

## Fix

The services part of that branch is ported to `main`, using `main`'s persistent
owner sign-in (`SalonOwnerPersistentSignIn.RestoredOwner`) instead of raw
session values.

| Change | File |
|---|---|
| Add `POST` and `PUT {serviceId}` (multipart, 6 MB limit, `X-Salonexa-Request: 1` header required) | `Controllers/SalonOwnerServicesController.cs` |
| Image validation and storage, then `dbo.SaveSalonOwnerService` | `Services/SalonOwnerServicesWriter.cs` |
| Request model; duration fixed at 30 | `Modals/SalonOwnerServiceRequest.cs` |
| Add view `SalonOwnerServiceCatalogue` and procedure `SaveSalonOwnerService`; replace `GetSalonOwnerServices`. Linked menu items become editable. | `Database/SalonOwnerServices.sql` (replaces `SalonOwnerServicesRead.sql`) |
| Dashboard service list and active count use the same catalogue | `Database/SalonOwnerDashboard.sql` |

Saved services are stored in `HairCut_Item_List`, linked to the salon through
`SalonHairCutItem` (duration, description and active flag live on the link).
Older `SalonService` rows are copied into the menu once and never deleted. When
the price changes, CGST, SGST and the taxable amount are rescaled in the same
proportions. Bills are never rewritten. `HairCut_Item_Price` is changed from
`int` to `decimal(10,2)`.

## Deploy

1. Back up the database.
2. Run `InfinityHairartsAPI/SQL_Update_ServiceEditing_2026-11-07.sql`. It contains
   `SalonOwnerServices.sql` followed by `SalonOwnerDashboard.sql`, and both parts
   can be rerun. Stop on the first error.
3. Publish the API. Keep the server's `appsettings.json`, `App_Data`, and the
   `Images/HairCutItemListImages` folder. The app pool account must be able to
   write to that folder.
4. The admin app needs no update. After signing in, the owner sees
   **Edit service** on their menu items.

Do not run `SalonOwnerServicesRead.sql` or the admin repository's
`deployment/api/SQL_Update_ServiceLoading_2026-11-05.sql` again. They restore
the read-only procedure, which hides Edit again.

## Verify

```powershell
dotnet run --project tests/OwnerServicesChecks -c Release
```

65 HTTP/SQL checks run against a disposable local database. They cover sign-in,
salon isolation, create, edit, deactivate and reactivate, photo upload and
replacement, rollback, duplicate names, the fixed 30-minute duration, tax
rescaling, migration reruns, and missing-procedure errors.

## Known limitation (customer app)

The customer menu procedure `dbo.getHairCut_Item_List` is not filtered by salon
or by `IsDeleted`. As a result:

- Name and price edits change what every customer sees.
- Services added by any salon appear in the shared customer menu when their
  category's `OrderID` matches the menu filter.
- Deactivated services may still be listed for customers.

Scoping the customer menu to the selected salon is a separate change to the
customer app's API.
