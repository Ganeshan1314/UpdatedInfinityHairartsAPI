Run `dotnet run --project tests/OwnerSignInChecks --configuration Release` from the repository root.

These loopback HTTP checks run the actual owner login, session and logout controllers,
cookie authentication, session restoration middleware and persistent data protection.
Credential/account lookups use an in-memory fixture. The checks never connect to a
database, live API, Android device or real account.

They reproduce loss of a temporary session cookie, then verify restoration with only
the persistent owner cookie, a fresh API host/session cache, logout, expiry, tampering,
password-version revocation, inactive accounts, temporary repository errors and legacy
session upgrade. Temporary encryption keys are confined to the test build directory
and removed after the run.
