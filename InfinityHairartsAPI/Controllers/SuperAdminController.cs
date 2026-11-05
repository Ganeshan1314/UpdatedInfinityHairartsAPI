using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers;

[ApiController, Route("api/SuperAdmin")]
[Authorize(AuthenticationSchemes = SuperAdminAuthentication.Scheme, Roles = SuperAdminAuthentication.Role)]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SuperAdminController(SuperAdminAuthentication authentication, ISuperAdminDashboard dashboard) : ControllerBase
{
    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("super-admin-login")]
    public async Task<IActionResult> Login(SuperAdminLogin request, CancellationToken cancellationToken)
    {
        var signIn = await authentication.AuthenticateAsync(request, cancellationToken);
        if (signIn is null) return Unauthorized(new { message = "Incorrect username or password." });
        var account = signIn.Session;
        await HttpContext.SignInAsync(SuperAdminAuthentication.Scheme, new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, account.SuperAdminID.ToString()),
            new Claim(ClaimTypes.Name, account.UserName),
            new Claim(ClaimTypes.Role, SuperAdminAuthentication.Role),
            new Claim(SuperAdminAuthentication.StampClaim, signIn.Stamp)
        ], SuperAdminAuthentication.Scheme)), new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
        return Ok(account);
    }

    [HttpGet("session")]
    public IActionResult Session() => Ok(SuperAdminAuthentication.CurrentSession(HttpContext));

    [AllowAnonymous, HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutSuperAdminAsync();
        return NoContent();
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Overview([FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330));
        var start = fromDate ?? today;
        var end = toDate ?? start;
        if (start.Year < 2000 || end.Year > 2100 || start > end)
            return BadRequest(new { message = "Choose dates between 2000 and 2100, with From date on or before To date." });
        return Ok(await dashboard.GetOverview(start, end, cancellationToken));
    }

    [HttpGet("salons")]
    public async Task<IActionResult> Salons([FromQuery, StringLength(100)] string search = "", [FromQuery] string status = "all",
        [FromQuery, Range(1, 100000)] int page = 1, CancellationToken cancellationToken = default)
    {
        if (status is not ("all" or "pending" or "visible" or "inactive")) return BadRequest(new { message = "Invalid salon status." });
        return Ok(await dashboard.GetSalons(search.Trim(), status, page, cancellationToken));
    }

    public sealed class VisibilityUpdate
    {
        [Required] public bool? IsVisibleToCustomers { get; set; }
        [Required] public bool? ExpectedVisible { get; set; }
    }

    [HttpPut("salons/{salonId:guid}/visibility")]
    public async Task<IActionResult> Visibility(Guid salonId, VisibilityUpdate request, CancellationToken cancellationToken)
    {
        try
        {
            await dashboard.SetVisibility(salonId, request.IsVisibleToCustomers!.Value, request.ExpectedVisible!.Value,
                User.FindFirstValue(ClaimTypes.Name)!, cancellationToken);
            return NoContent();
        }
        catch (SqlException exception) when (exception.Number == 50044)
        { return NotFound(new { message = "This salon no longer exists." }); }
        catch (SqlException exception) when (exception.Number is 50045 or 50046)
        { return Conflict(new { message = exception.Message }); }
    }
}
