using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class LocationController : ControllerBase
{
    private readonly LocationService _locationService;

    public LocationController(LocationService locationService)
    {
        _locationService = locationService;
    }

    [HttpGet("getLocations")]
    [HttpGet("/api/Admin/signup-locations")]
    public async Task<ActionResult<IReadOnlyList<LocationMasterModal>>> GetLocations()
    {
        return Ok(await _locationService.GetLocationsAsync());
    }

    [HttpGet("salons")]
    public async Task<ActionResult<SalonDirectoryPage>> GetDirectory(
        [FromQuery] string? search = null, [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _locationService.GetDirectoryAsync(search, cursor, pageSize, cancellationToken));
        }
        catch (ArgumentException error)
        {
            return BadRequest(new { message = error.Message });
        }
    }

    [HttpGet("{locationMasterId:guid}/salons")]
    public async Task<ActionResult<IReadOnlyList<SalonMasterModal>>> GetSalonsByLocation(
        Guid locationMasterId)
    {
        return Ok(await _locationService.GetSalonsByLocationAsync(locationMasterId));
    }
}
