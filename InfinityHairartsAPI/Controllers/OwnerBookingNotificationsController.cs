using System.ComponentModel.DataAnnotations;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers;

[ApiController, Route("api/Admin/notifications"), OwnerNotificationAccess]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class OwnerBookingNotificationsController(OwnerBookingNotificationStore store) : ControllerBase
{
    private SalonOwnerLoginResponse Owner => SalonOwnerPersistentSignIn.RestoredOwner(HttpContext)!;

    [HttpGet]
    public async Task<ActionResult<OwnerBookingInbox>> Inbox(CancellationToken cancellationToken) =>
        Ok(await store.GetInboxAsync(Owner.SalonOwnerID, Owner.SalonMasterID, cancellationToken));

    [HttpPost("register-device")]
    public async Task<IActionResult> Register(RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        if (request.Token.Any(char.IsWhiteSpace) || request.Platform != "android") return BadRequest(new { message = "An Android push token is required." });
        try { await store.RegisterAsync(Owner.SalonOwnerID, request.Token, cancellationToken); }
        catch (SqlException error) when (error.Number == 50061) { return Unauthorized(); }
        return Ok(new { saved = true });
    }

    [HttpPost("unregister-device")]
    public async Task<IActionResult> Unregister(RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        await store.UnregisterAsync(Owner.SalonOwnerID, request.Token, cancellationToken);
        return Ok(new { saved = true });
    }

    public sealed class ReadRequest { [Range(1, long.MaxValue)] public long ThroughID { get; set; } }
    [HttpPost("read")]
    public async Task<IActionResult> Read(ReadRequest request, CancellationToken cancellationToken)
    {
        await store.ReadAsync(Owner.SalonOwnerID, Owner.SalonMasterID, request.ThroughID, cancellationToken);
        return Ok(new { saved = true });
    }
}

// The existing owner middleware validates the persistent ticket against the account.
public sealed class OwnerNotificationAccessAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (SalonOwnerPersistentSignIn.RestoredOwner(context.HttpContext) is null)
            context.Result = new UnauthorizedObjectResult(new { message = "Please sign in as a salon owner." });
        else if (!HttpMethods.IsGet(context.HttpContext.Request.Method)
            && context.HttpContext.Request.Headers["X-Salonexa-Request"] != "1")
            context.Result = new ObjectResult(new { message = "Use the application to submit this request." }) { StatusCode = 403 };
    }
}
