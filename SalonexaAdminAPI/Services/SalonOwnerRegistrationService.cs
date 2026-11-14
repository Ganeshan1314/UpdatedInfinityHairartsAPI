using System.Data;
using System.Threading.RateLimiting;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using SalonexaAdminAPI.Modals;

namespace SalonexaAdminAPI.Services;

/// <summary>Creates a salon and its first owner account (dbo.RegisterSalonOwner). New salons start hidden from customers.</summary>
public sealed class SalonOwnerRegistrationService(IConfiguration configuration, SalonImageStore images)
{
    public const string RateLimitPolicy = "owner-signup";
    private readonly PasswordHasher<string> hasher = new();

    public async Task<SignupResult> SignupAsync(SalonOwnerSignupRequest request, CancellationToken cancellationToken)
    {
        var userName = request.UserName.Trim();
        var city = request.City.Trim();
        var pincode = request.Pincode;
        // Make sure the saved address names the city and PIN code, as customers see it.
        var parts = request.SalonAddress.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
        if (!parts.Contains(city, StringComparer.OrdinalIgnoreCase)) parts.Add(city);
        if (!parts.Any(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(pincode))) parts.Add(pincode);
        var address = string.Join(", ", parts);
        if (address.Length > 500) return SignupResult.InvalidDetails;

        var ownerId = Guid.NewGuid();
        var salonId = Guid.NewGuid();
        var image = await images.SaveAsync(salonId, request.Image!, cancellationToken);
        var created = false;
        try
        {
            await using var connection = new SqlConnection(configuration.GetConnectionString("dbconnection")
                ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));
            var result = await connection.QuerySingleAsync<int>(new CommandDefinition("dbo.RegisterSalonOwner", new
            {
                SalonOwnerID = ownerId,
                SalonMasterID = salonId,
                OwnerName = request.OwnerName.Trim(),
                UserName = userName,
                PasswordHash = hasher.HashPassword(userName, request.Password),
                SalonName = request.SalonName.Trim(),
                request.LocationMasterID,
                EmailAddress = request.EmailAddress.Trim(),
                ContactNumber = request.ContactNumber.Trim(),
                SalonAddress = address,
                City = city,
                Pincode = pincode,
                request.Latitude,
                request.Longitude,
                ImagePath = image.PublicPath,
            }, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
            var outcome = Enum.IsDefined(typeof(SignupResult), result) ? (SignupResult)result : SignupResult.InvalidDetails;
            created = outcome == SignupResult.Created;
            return outcome;
        }
        finally
        {
            if (!created) images.Delete(image);
        }
    }

    public static IServiceCollection AddOwnerSignupRateLimit(IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
        });
}
