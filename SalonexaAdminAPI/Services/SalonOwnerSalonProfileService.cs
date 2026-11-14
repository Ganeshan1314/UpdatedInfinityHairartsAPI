using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using SalonexaAdminAPI.Modals;

namespace SalonexaAdminAPI.Services;

/// <summary>Reads and saves the signed-in owner's own salon (dbo.GetSalonOwnerProfile / dbo.SaveSalonOwnerProfile).</summary>
public sealed class SalonOwnerSalonProfileService(IConfiguration configuration)
{
    private SqlConnection Connect() => new(configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));

    public async Task<OwnerSalonProfile?> GetAsync(Guid ownerId, Guid salonId, CancellationToken cancellationToken)
    {
        await using var connection = Connect();
        return await connection.QuerySingleOrDefaultAsync<OwnerSalonProfile>(new CommandDefinition(
            "dbo.GetSalonOwnerProfile", new { OwnerID = ownerId, SalonID = salonId },
            commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
    }

    public async Task<OwnerSalonProfile> SaveAsync(Guid ownerId, Guid salonId, OwnerSalonProfileRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = Connect();
        return await connection.QuerySingleAsync<OwnerSalonProfile>(new CommandDefinition("dbo.SaveSalonOwnerProfile", new
        {
            OwnerID = ownerId,
            SalonID = salonId,
            SalonName = request.SalonName.Trim(),
            request.LocationMasterID,
            SalonAddress = request.SalonAddress.Trim(),
            ContactNumber = request.ContactNumber,
            OpeningTime = TimeSpan.Parse(request.OpeningTime),
            ClosingTime = TimeSpan.Parse(request.ClosingTime),
            request.ChairCount,
            request.WeeklyHoliday,
            request.Latitude,
            request.Longitude,
        }, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
    }
}
