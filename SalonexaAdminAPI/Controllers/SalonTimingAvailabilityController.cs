using System.ComponentModel.DataAnnotations;
using System.Globalization;
using SalonexaAdminAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace SalonexaAdminAPI.Controllers;

[ApiController, Route("api/Admin/availability")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SalonTimingAvailabilityController(SalonTimingAvailability availability) : ControllerBase
{
    // No date means the daily (every day) schedule. A date must be yyyy-MM-dd.
    private static bool TryDate(string? value, out DateTime? date)
    {
        date = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed;
        return true;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? date, CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized();
        if (!TryDate(date, out var day)) return BadRequest(new { message = "Choose a valid date." });
        try { return Ok(await availability.GetAsync(owner.SalonOwnerID, owner.SalonMasterID, cancellationToken, day)); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    public sealed class ScheduleUpdate
    {
        [Required, Range(0, long.MaxValue)] public long? Version { get; set; }
        [Required, MaxLength(200)] public Guid[]? TimeAllocationIDs { get; set; }
        /// <summary>yyyy-MM-dd to set times for one date only; omit for the daily schedule.</summary>
        [MaxLength(10)] public string? Date { get; set; }
    }

    [HttpPut]
    public async Task<IActionResult> Save(ScheduleUpdate request, CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized();
        if (Request.Headers["X-Salonexa-Request"] != "1") return StatusCode(403, new { message = "Use the application to save available times." });
        if (!TryDate(request.Date, out var day)) return BadRequest(new { message = "Choose a valid date." });
        try { return Ok(await availability.SaveAsync(owner.SalonOwnerID, owner.SalonMasterID, request.Version!.Value, request.TimeAllocationIDs!, cancellationToken, day)); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (SalonTimingConflict error) { return Conflict(new { message = error.Message }); }
    }

    /// <summary>Removes a date's own times so it follows the daily schedule again.</summary>
    [HttpDelete]
    public async Task<IActionResult> Reset([FromQuery] string? date, [FromQuery] long? version, CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized();
        if (Request.Headers["X-Salonexa-Request"] != "1") return StatusCode(403, new { message = "Use the application to save available times." });
        if (!TryDate(date, out var day) || day is null || version is null or < 0) return BadRequest(new { message = "Choose a valid date." });
        try { return Ok(await availability.ResetDateAsync(owner.SalonOwnerID, owner.SalonMasterID, version.Value, day.Value, cancellationToken)); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (SalonTimingConflict error) { return Conflict(new { message = error.Message }); }
    }
}
