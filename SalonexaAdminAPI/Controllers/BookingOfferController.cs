using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SalonexaAdminAPI.Services;

namespace SalonexaAdminAPI.Controllers;

[ApiController, Route("api/SuperAdmin/offer")]
[Authorize(AuthenticationSchemes = SuperAdminAuthentication.Scheme, Roles = SuperAdminAuthentication.Role)]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SuperAdminOfferController(BookingOfferService offers) : ControllerBase
{
    public sealed class SettingsUpdate
    {
        [Required] public bool? IsEnabled { get; set; }
        [Required, RegularExpression("^(Fixed|Percent)$")] public string DiscountType { get; set; } = string.Empty;
        [Required, Range(typeof(decimal), "0", "100000")] public decimal? DiscountValue { get; set; }
        [Range(typeof(decimal), "0", "100000")] public decimal? MaximumDiscount { get; set; }
        [Required, Range(typeof(decimal), "0", "1000000")] public decimal? MinimumBill { get; set; }
        public DateOnly? EndDate { get; set; }
        [Required, Range(typeof(decimal), "0", "100000000")] public decimal? TotalBudget { get; set; }
        [Required, Range(0, 100000)] public int? SalonMonthlyLimit { get; set; }
        [Required] public int? Version { get; set; }
    }

    public sealed class SalonUpdate
    {
        [Required] public bool? IsParticipating { get; set; }
    }

    public sealed class ClaimReview
    {
        [Required, RegularExpression("^(Approve|Reject|Paid)$")] public string Action { get; set; } = string.Empty;
        [StringLength(100)] public string? PaymentReference { get; set; }
        [StringLength(300)] public string? Note { get; set; }
    }

    private string Actor => User.FindFirstValue(ClaimTypes.Name)!;

    [HttpGet]
    public Task<IActionResult> Settings(CancellationToken cancellationToken) =>
        Run(async () => Ok(await offers.GetSettings(cancellationToken)));

    [HttpPut]
    public Task<IActionResult> Save(SettingsUpdate request, CancellationToken cancellationToken) => Run(async () =>
    {
        await offers.SaveSettings(new(request.IsEnabled!.Value, request.DiscountType, request.DiscountValue!.Value,
            request.MaximumDiscount, request.MinimumBill!.Value, request.EndDate, request.TotalBudget!.Value,
            request.SalonMonthlyLimit!.Value, request.Version!.Value), Actor, cancellationToken);
        return Ok(await offers.GetSettings(cancellationToken));
    });

    [HttpGet("salons")]
    public Task<IActionResult> Salons([FromQuery, StringLength(100)] string search = "",
        [FromQuery, Range(1, 100000)] int page = 1, CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await offers.GetSalons(search.Trim(), page, cancellationToken)));

    [HttpPut("salons/{salonId:guid}")]
    public Task<IActionResult> Salon(Guid salonId, SalonUpdate request, CancellationToken cancellationToken) =>
        Run(async () =>
        {
            await offers.SetSalon(salonId, request.IsParticipating!.Value, Actor, cancellationToken);
            return NoContent();
        });

    [HttpGet("claims")]
    public Task<IActionResult> Claims([FromQuery] string status = "ToCheck",
        [FromQuery, Range(1, 100000)] int page = 1, CancellationToken cancellationToken = default) =>
        Run(async () => status is "ToCheck" or "Approved" or "Paid" or "Rejected"
            ? Ok(await offers.GetClaims(status, page, cancellationToken))
            : BadRequest(new { message = "Invalid claim status." }));

    /// <summary>Records a decision or a payment made outside the app. No money is sent here.</summary>
    [HttpPost("claims/{bookingId:guid}/review")]
    public Task<IActionResult> Review(Guid bookingId, ClaimReview request, CancellationToken cancellationToken) =>
        Run(async () =>
        {
            await offers.Review(bookingId, request.Action, request.PaymentReference, request.Note, Actor, cancellationToken);
            return NoContent();
        });

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SqlException error) when (error.Number == 50062) { return BadRequest(new { message = error.Message }); }
        catch (SqlException error) when (error.Number is 50063 or 50064) { return NotFound(new { message = error.Message }); }
        catch (SqlException error) when (error.Number is 50061 or 50065) { return Conflict(new { message = error.Message }); }
        catch (SqlException error) when (error.Number == 50066)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = error.Message });
        }
        catch (SqlException error) when (error.Number is 2812 or 208 or 207)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The offer database update is required." });
        }
    }
}

[ApiController, Route("api/Admin/offers")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SalonOwnerOfferController(BookingOfferService offers) : ControllerBase
{
    /// <summary>The signed-in salon's bookings that carry the offer, with the amount to collect.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext);
        if (owner is null) return Unauthorized(new { message = "Please sign in again." });
        try
        {
            return Ok(new { items = await offers.GetForSalon(owner.SalonOwnerID, owner.SalonMasterID, cancellationToken) });
        }
        catch (SqlException error) when (error.Number == 50013)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Please sign in again." });
        }
        // Before the offer database update there can be no offers to show.
        catch (SqlException error) when (error.Number == 2812) { return Ok(new { items = Array.Empty<SalonBookingOffer>() }); }
    }
}
