using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Data.SqlClient;
using SalonexaAdminAPI.Modals;
using SalonexaAdminAPI.Services;

namespace SalonexaAdminAPI.Controllers;

/// <summary>
/// Salon-owner sign-in, session, dashboard and booking completion for the Salonexa Admin app.
/// The legacy unauthenticated haircut-catalogue endpoints of the customer API are deliberately
/// not part of this host; owners manage services through <c>api/Admin/services</c>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AdminController(
    IWebHostEnvironment env,
    ISalonOwnerAuthenticationService salonOwnerAuthenticationService,
    SalonOwnerDashboardService salonOwnerDashboardService,
    BookingQrService bookingQrService,
    SalonOwnerPersistentSignIn persistentSignIn) : ControllerBase
{
    [HttpPost("complete-booking-qr")]
    public async Task<ActionResult<CompleteBookingQrResponse>> CompleteBookingQr(
        [FromBody] CompleteBookingQrRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(HttpContext.Session.GetString("SalonOwnerID"), out var salonOwnerId) ||
            !Guid.TryParse(HttpContext.Session.GetString("SalonMasterID"), out var salonMasterId))
        {
            return Unauthorized(new { message = "The salon-owner session has expired." });
        }

        try
        {
            return Ok(await bookingQrService.CompleteAsync(
                request.QrReference,
                salonOwnerId,
                salonMasterId,
                cancellationToken));
        }
        catch (FormatException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (SqlException exception) when (exception.Number == 50010)
        {
            return NotFound(new { message = exception.Message });
        }
        catch (SqlException exception) when (exception.Number is 50011 or 50012)
        {
            return Conflict(new { message = exception.Message });
        }
        catch (SqlException exception) when (exception.Number == 50013)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = exception.Message });
        }
    }

    [HttpPost("login")]
    public async Task<ActionResult<SalonOwnerLoginResponse>> Login(
        [FromBody] SalonOwnerLoginRequest request,
        CancellationToken cancellationToken)
    {
        var owner = await salonOwnerAuthenticationService.AuthenticateAsync(request, cancellationToken);
        if (owner is null)
        {
            return Unauthorized(new { message = "Invalid username or password." });
        }

        await persistentSignIn.SignInAsync(HttpContext, owner);
        return Ok(owner);
    }

    [HttpGet("session")]
    public async Task<ActionResult<SalonOwnerLoginResponse>> GetSession(CancellationToken cancellationToken)
    {
        var salonOwnerIdValue = HttpContext.Session.GetString("SalonOwnerID");
        if (!Guid.TryParse(salonOwnerIdValue, out var salonOwnerId))
        {
            return Unauthorized(new { message = "The salon-owner session has expired." });
        }

        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext)
            ?? await salonOwnerAuthenticationService.GetActiveSessionOwnerAsync(salonOwnerId, cancellationToken);

        if (owner is null)
        {
            await persistentSignIn.SignOutAsync(HttpContext);
            return Unauthorized(new { message = "The salon-owner session has expired." });
        }

        // Upgrade a still-valid legacy session on the next session check.
        if (SalonOwnerPersistentSignIn.RestoredOwner(HttpContext) is null)
            await persistentSignIn.SignInAsync(HttpContext, owner);
        return Ok(owner);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] OwnerLogoutRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(request?.PushToken))
            {
                await persistentSignIn.RestoreSessionAsync(HttpContext);
                var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
                var notifications = HttpContext.RequestServices.GetService<OwnerBookingNotificationStore>();
                if (owner is not null && notifications is not null)
                    await notifications.UnregisterAsync(owner.SalonOwnerID, request.PushToken, cancellationToken);
            }
        }
        finally { await persistentSignIn.SignOutAsync(HttpContext); }
        return NoContent();
    }

    public sealed class OwnerLogoutRequest { [StringLength(512)] public string? PushToken { get; set; } }

    [HttpGet("dashboard")]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<ActionResult<SalonOwnerDashboardResponse>> GetDashboard(
        [FromQuery] DateTime? date,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken)
    {
        var salonOwnerIdValue = HttpContext.Session.GetString("SalonOwnerID");
        var salonMasterIdValue = HttpContext.Session.GetString("SalonMasterID");
        if (!Guid.TryParse(salonOwnerIdValue, out var salonOwnerId) ||
            !Guid.TryParse(salonMasterIdValue, out var salonMasterId))
        {
            return Unauthorized(new { message = "The salon-owner session has expired." });
        }

        try
        {
            return Ok(await salonOwnerDashboardService.GetDashboardAsync(
                salonOwnerId, salonMasterId, date, cancellationToken, fromDate, toDate));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (SqlException exception) when (exception.Number is 50002 or 50003)
        {
            return Conflict(new { message = exception.Message });
        }
        catch (SqlException exception) when (exception.Number == 50013)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = exception.Message });
        }
    }

    /// <summary>Development-only helper that creates the first salon owner.</summary>
    [HttpPost("initialize-salon-owner")]
    public async Task<ActionResult<SalonOwnerLoginResponse>> InitializeSalonOwner(
        [FromBody] InitializeSalonOwnerRequest request,
        CancellationToken cancellationToken)
    {
        if (!env.IsDevelopment())
        {
            return NotFound();
        }

        try
        {
            var owner = await salonOwnerAuthenticationService.InitializeAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetSession), owner);
        }
        catch (SqlException exception) when (exception.Number == 50001)
        {
            return Conflict(new { message = exception.Message });
        }
        catch (SqlException exception) when (exception.Number == 50002)
        {
            return BadRequest(new { message = exception.Message });
        }
    }
}
