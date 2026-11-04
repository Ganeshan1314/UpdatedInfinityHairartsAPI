# Customer salon directory

The customer dashboard uses `GET /api/Location/salons?pageSize=20&search=` to
fetch one page across all active locations. The response is `{ items,
nextCursor }`. Cards include salon ID, location ID/name, salon name/address and
contact number. Only active salons approved for customer visibility are returned.
The location picker continues to use `GET /api/Location/getLocations`.

Search accepts up to 100 characters and matches salon or location names using
the database collation. Punctuation is treated literally. The page size defaults
to 20 and is bounded to 1–50. Follow `nextCursor` with the same search term;
`null` means the end. Cursors use the last display order, name and unique salon
ID, avoiding large OFFSET scans and duplicate pages when earlier rows are added.
Editing sort fields during browsing can still change a salon's position; refresh
starts a new traversal. Cancellation propagates to SQL when a search is replaced.

Deployment order:

1. Ensure the existing location, salon and super admin schema is deployed.
2. Run `InfinityHairartsAPI/Database/CustomerSalonDirectory.sql`. It adds a
   filtered covering index and `dbo.getCustomerSalons`; it is safe to rerun and
   does not change approval or visibility.
3. Deploy the API, then release the updated customer app. Older location endpoints
   remain available for previously installed app versions.

The migration has been exercised only in a disposable local test database; it is
not automatically applied to the application or production database. Contains
search still scans matching eligible index entries. Very large directories may
eventually need a separate search index; measure production query plans before
adding that infrastructure.

Validation:

```powershell
dotnet run --project tests/CustomerSalonDirectoryChecks/CustomerSalonDirectoryChecks.csproj
dotnet run --project tests/CustomerSalonDirectoryChecks/CustomerSalonDirectoryChecks.csproj -- --database
```

The first command checks the real HTTP controller with a fixture directory. The
second also tests the real service, migration, index, pagination and search in a
temporary SQL database which it removes afterward. Database checks accept only
local SQL Server hosts, using `SALON_DIRECTORY_TEST_CONNECTION` or the local API
connection settings. Neither command deploys schema to the application's database.
