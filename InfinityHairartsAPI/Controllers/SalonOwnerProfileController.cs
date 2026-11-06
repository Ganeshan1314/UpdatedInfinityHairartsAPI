using Dapper;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;

namespace InfinityHairartsAPI.Controllers;

[ApiController, Route("api/Admin/owner-profile")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SalonOwnerProfileController(IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized(new { message = "Please sign in again." });

        await using var connection = new SqlConnection(configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is required."));
        var profile = await connection.QuerySingleOrDefaultAsync<OwnerProfile>(new CommandDefinition(
            "dbo.GetSalonOwnerProfile", new { OwnerID = owner.SalonOwnerID, SalonID = owner.SalonMasterID },
            commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
        return profile is null ? Unauthorized(new { message = "Please sign in again." }) : Ok(profile);
    }

    // This release supplies the missing profile read route. The UI already supports
    // read-only profiles; do not advertise account edits that the API cannot save.
    public sealed record OwnerProfile(string OwnerName, string UserName, string EmailAddress, string SalonName)
    {
        public bool ReadOnly => true;
        public bool CanChangeUserName => false;
    }
}
