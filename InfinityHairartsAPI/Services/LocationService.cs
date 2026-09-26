using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.Data.SqlClient;
using System.Data;

namespace InfinityHairartsAPI.Services;

public sealed class LocationService
{
    private readonly string _connectionString;

    public LocationService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is not configured.");
    }

    public async Task<IReadOnlyList<LocationMasterModal>> GetLocationsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        var locations = await connection.QueryAsync<LocationMasterModal>(
            "dbo.getActiveLocations",
            commandType: CommandType.StoredProcedure);
        return locations.ToList();
    }

    public async Task<IReadOnlyList<SalonMasterModal>> GetSalonsByLocationAsync(Guid locationMasterId)
    {
        await using var connection = new SqlConnection(_connectionString);
        var salons = await connection.QueryAsync<SalonMasterModal>(
            "dbo.getActiveSalonsByLocation",
            new { LocationMasterID = locationMasterId },
            commandType: CommandType.StoredProcedure);
        return salons.ToList();
    }
}
