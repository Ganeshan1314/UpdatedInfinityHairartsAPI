using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SalonexaAdminAPI.Modals;
using SalonexaAdminAPI.Services;

namespace SalonexaAdminAPI.Controllers;

/// <summary>The signed-in owner's own salon. The salon ID always comes from the owner ticket, never from the request.</summary>
[ApiController, Route("api/Admin/salon")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class AdminSalonController(SalonOwnerSalonProfileService salons, ILogger<AdminSalonController> logger) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(CancellationToken cancellationToken) => ForOwner(async owner =>
    {
        var profile = await salons.GetAsync(owner.SalonOwnerID, owner.SalonMasterID, cancellationToken);
        return profile is null ? SignInRequired() : Ok(profile);
    });

    [HttpPut]
    public Task<IActionResult> Save([FromBody] OwnerSalonProfileRequest request, CancellationToken cancellationToken) =>
        ForOwner(async owner =>
        {
            // The salon name and address are shown to customers, so they follow the same content rules.
            var rejection = PublicTextRules.Rejection(("salon name", request.SalonName), ("salon address", request.SalonAddress));
            if (rejection is not null) return BadRequest(new { message = rejection });
            return Ok(await salons.SaveAsync(owner.SalonOwnerID, owner.SalonMasterID, request, cancellationToken));
        });

    private async Task<IActionResult> ForOwner(Func<SalonOwnerLoginResponse, Task<IActionResult>> action)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return SignInRequired();
        try { return await action(owner); }
        catch (SqlException exception) when (exception.Number == 50013) { return StatusCode(StatusCodes.Status403Forbidden, new { message = "Please sign in again." }); }
        catch (SqlException exception) when (exception.Number == 50015) { return BadRequest(new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number is 2812 or 208 or 207)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The salon database update is required. Please contact support." });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The salon profile operation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Salon details are temporarily unavailable. Please try again." });
        }
    }

    private UnauthorizedObjectResult SignInRequired() => Unauthorized(new { message = "Please sign in again." });
}
