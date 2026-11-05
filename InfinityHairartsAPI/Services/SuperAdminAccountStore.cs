using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public sealed record SuperAdminAccount(Guid SuperAdminID, string UserName, string DisplayName,
    string PasswordHash, string Role, bool IsActive);

public interface ISuperAdminAccountStore
{
    Task<SuperAdminAccount?> FindByUserNameAsync(string userName, CancellationToken cancellationToken);
    Task<SuperAdminAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> MarkLoginAsync(SuperAdminAccount account, CancellationToken cancellationToken);
}

public sealed class SuperAdminAccountStore(IConfiguration configuration) : ISuperAdminAccountStore
{
    private const string SelectAccount = "SELECT SuperAdminID, UserName, DisplayName, PasswordHash, Role, IsActive FROM dbo.SuperAdmin ";
    private SqlConnection Connection() => new(configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));

    public async Task<SuperAdminAccount?> FindByUserNameAsync(string userName, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return await connection.QuerySingleOrDefaultAsync<SuperAdminAccount>(new CommandDefinition(
            SelectAccount + "WHERE UserName = @userName", new { userName }, cancellationToken: cancellationToken));
    }

    public async Task<SuperAdminAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return await connection.QuerySingleOrDefaultAsync<SuperAdminAccount>(new CommandDefinition(
            SelectAccount + "WHERE SuperAdminID = @id", new { id }, cancellationToken: cancellationToken));
    }

    public async Task<bool> MarkLoginAsync(SuperAdminAccount account, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.SuperAdmin SET LastLoginUtc = SYSUTCDATETIME()
            WHERE SuperAdminID = @SuperAdminID AND UserName = @UserName AND PasswordHash = @PasswordHash
                AND Role = 'SuperAdmin' AND IsActive = 1
            """, account, cancellationToken: cancellationToken)) == 1;
    }

    // Used by the local provisioning tool, never exposed through public signup.
    public async Task<Guid> CreateAsync(string userName, string displayName, string passwordHash, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(userName, @"\A[A-Za-z0-9._-]{3,100}\z")
            || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 150
            || string.IsNullOrWhiteSpace(passwordHash) || passwordHash.Length > 512)
            throw new ArgumentException("Use a 3-100 character username (letters, numbers, dot, underscore or hyphen), a display name up to 150 characters and a password hash.");
        await using var connection = Connection();
        return await connection.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT dbo.SuperAdmin(UserName, DisplayName, PasswordHash)
            OUTPUT INSERTED.SuperAdminID VALUES(@userName, @displayName, @passwordHash)
            """, new { userName, displayName = displayName.Trim(), passwordHash }, cancellationToken: cancellationToken));
    }
}
