using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SalonexaAdminAPI.Modals;
using SalonexaAdminAPI.Services;

namespace SalonexaAdminAPI.Controllers;

/// <summary>
/// Service areas shown when a salon owner chooses their salon location. The customer salon
/// directory endpoints are not part of this host.
/// </summary>
[ApiController]
public sealed class AdminLocationController(LocationService locationService, ILogger<AdminLocationController> logger) : ControllerBase
{
    // The admin app asks for signup-locations first and falls back to Location/getLocations.
    [HttpGet("api/Admin/signup-locations")]
    [HttpGet("api/Location/getLocations")]
    public async Task<ActionResult<IReadOnlyList<LocationMasterModal>>> GetLocations(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var outcome = "failed";
        var cacheStatus = "unavailable";
        try
        {
            var result = await locationService.GetLocationsAsync(deadline.Token);
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
            logger.LogWarning("Location SQL failed: SqlErrorNumber={SqlErrorNumber} TraceId={TraceId}",
                error.Number, Activity.Current?.TraceId.ToString());
            return StatusCode(503, new { message = "Locations are temporarily unavailable. Please try again." });
        }
        finally
        {
            var elapsedMs = clock.Elapsed.TotalMilliseconds;
            Response.Headers["Server-Timing"] = "locations;dur=" + elapsedMs.ToString("F1", CultureInfo.InvariantCulture)
                + ", location-cache;desc=\"" + cacheStatus + "\"";
            logger.LogInformation(
                "Location API timing: Outcome={Outcome} Cache={Cache} TotalMs={TotalMs:F1} TraceId={TraceId}",
                outcome, cacheStatus, elapsedMs, Activity.Current?.TraceId.ToString());
        }
    }
}
