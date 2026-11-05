using System.ComponentModel.DataAnnotations;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfinityHairartsAPI.Controllers;

[ApiController, Route("api/Admin/availability")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SalonTimingAvailabilityController(SalonTimingAvailability availability) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized();
        try { return Ok(await availability.GetAsync(owner.SalonOwnerID, owner.SalonMasterID, cancellationToken)); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    public sealed class ScheduleUpdate
    {
        [Required, Range(0, long.MaxValue)] public long? Version { get; set; }
        [Required, MaxLength(200)] public Guid[]? TimeAllocationIDs { get; set; }
    }

    [HttpPut]
    public async Task<IActionResult> Save(ScheduleUpdate request, CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized();
        if (Request.Headers["X-Salonexa-Request"] != "1") return StatusCode(403, new { message = "Use the application to save available times." });
        try { return Ok(await availability.SaveAsync(owner.SalonOwnerID, owner.SalonMasterID, request.Version!.Value, request.TimeAllocationIDs!, cancellationToken)); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (SalonTimingConflict error) { return Conflict(new { message = error.Message }); }
    }
}
