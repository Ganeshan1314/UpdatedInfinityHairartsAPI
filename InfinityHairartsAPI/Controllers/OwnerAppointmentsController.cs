using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[Route("api/Admin/appointments")]
public sealed class OwnerAppointmentsController(OwnerAppointmentsService service) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get([FromQuery] DateTime? fromDate=null,[FromQuery] DateTime? toDate=null,
        [FromQuery] string status="Pending",[FromQuery] int page=1,CancellationToken cancellationToken=default) =>
        WithOwner((owner,salon) => service.GetAsync(owner,salon,fromDate,toDate,status,page,cancellationToken));

    [HttpPost("{bookingID:guid}/start")]
    public Task<IActionResult> Start(Guid bookingID,CancellationToken cancellationToken) =>
        WithOwner((owner,salon) => service.StartAsync(owner,salon,bookingID,cancellationToken));

    private async Task<IActionResult> WithOwner<T>(Func<Guid,Guid,Task<T>> action)
    {
        Response.Headers.CacheControl="no-store";
        if (!Guid.TryParse(HttpContext.Session.GetString("SalonOwnerID"),out var owner)
            || !Guid.TryParse(HttpContext.Session.GetString("SalonMasterID"),out var salon))
            return Unauthorized(new { message="Please sign in again." });
        try { return Ok(await action(owner,salon)); }
        catch(ArgumentException ex) { return BadRequest(new {message=ex.Message}); }
        catch(SqlException ex) when(ex.Number==50013) { return StatusCode(403,new {message=ex.Message}); }
        catch(SqlException ex) when(ex.Number==50010) { return NotFound(new {message=ex.Message}); }
        catch(SqlException ex) when(ex.Number==50002) { return BadRequest(new {message=ex.Message}); }
        catch(SqlException ex) when(ex.Number is 50011 or 50012) { return Conflict(new {message=ex.Message}); }
    }
}
