using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public sealed class SalonOwnerAppointmentsReader(IConfiguration configuration)
{
    public const int PageSize = 10;

    public async Task<SalonOwnerAppointmentPage> GetAsync(Guid ownerId, Guid salonId,
        DateTime? fromDate, DateTime? toDate, string status, int page, CancellationToken cancellationToken)
    {
        if (status is not ("Pending" or "InProgress" or "Completed"))
            throw new ArgumentException("Choose a valid appointment status.");
        if (page < 1) throw new ArgumentException("Page must be at least 1.");
        if (fromDate.HasValue != toDate.HasValue)
            throw new ArgumentException("Choose both From date and To date.");
        var from = fromDate?.Date;
        var to = toDate?.Date;
        if (from < new DateTime(2000, 1, 1) || to > new DateTime(2100, 12, 31) || from > to)
            throw new ArgumentException("Choose dates between 2000 and 2100, with From date on or before To date.");

        await using var connection = new SqlConnection(configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            "dbo.GetSalonOwnerAppointments", new {
                OwnerID = ownerId, SalonID = salonId, FromDate = from, ToDate = to, Status = status, Page = page
            }, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
        var counts = await results.ReadSingleAsync<SalonOwnerAppointmentCounts>();
        var total = await results.ReadSingleAsync<int>();
        var items = (await results.ReadAsync<SalonOwnerAppointmentItem>()).AsList();
        return new() { Items = items, Counts = counts, TotalCount = total, Page = page, PageSize = PageSize };
    }
}

public sealed class SalonOwnerAppointmentPage
{
    public IReadOnlyList<SalonOwnerAppointmentItem> Items { get; set; } = [];
    public SalonOwnerAppointmentCounts Counts { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public sealed class SalonOwnerAppointmentCounts
{
    public int Pending { get; set; }
    public int InProgress { get; set; }
    public int Completed { get; set; }
}

public sealed class SalonOwnerAppointmentItem
{
    public Guid SeatBookingDetailsID { get; set; }
    public string CustomerName { get; set; } = "";
    public string MobileNo { get; set; } = "";
    public DateTime BookingDate { get; set; }
    public TimeSpan? StartTime { get; set; }
    public int SeatCount { get; set; }
    public string BookingStatus { get; set; } = "";
    public decimal? BillAmount { get; set; }
}
