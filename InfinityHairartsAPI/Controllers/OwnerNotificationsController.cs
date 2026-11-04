using System.ComponentModel.DataAnnotations;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[Route("api/Admin/notifications")]
public sealed class OwnerNotificationsController(OwnerBookingNotificationRepository repository) : ControllerBase
{
    public sealed class DeviceRequest
    {
        [Required, StringLength(512, MinimumLength = 10), RegularExpression(@"^[A-Za-z0-9_:\-]+$")]
        public string Token { get; set; } = "";
    }
    public sealed class ReadRequest
    {
        [Range(1, long.MaxValue)] public long ThroughID { get; set; }
    }

    [HttpGet]
    public Task<IActionResult> Get(CancellationToken ct) => WithOwner((owner, salon) => repository.GetAsync(owner, salon, ct));

    [HttpPost("read")]
    public Task<IActionResult> Read(ReadRequest request, CancellationToken ct) => WithOwner(async (owner, salon) => {
        await repository.ReadAsync(owner, salon, request.ThroughID, ct); return new { saved = true };
    });

    [HttpPost("register-device")]
    public Task<IActionResult> Register(DeviceRequest request, CancellationToken ct) => WithOwner(async (owner, salon) => {
        await repository.RegisterAsync(owner, salon, request.Token, ct); return new { saved = true };
    });

    [HttpPost("unregister-device")]
    public Task<IActionResult> Unregister(DeviceRequest request, CancellationToken ct) => WithOwner(async (owner, salon) => {
        await repository.UnregisterAsync(owner, request.Token, ct); return new { saved = true };
    });

    private async Task<IActionResult> WithOwner<T>(Func<Guid, Guid, Task<T>> action)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(HttpContext.Session.GetString("SalonOwnerID"), out var owner) ||
            !Guid.TryParse(HttpContext.Session.GetString("SalonMasterID"), out var salon))
            return Unauthorized(new { message = "Please sign in again." });
        try { return Ok(await action(owner, salon)); }
        catch (SqlException ex) when (ex.Number == 50013) { return StatusCode(403, new { message = ex.Message }); }
    }
}
