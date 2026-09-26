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
    public async Task<ActionResult<IReadOnlyList<LocationMasterModal>>> GetLocations()
    {
        return Ok(await _locationService.GetLocationsAsync());
    }

    [HttpGet("{locationMasterId:guid}/salons")]
    public async Task<ActionResult<IReadOnlyList<SalonMasterModal>>> GetSalonsByLocation(
        Guid locationMasterId)
    {
        return Ok(await _locationService.GetSalonsByLocationAsync(locationMasterId));
    }
}
