using SalonexaAdminAPI.Modals;
using SalonexaAdminAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace SalonexaAdminAPI.Controllers;

[ApiController, Route("api/Admin/services")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SalonOwnerServicesController(SalonOwnerServicesReader services) : ControllerBase
{
    private const int MaximumRequestSize = 6 * 1024 * 1024;

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
            return CatalogueUpdateRequired();
        }
    }

    [HttpPost]
    [RequestSizeLimit(MaximumRequestSize), RequestFormLimits(MultipartBodyLengthLimit = MaximumRequestSize)]
    public Task<IActionResult> Create([FromForm] SalonOwnerServiceRequest request,
        [FromServices] SalonOwnerServicesWriter writer, CancellationToken cancellationToken) =>
        Save(null, request, writer, cancellationToken);

    [HttpPut("{serviceId:guid}")]
    [RequestSizeLimit(MaximumRequestSize), RequestFormLimits(MultipartBodyLengthLimit = MaximumRequestSize)]
    public Task<IActionResult> Update(Guid serviceId, [FromForm] SalonOwnerServiceRequest request,
        [FromServices] SalonOwnerServicesWriter writer, CancellationToken cancellationToken) =>
        Save(serviceId, request, writer, cancellationToken);

    private async Task<IActionResult> Save(Guid? serviceId, SalonOwnerServiceRequest request,
        SalonOwnerServicesWriter writer, CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized(new { message = "Please sign in again." });
        // Multipart forms are not preflighted; require the app's custom header.
        if (Request.Headers["X-Salonexa-Request"] != "1")
            return BadRequest(new { message = "Save services using the application." });
        try
        {
            return Ok(await writer.SaveAsync(owner.SalonOwnerID, owner.SalonMasterID, serviceId, request, cancellationToken));
        }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
        catch (KeyNotFoundException exception) { return NotFound(new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number == 50013)
        { return StatusCode(StatusCodes.Status403Forbidden, new { message = "Please sign in again." }); }
        catch (SqlException exception) when (exception.Number == 50016)
        { return NotFound(new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number == 50015)
        { return BadRequest(new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number is 50017 or 2601 or 2627)
        { return Conflict(new { message = "A service with this name already exists in your salon." }); }
        catch (SqlException exception) when (exception.Number is 2812 or 208 or 207)
        { return CatalogueUpdateRequired(); }
    }

    private ObjectResult CatalogueUpdateRequired() => StatusCode(StatusCodes.Status503ServiceUnavailable,
        new { message = "The service catalogue database update is required. Please contact support." });
}
