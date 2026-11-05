using System.Security.Claims;
using Dapper;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static class AccountDatabaseChecks
{
    public static async Task<int> Run(SqlConnection connection, string connectionString, string root)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS ACCOUNT SQL: " + message); }
        var script = await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "SuperAdminAccounts.sql"));
        await connection.ExecuteAsync(script);
        await connection.ExecuteAsync(script);
        Check(await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.SuperAdmin") == 0, "The account migration is rerunnable and creates no default credentials");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:dbconnection"] = connectionString,
            // Old environment credentials must not act as a fallback.
            ["SuperAdmin:UserName"] = "environment-owner",
            ["SuperAdmin:PasswordHash"] = new PasswordHasher<string>().HashPassword("environment-owner", "environment-password")
        }).Build();
        var store = new SuperAdminAccountStore(configuration);
        var auth = new SuperAdminAuthentication(store);
        const string password = "fixture-account-password";
        var hash = new PasswordHasher<string>().HashPassword("application-owner", password);
        var id = await store.CreateAsync("application-owner", "Application owner", hash, default);
        var account = (await store.FindByIdAsync(id, default))!;
        Check(account.UserName == "application-owner" && account.IsActive && account.Role == "SuperAdmin", "The dedicated table stores its own account, active state and fixed role");
        Check(account.PasswordHash == hash && account.PasswordHash != password, "Only the Identity password hash is stored");
        Check((await store.FindByUserNameAsync("application-owner", default))?.SuperAdminID == id, "Username lookup maps the persisted account");
        Check(await store.FindByUserNameAsync("Application-owner", default) is null, "Username matching is case-sensitive");
        await connection.ExecuteAsync(script);
        Check((await store.FindByIdAsync(id, default))?.PasswordHash == hash, "Rerunning the migration preserves existing credentials");
        try {
            await store.CreateAsync("application-owner", "Duplicate", hash, default);
            throw new Exception("Expected a rejected duplicate username");
        } catch (SqlException error) when (error.Number is 2601 or 2627) { Check(true, "The database rejects duplicate usernames"); }
        try {
            await connection.ExecuteAsync("UPDATE dbo.SuperAdmin SET Role = 'SalonOwner' WHERE SuperAdminID = @id", new { id });
            throw new Exception("Expected a rejected role");
        } catch (SqlException error) when (error.Number == 547) { Check(true, "The table cannot be assigned a salon-owner role"); }
        var login = await auth.AuthenticateAsync(new() { UserName = "application-owner", Password = password }, default);
        Check(login?.Session.SuperAdminID == id && login.Session.Role == "SuperAdmin", "Real database credentials authenticate with the dedicated role");
        Check(await connection.QuerySingleAsync<DateTime?>("SELECT LastLoginUtc FROM dbo.SuperAdmin WHERE SuperAdminID = @id", new { id }) is not null, "Successful sign-in records the last login time");
        Check(await auth.AuthenticateAsync(new() { UserName = "application-owner", Password = "wrong-password" }, default) is null, "An incorrect password is rejected");
        Check(await auth.AuthenticateAsync(new() { UserName = "environment-owner", Password = "environment-password" }, default) is null, "Old server-config credentials do not grant database account access");
        ClaimsPrincipal Principal(SuperAdminAccount row) => new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, row.SuperAdminID.ToString()), new Claim(ClaimTypes.Name, row.UserName),
            new Claim(ClaimTypes.Role, row.Role), new Claim(SuperAdminAuthentication.StampClaim, SuperAdminAuthentication.StampFor(row))
        ], SuperAdminAuthentication.Scheme));
        var principal = Principal(account);
        Check((await auth.ValidateAsync(principal, default))?.SuperAdminID == id, "A ticket restores the current database account by ID");
        await connection.ExecuteAsync("UPDATE dbo.SuperAdmin SET IsActive = 0 WHERE SuperAdminID = @id", new { id });
        Check(await auth.ValidateAsync(principal, default) is null && await auth.AuthenticateAsync(new() { UserName = "application-owner", Password = password }, default) is null,
            "Disabling the account blocks both sign-in and existing sessions");
        var replacementHash = new PasswordHasher<string>().HashPassword("application-owner", "replacement-password");
        await connection.ExecuteAsync("UPDATE dbo.SuperAdmin SET IsActive = 1, PasswordHash = @replacementHash WHERE SuperAdminID = @id", new { id, replacementHash });
        Check(await auth.ValidateAsync(principal, default) is null, "Changing the password hash revokes existing tickets");
        Check(!await store.MarkLoginAsync(account, default), "A concurrent credential change rejects the stale login write");
        var secondId = await store.CreateAsync("second-owner", "Second owner", hash, default);
        var second = (await store.FindByIdAsync(secondId, default))!;
        Check((await auth.ValidateAsync(Principal(second), default))?.SuperAdminID == secondId, "Each super admin has an independent database identity");
        await connection.ExecuteAsync("DELETE dbo.SuperAdmin WHERE SuperAdminID = @id", new { id });
        Check(await auth.ValidateAsync(principal, default) is null && (await auth.ValidateAsync(Principal(second), default))?.SuperAdminID == secondId,
            "Deleting one account does not affect another administrator");
        await connection.ExecuteAsync("UPDATE dbo.SuperAdmin SET PasswordHash = N'invalid hash' WHERE SuperAdminID = @secondId", new { secondId });
        Check(await auth.AuthenticateAsync(new() { UserName = "second-owner", Password = password }, default) is null, "Malformed password hashes fail authentication");
        Console.WriteLine($"Passed {checks} super-admin account SQL checks.");
        return checks;
    }
}
