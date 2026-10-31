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
        Guid salonOwnerId,
        Guid salonMasterId,
        DateTime? date,
        CancellationToken cancellationToken,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var selectedDate = date?.Date ?? CustomerBookingRules.Today;
        var rangeStart = (fromDate ?? toDate ?? selectedDate).Date;
        var rangeEnd = (toDate ?? fromDate ?? selectedDate).Date;
        if (rangeStart < new DateTime(2000, 1, 1) || rangeEnd > new DateTime(2100, 12, 31)
            || rangeStart > rangeEnd)
            throw new ArgumentException("Choose dates between 2000 and 2100, with From date on or before To date.");
        await using var connection = new SqlConnection(_connectionString);
        using var results = await connection.QueryMultipleAsync(
            new CommandDefinition(
                "dbo.getSalonOwnerDashboard",
                new { OwnerID = salonOwnerId, SalonID = salonMasterId, Date = rangeEnd,
                    FromDate = rangeStart, ToDate = rangeEnd },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return new SalonOwnerDashboardResponse
        {
            Summary = await results.ReadSingleAsync<SalonOwnerDashboardSummary>(),
            UpcomingAppointments = (await results.ReadAsync<SalonOwnerDashboardAppointment>()).AsList(),
            WeeklyBookings = (await results.ReadAsync<SalonOwnerDashboardDay>()).AsList(),
            PopularServices = (await results.ReadAsync<SalonOwnerDashboardServiceItem>()).AsList(),
            Salon = await results.ReadSingleAsync<SalonOwnerDashboardProfile>(),
            Services = (await results.ReadAsync<SalonOwnerDashboardCatalogueService>()).AsList(),
            Date = DateOnly.FromDateTime(rangeEnd),
            FromDate = DateOnly.FromDateTime(rangeStart),
            ToDate = DateOnly.FromDateTime(rangeEnd)
        };
    }
}
