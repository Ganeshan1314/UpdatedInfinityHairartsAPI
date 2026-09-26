using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.Data.SqlClient;
using System.Data;

namespace InfinityHairartsAPI.Services;

public sealed class SalonOwnerDashboardService
{
    private readonly string _connectionString;

    public SalonOwnerDashboardService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is not configured.");
    }

    public async Task<SalonOwnerDashboardResponse> GetDashboardAsync(
        Guid salonMasterId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        using var results = await connection.QueryMultipleAsync(
            new CommandDefinition(
                "dbo.getSalonOwnerDashboard",
                new { SalonMasterID = salonMasterId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return new SalonOwnerDashboardResponse
        {
            Summary = await results.ReadSingleAsync<SalonOwnerDashboardSummary>(),
            UpcomingAppointments = (await results.ReadAsync<SalonOwnerDashboardAppointment>()).AsList(),
            WeeklyBookings = (await results.ReadAsync<SalonOwnerDashboardDay>()).AsList(),
            PopularServices = (await results.ReadAsync<SalonOwnerDashboardServiceItem>()).AsList()
        };
    }
}
