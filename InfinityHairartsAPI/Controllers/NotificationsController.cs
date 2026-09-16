using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[SessionTimeout]
public sealed class NotificationsController : ControllerBase
{
    private readonly NotificationRepository _repository;

    public NotificationsController(NotificationRepository repository)
    {
        _repository = repository;
    }

    [HttpPost("register-device")]
    public async Task<IActionResult> RegisterDevice(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var customerIdValue = HttpContext.Session.GetString("CustomerID");
        if (!Guid.TryParse(customerIdValue, out var customerId))
        {
            return Unauthorized(new { item1 = "Session Expired" });
        }

        await _repository.RegisterDeviceAsync(
            customerId,
            request.Token.Trim(),
            request.Platform.Trim().ToLowerInvariant(),
            cancellationToken);

        return Ok(new { item1 = "Success" });
    }
}
