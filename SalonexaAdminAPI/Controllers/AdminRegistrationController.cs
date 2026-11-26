using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using SalonexaAdminAPI.Modals;
using SalonexaAdminAPI.Services;

namespace SalonexaAdminAPI.Controllers;

/// <summary>Self-service salon-owner signup for the admin app. The service-area list is on AdminLocationController.</summary>
[ApiController, Route("api/Admin/signup")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminRegistrationController(
    SalonOwnerRegistrationService registration, ILogger<AdminRegistrationController> logger) : ControllerBase
{
    private const int MaximumRequestSize = 6 * 1024 * 1024;

    [HttpPost, EnableRateLimiting(SalonOwnerRegistrationService.RateLimitPolicy)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumRequestSize), RequestFormLimits(MultipartBodyLengthLimit = MaximumRequestSize)]
    public async Task<IActionResult> Signup([FromForm] SalonOwnerSignupRequest request, CancellationToken cancellationToken)
    {
        // The salon name and address are shown to customers, so they follow the same content rules.
        var rejection = PublicTextRules.Rejection(("salon name", request.SalonName), ("salon address", request.SalonAddress),
            ("owner name", request.OwnerName));
        if (rejection is not null) return BadRequest(new { message = rejection });
        try
        {
            return await registration.SignupAsync(request, cancellationToken) switch
            {
                SignupResult.Created => StatusCode(StatusCodes.Status201Created,
                    new { message = "Your account has been created. Please sign in." }),
                SignupResult.DuplicateUserName => Conflict(new { message = "That username is already taken. Choose another username." }),
                SignupResult.DuplicateSalon => Conflict(new { message = "This salon name is already registered. Please choose a different salon name." }),
                SignupResult.DuplicateDetails => Conflict(new { message = "The username or salon name is already registered." }),
                SignupResult.InvalidLocation => BadRequest(new { message = "Select an available location and try again." }),
                _ => BadRequest(new { message = "Please check your registration details and try again." }),
            };
        }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
        catch (SqlException exception) when (exception.Number is 2812 or 208 or 207)
        {
            logger.LogError(exception, "Signup needs the owner signup database update.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Registration is temporarily unavailable. Please try again later." });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Salon owner registration could not be completed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Registration is temporarily unavailable. Please try again later." });
        }
    }
}
