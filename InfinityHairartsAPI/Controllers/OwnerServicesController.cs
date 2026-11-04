using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[Route("api/Admin/services")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
[RequestSizeLimit(6 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
public sealed class OwnerServicesController(SalonOwnerServicesService services) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(CancellationToken cancellationToken) =>
        WithOwner((owner, salon) => services.GetAsync(owner, salon, cancellationToken));

    [HttpPost]
    public Task<IActionResult> Create([FromForm] SalonOwnerServiceRequest request, CancellationToken cancellationToken) =>
        Save(null, request, cancellationToken);

    [HttpPut("{serviceId:guid}")]
    public Task<IActionResult> Update(Guid serviceId, [FromForm] SalonOwnerServiceRequest request, CancellationToken cancellationToken) =>
        Save(serviceId, request, cancellationToken);

    private Task<IActionResult> Save(Guid? serviceId, SalonOwnerServiceRequest request, CancellationToken cancellationToken) =>
        WithOwner(async (owner, salon) => {
            if (Request.Headers["X-Salonexa-Request"] != "1")
                throw new ArgumentException("Save services using the application.");
            return await services.SaveAsync(owner, salon, serviceId, request, cancellationToken);
        });

    private async Task<IActionResult> WithOwner<T>(Func<Guid, Guid, Task<T>> action)
    {
        if (!Guid.TryParse(HttpContext.Session.GetString("SalonOwnerID"), out var owner)
            || !Guid.TryParse(HttpContext.Session.GetString("SalonMasterID"), out var salon))
            return Unauthorized(new { message = "Please sign in again." });
        try { return Ok(await action(owner, salon)); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
        catch (KeyNotFoundException exception) { return NotFound(new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number == 50013)
        { return StatusCode(403, new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number == 50016)
        { return NotFound(new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number == 50015)
        { return BadRequest(new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        { return Conflict(new { message = "A service with this name already exists in your salon." }); }
        catch (SqlException exception) when (exception.Number == 50017)
        { return Conflict(new { message = exception.Message }); }
    }
}
