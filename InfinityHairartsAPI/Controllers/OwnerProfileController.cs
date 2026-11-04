using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[Route("api/Admin/owner-profile")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OwnerProfileController(IConfiguration configuration) : ControllerBase
{
    private readonly PasswordHasher<string> hasher = new();

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        if (!TryGetOwner(out var ownerId, out var salonId)) return SignInRequired();
        await using var connection = Connection();
        var profile = await ReadProfile(connection, ownerId, salonId, token);
        return profile is null ? SignInRequired() : Ok(ToProfileResponse(profile));
    }

    [HttpPut]
    public async Task<IActionResult> Save(ProfileRequest request, CancellationToken token)
    {
        if (!TryGetWritableOwner(out var ownerId, out var salonId)) return SignInRequired();
        if (Request.Headers["X-Salonexa-Request"] != "1") return UseApplication();
        await using var connection = Connection();
        var credential = await ReadCredential(connection, ownerId, salonId, token);
        if (credential is null) return SignInRequired();
        try
        {
            var changed = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE SO SET OwnerName=@name, EmailAddress=@email, UpdatedUtc=SYSUTCDATETIME()
                FROM dbo.SalonOwner AS SO
                INNER JOIN dbo.SalonMaster AS SM ON SM.SalonMasterID=SO.SalonMasterID
                WHERE SO.SalonOwnerID=@ownerId AND SO.SalonMasterID=@salonId
                  AND SO.IsActive=1 AND SM.IsActive=1
                  AND SO.PasswordHash COLLATE Latin1_General_100_BIN2=@expectedHash;
                """, new { ownerId, salonId, name = request.OwnerName.Trim(), email = request.EmailAddress.Trim(),
                    expectedHash = credential.PasswordHash }, cancellationToken: token));
            if (changed != 1) return AccountChanged();
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            return Conflict(new { message = "That email address is already used by another account." });
        }
        var profile = await ReadProfile(connection, ownerId, salonId, token);
        return profile is null ? SignInRequired() : Ok(ToProfileResponse(profile));
    }

    [HttpPost("~/api/Admin/change-username")]
    public async Task<IActionResult> ChangeUsername(UsernameRequest request,
        [FromServices] SalonOwnerPersistentSignIn signIn, CancellationToken token)
    {
        if (!TryGetWritableOwner(out var ownerId, out var salonId)) return SignInRequired();
        if (Request.Headers["X-Salonexa-Request"] != "1") return UseApplication();
        var userName = request.NewUserName.Trim();
        if (!Regex.IsMatch(userName, @"\A[A-Za-z0-9._-]{3,100}\z"))
            return BadRequest(new { message = "Use 3–100 letters, numbers, dots, underscores or hyphens for your username." });
        await using var connection = Connection();
        var credential = await ReadCredential(connection, ownerId, salonId, token);
        if (credential is null) return SignInRequired();
        if (!Verify(credential, request.CurrentPassword)) return IncorrectPassword();
        if (string.Equals(userName, credential.UserName, StringComparison.Ordinal))
            return BadRequest(new { message = "Choose a different username." });
        // A fresh salt changes the authentication stamp, revoking other tickets
        // while preserving the current password for the new username.
        var passwordHash = hasher.HashPassword(userName, request.CurrentPassword);
        try
        {
            var changed = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE SO SET UserName=@userName, PasswordHash=@passwordHash, UpdatedUtc=SYSUTCDATETIME()
                FROM dbo.SalonOwner AS SO
                INNER JOIN dbo.SalonMaster AS SM ON SM.SalonMasterID=SO.SalonMasterID
                WHERE SO.SalonOwnerID=@ownerId AND SO.SalonMasterID=@salonId
                  AND SO.IsActive=1 AND SM.IsActive=1
                  AND SO.PasswordHash COLLATE Latin1_General_100_BIN2=@expectedHash;
                """, new { ownerId, salonId, userName, passwordHash, expectedHash = credential.PasswordHash },
                cancellationToken: token));
            if (changed != 1) return AccountChanged();
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            return Conflict(new { message = "This username is already taken. Choose another username." });
        }
        await signIn.SignOutAsync(HttpContext);
        return Ok(new { message = "Your username was updated. Please sign in again." });
    }

    [HttpPost("~/api/Admin/change-password")]
    public async Task<IActionResult> ChangePassword(PasswordRequest request,
        [FromServices] SalonOwnerPersistentSignIn signIn, CancellationToken token)
    {
        if (!TryGetWritableOwner(out var ownerId, out var salonId)) return SignInRequired();
        if (Request.Headers["X-Salonexa-Request"] != "1") return UseApplication();
        await using var connection = Connection();
        var credential = await ReadCredential(connection, ownerId, salonId, token);
        if (credential is null) return SignInRequired();
        if (!Verify(credential, request.CurrentPassword)) return IncorrectPassword();
        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
            return BadRequest(new { message = "Choose a different password." });
        var passwordHash = hasher.HashPassword(credential.UserName, request.NewPassword);
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE SO SET PasswordHash=@passwordHash, UpdatedUtc=SYSUTCDATETIME()
            FROM dbo.SalonOwner AS SO
            INNER JOIN dbo.SalonMaster AS SM ON SM.SalonMasterID=SO.SalonMasterID
            WHERE SO.SalonOwnerID=@ownerId AND SO.SalonMasterID=@salonId
              AND SO.IsActive=1 AND SM.IsActive=1
              AND SO.PasswordHash COLLATE Latin1_General_100_BIN2=@expectedHash;
            """, new { ownerId, salonId, passwordHash, expectedHash = credential.PasswordHash }, cancellationToken: token));
        if (changed != 1) return AccountChanged();
        await signIn.SignOutAsync(HttpContext);
        return Ok(new { message = "Your password was updated. Please sign in again." });
    }

    private SqlConnection Connection() => new(configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The database connection is not configured."));

    private bool TryGetOwner(out Guid ownerId, out Guid salonId)
    {
        var ownerValid = Guid.TryParse(HttpContext.Session.GetString("SalonOwnerID"), out ownerId) && ownerId != Guid.Empty;
        var salonValid = Guid.TryParse(HttpContext.Session.GetString("SalonMasterID"), out salonId) && salonId != Guid.Empty;
        return ownerValid && salonValid;
    }

    private bool TryGetWritableOwner(out Guid ownerId, out Guid salonId)
    {
        var valid = TryGetOwner(out ownerId, out salonId);
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        return valid && owner is not null && owner.SalonOwnerID == ownerId && owner.SalonMasterID == salonId;
    }

    private async Task<Credential?> ReadCredential(SqlConnection connection, Guid ownerId, Guid salonId, CancellationToken token)
    {
        var credential = await connection.QuerySingleOrDefaultAsync<Credential>(new CommandDefinition("""
            SELECT SO.UserName, SO.PasswordHash FROM dbo.SalonOwner AS SO
            INNER JOIN dbo.SalonMaster AS SM ON SM.SalonMasterID=SO.SalonMasterID
            WHERE SO.SalonOwnerID=@ownerId AND SO.SalonMasterID=@salonId AND SO.IsActive=1 AND SM.IsActive=1;
            """, new { ownerId, salonId }, cancellationToken: token));
        if (credential is null) return null;
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.PasswordHash)));
        return stamp == SalonOwnerPersistentSignIn.RestoredOwner(HttpContext)?.AuthenticationStamp ? credential : null;
    }

    private bool Verify(Credential credential, string password)
    {
        try { return hasher.VerifyHashedPassword(credential.UserName, credential.PasswordHash, password) != PasswordVerificationResult.Failed; }
        catch (Exception exception) when (exception is FormatException or ArgumentException) { return false; }
    }

    private static Task<AdminDetails?> ReadProfile(SqlConnection connection, Guid ownerId, Guid salonId, CancellationToken token) =>
        connection.QuerySingleOrDefaultAsync<AdminDetails>(new CommandDefinition("""
            SELECT SO.OwnerName, SO.UserName, COALESCE(SO.EmailAddress,N'') AS EmailAddress, SM.SalonName
            FROM dbo.SalonOwner AS SO INNER JOIN dbo.SalonMaster AS SM ON SM.SalonMasterID=SO.SalonMasterID
            WHERE SO.SalonOwnerID=@ownerId AND SO.SalonMasterID=@salonId AND SO.IsActive=1 AND SM.IsActive=1;
            """, new { ownerId, salonId }, cancellationToken: token));

    private static object ToProfileResponse(AdminDetails profile) => new {
        profile.OwnerName, profile.UserName, profile.EmailAddress, profile.SalonName,
        readOnly = false, canChangeUserName = true
    };
    private UnauthorizedObjectResult SignInRequired() => Unauthorized(new { message = "Please sign in again." });
    private BadRequestObjectResult UseApplication() => BadRequest(new { message = "Use the application to update your account." });
    private BadRequestObjectResult IncorrectPassword() => BadRequest(new { message = "Your current password is incorrect." });
    private ConflictObjectResult AccountChanged() => Conflict(new { message = "Your account changed during this request. Please sign in again." });

    public sealed class ProfileRequest
    {
        [Required, MaxLength(150)] public string OwnerName { get; set; } = "";
        [Required, MaxLength(254), EmailAddress]
        [RegularExpression(@"(?i)^[^@\s]+@gmail[.]com$", ErrorMessage = "Enter a valid Gmail address ending in @gmail.com.")]
        public string EmailAddress { get; set; } = "";
    }
    public class PasswordProof
    {
        [Required, MaxLength(200)] public string CurrentPassword { get; set; } = "";
    }
    public sealed class UsernameRequest : PasswordProof
    {
        [Required, MaxLength(100)] public string NewUserName { get; set; } = "";
    }
    public sealed class PasswordRequest : PasswordProof
    {
        [Required, StringLength(128, MinimumLength = 8)] public string NewPassword { get; set; } = "";
        [Required, Compare(nameof(NewPassword))] public string ConfirmPassword { get; set; } = "";
    }
    private sealed class Credential
    {
        public string UserName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
    }
    private sealed class AdminDetails
    {
        public string OwnerName { get; set; } = "";
        public string UserName { get; set; } = "";
        public string EmailAddress { get; set; } = "";
        public string SalonName { get; set; } = "";
    }
}
