using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers;

[ApiController, Route("api/Admin/appointments")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SalonOwnerAppointmentsController(SalonOwnerAppointmentsReader appointments) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken, [FromQuery] string status = "Pending", [FromQuery] int page = 1)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized(new { message = "Please sign in again." });
        try
        {
            return Ok(await appointments.GetAsync(owner.SalonOwnerID, owner.SalonMasterID,
                fromDate, toDate, status, page, cancellationToken));
        }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (SqlException error) when (error.Number == 50002)
        {
            return BadRequest(new { message = error.Message });
        }
        catch (SqlException error) when (error.Number == 50013)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Please sign in again." });
        }
        catch (SqlException error) when (error.Number is 2812 or 208 or 207)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The appointment database update is required. Please contact support." });
        }
    }
}
