using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public sealed record SuperAdminSummary(int TotalSalons, int VisibleSalons, int AwaitingApproval, int ActiveOwners,
    int Bookings, int CompletedBookings, int Customers, decimal BilledAmount);
public sealed record SuperAdminDay(DateTime BookingDate, int BookingCount);
public sealed record SuperAdminAudit(long AuditID, Guid SalonMasterID, string SalonName, string ActorUserName,
    bool IsVisibleToCustomers, DateTime CreatedUtc);
public sealed record SuperAdminOverview(string FromDate, string ToDate, SuperAdminSummary Summary,
    IReadOnlyList<SuperAdminDay> WeeklyBookings, IReadOnlyList<SuperAdminAudit> RecentActivity);
public sealed record SuperAdminSalon(Guid SalonMasterID, string SalonName, string City, string SalonAddress,
    string ContactNumber, string OwnerName, string EmailAddress, bool IsActive, bool IsVisibleToCustomers);
public sealed record SuperAdminSalonPage(int Total, int Page, int PageSize, IReadOnlyList<SuperAdminSalon> Items);

public interface ISuperAdminDashboard
{
    Task<SuperAdminOverview> GetOverview(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);
    Task<SuperAdminSalonPage> GetSalons(string search, string status, int page, CancellationToken cancellationToken);
    Task SetVisibility(Guid salonId, bool visible, bool expectedVisible, string actor, CancellationToken cancellationToken);
}

public sealed class SuperAdminDashboardService(IConfiguration configuration) : ISuperAdminDashboard
{
    private SqlConnection Connection() => new(configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));

    public async Task<SuperAdminOverview> GetOverview(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        using var results = await connection.QueryMultipleAsync(new CommandDefinition("dbo.GetSuperAdminOverview",
            new { FromDate = fromDate.ToDateTime(TimeOnly.MinValue), ToDate = toDate.ToDateTime(TimeOnly.MinValue) },
            commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
        var summary = await results.ReadSingleAsync<SuperAdminSummary>();
        var days = (await results.ReadAsync<SuperAdminDay>()).ToList();
        var activity = (await results.ReadAsync<SuperAdminAudit>()).ToList();
        return new(fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"), summary, days, activity);
    }

    public async Task<SuperAdminSalonPage> GetSalons(string search, string status, int page, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        using var results = await connection.QueryMultipleAsync(new CommandDefinition("dbo.GetSuperAdminSalons",
            new { Search = search, Status = status, Page = page, PageSize = 20 },
            commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
        var total = await results.ReadSingleAsync<int>();
        return new(total, page, 20, (await results.ReadAsync<SuperAdminSalon>()).ToList());
    }

    public async Task SetVisibility(Guid salonId, bool visible, bool expectedVisible, string actor, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(new CommandDefinition("dbo.SetSuperAdminSalonVisibility",
            new { SalonID = salonId, Visible = visible, ExpectedVisible = expectedVisible, Actor = actor },
            commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
    }
}
