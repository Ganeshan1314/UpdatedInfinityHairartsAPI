using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers;

[ApiController, Route("api/Admin/services")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SalonOwnerServicesController(SalonOwnerServicesReader services) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized(new { message = "Please sign in again." });
        try
        {
            return Ok(await services.GetAsync(owner.SalonOwnerID, owner.SalonMasterID, cancellationToken));
        }
        catch (SqlException error) when (error.Number == 50013)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Please sign in again." });
        }
        catch (SqlException error) when (error.Number is 2812 or 208 or 207)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The service catalogue database update is required. Please contact support." });
        }
    }
}
