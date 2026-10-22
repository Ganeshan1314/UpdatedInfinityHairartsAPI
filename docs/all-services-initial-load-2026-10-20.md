# Show every service on initial load

The live `POST /api/Admin/GetListHairCutTpyes` response contained only four combos
(prices 330, 370, 420 and 520). Its stored procedure, `getHairCut_Item_List`, filters
to `HairCut_GroupMaster.OrderID = 1`. Sorting that response cannot show the missing
Hair Cut or Facial services.

`GetListHairCutTpyes` now calls `getAllHairCut_Item_List`, which returns all groups
ordered by price ascending with the same result columns. Category filtering continues
to use the existing procedures. No inline SQL was added to application code.

## Deployment

1. Run `InfinityHairartsAPI/Database/ServiceCatalog.sql` in the production API database.
   This creates a separate procedure; existing procedures and service data are preserved.
2. Publish the updated API using the normal deployment process, preserving server
   configuration, credentials and uploaded files.
3. Read `POST /api/Admin/GetListHairCutTpyes`. Verify that `item2` includes all service
   groups and the expected 90-price service, with prices ascending.
4. Reopen Book Appointment in the updated app. Before selecting a category, the option
   count should match the full named service list and the lowest price should be first.

Publishing the API without running the SQL script first will make the endpoint fail.
The earlier frontend-only sorting change could not fix the incomplete API response.

## Local verification

Apply the script to the local `InfinityHairArtsAdmin` database, then run:

```powershell
dotnet run --project tests/ServiceCatalogChecks -- (Resolve-Path InfinityHairartsAPI/appsettings.json).Path
```

The checks read local data through the actual controller and stored procedures,
verify all three categories are included, numeric price ordering and unique IDs,
and confirm the original combo procedure still returns its original count.
They start an isolated HTTP host without notification workers and do not change data.
Local catalog prices differ from production; production's 90-price service must be
confirmed after deployment.
