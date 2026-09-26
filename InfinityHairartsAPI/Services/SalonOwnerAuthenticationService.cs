using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using System.Data;

namespace InfinityHairartsAPI.Services;

public sealed class SalonOwnerAuthenticationService
{
    private readonly string _connectionString;
    private readonly PasswordHasher<SalonOwnerCredentialRecord> _passwordHasher = new();

    public SalonOwnerAuthenticationService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is not configured.");
    }

    public async Task<SalonOwnerLoginResponse?> AuthenticateAsync(
        SalonOwnerLoginRequest request,
        CancellationToken cancellationToken)
    {
        var userName = request.UserName.Trim();
        if (userName.Length == 0 || request.Password.Length == 0)
        {
            return null;
        }

        await using var connection = new SqlConnection(_connectionString);
        var credential = await connection.QuerySingleOrDefaultAsync<SalonOwnerCredentialRecord>(
            new CommandDefinition(
                "dbo.getSalonOwnerCredentialByUsername",
                new { UserName = userName },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        if (credential is null)
        {
            return null;
        }

        var verificationResult = _passwordHasher.VerifyHashedPassword(
            credential,
            credential.PasswordHash,
            request.Password);

        if (verificationResult == PasswordVerificationResult.Failed)
        {
            return null;
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.markSalonOwnerLoginSucceeded",
                new { credential.SalonOwnerID },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return ToResponse(credential);
    }

    public async Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(
        Guid salonOwnerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        var credential = await connection.QuerySingleOrDefaultAsync<SalonOwnerCredentialRecord>(
            new CommandDefinition(
                "dbo.getActiveSalonOwnerById",
                new { SalonOwnerID = salonOwnerId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return credential is null ? null : ToResponse(credential);
    }

    public async Task<SalonOwnerLoginResponse> InitializeAsync(
        InitializeSalonOwnerRequest request,
        CancellationToken cancellationToken)
    {
        var credential = new SalonOwnerCredentialRecord
        {
            SalonOwnerID = Guid.NewGuid(),
            SalonMasterID = request.SalonMasterID,
            OwnerName = request.OwnerName.Trim(),
            UserName = request.UserName.Trim()
        };

        var passwordHash = _passwordHasher.HashPassword(credential, request.Password);

        await using var connection = new SqlConnection(_connectionString);
        var createdOwner = await connection.QuerySingleAsync<SalonOwnerCredentialRecord>(
            new CommandDefinition(
                "dbo.initializeSalonOwner",
                new
                {
                    credential.SalonOwnerID,
                    credential.SalonMasterID,
                    credential.OwnerName,
                    credential.UserName,
                    PasswordHash = passwordHash,
                    EmailAddress = string.IsNullOrWhiteSpace(request.EmailAddress)
                        ? null
                        : request.EmailAddress.Trim()
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return ToResponse(createdOwner);
    }

    private static SalonOwnerLoginResponse ToResponse(SalonOwnerCredentialRecord credential)
    {
        return new SalonOwnerLoginResponse
        {
            SalonOwnerID = credential.SalonOwnerID,
            SalonMasterID = credential.SalonMasterID,
            OwnerName = credential.OwnerName,
            UserName = credential.UserName,
            SalonName = credential.SalonName
        };
    }

    private sealed class SalonOwnerCredentialRecord
    {
        public Guid SalonOwnerID { get; set; }
        public Guid SalonMasterID { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string SalonName { get; set; } = string.Empty;
    }
}
