using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class LocationController : ControllerBase
{
    private readonly LocationService _locationService;
    private readonly ILogger<LocationController> _logger;

    public LocationController(LocationService locationService, ILogger<LocationController> logger)
    {
        _locationService = locationService;
        _logger = logger;
    }

    [HttpGet("getLocations")]
    [HttpGet("/api/Admin/signup-locations")]
    public async Task<ActionResult<IReadOnlyList<LocationMasterModal>>> GetLocations(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var outcome = "failed";
        var cacheStatus = "unavailable";
        try
        {
            var result = await _locationService.GetLocationsAsync(deadline.Token);
            cacheStatus = result.CacheHit ? "hit" : "miss";
            outcome = "success";
            return Ok(result.Items);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = "timeout";
            return StatusCode(503, new { message = "Locations are taking too long to load. Please try again." });
        }
        catch (SqlException error)
        {
            outcome = error.Number == -2 ? "sql-timeout" : "sql-error";
            _logger.LogWarning("Location SQL failed: SqlErrorNumber={SqlErrorNumber} TraceId={TraceId}",
                error.Number, Activity.Current?.TraceId.ToString());
            return StatusCode(503, new { message = "Locations are temporarily unavailable. Please try again." });
        }
        finally
        {
            var elapsedMs = clock.Elapsed.TotalMilliseconds;
            Response.Headers["Server-Timing"] = "locations;dur=" + elapsedMs.ToString("F1", CultureInfo.InvariantCulture)
                + ", location-cache;desc=\"" + cacheStatus + "\"";
            _logger.LogInformation(
                "Location API timing: Outcome={Outcome} Cache={Cache} TotalMs={TotalMs:F1} TraceId={TraceId}",
                outcome, cacheStatus, elapsedMs, Activity.Current?.TraceId.ToString());
        }
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
