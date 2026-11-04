using System.Data;
using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public sealed class BookingStatusCounts
{
    public int Pending { get; set; }
    public int InProgress { get; set; }
    public int Completed { get; set; }
}

public sealed record OwnerAppointmentPage(IReadOnlyList<SalonOwnerDashboardAppointment> Items,
    BookingStatusCounts Counts, int TotalCount, int Page, int PageSize);

public sealed class StartedOwnerBooking
{
    public Guid SeatBookingDetailsID { get; set; }
    public string BookingStatus { get; set; } = "InProgress";
    public DateTime StartedUtc { get; set; }
    public bool WasAlreadyStarted { get; set; }
}

public sealed class OwnerAppointmentsService(IConfiguration configuration)
{
    public async Task<OwnerAppointmentPage> GetAsync(Guid owner, Guid salon, DateTime? fromDate, DateTime? toDate,
        string status, int page, CancellationToken ct)
    {
        if (fromDate.HasValue != toDate.HasValue
            || (fromDate.HasValue && (fromDate.Value.Date < new DateTime(2000,1,1) || toDate!.Value.Date > new DateTime(2100,12,31)
                || fromDate.Value.Date > toDate.Value.Date))
            || status is not ("Pending" or "InProgress" or "Completed") || page < 1 || page > 100000)
            throw new ArgumentException("Choose a valid date range, booking status and page.");
        await using var db = new SqlConnection(configuration.GetConnectionString("dbconnection"));
        using var results = await db.QueryMultipleAsync(new CommandDefinition("dbo.getOwnerAppointments",
            new { OwnerID=owner,SalonID=salon,FromDate=fromDate?.Date,ToDate=toDate?.Date,Status=status,Page=page,PageSize=10 },
            commandType:CommandType.StoredProcedure,cancellationToken:ct));
        var counts = await results.ReadSingleAsync<BookingStatusCounts>();
        var items = (await results.ReadAsync<SalonOwnerDashboardAppointment>()).AsList();
        var total = status switch { "Pending" => counts.Pending, "InProgress" => counts.InProgress, _ => counts.Completed };
        return new(items, counts, total, page, 10);
    }

    public async Task<StartedOwnerBooking> StartAsync(Guid owner, Guid salon, Guid booking, CancellationToken ct)
    {
        if (booking == Guid.Empty) throw new ArgumentException("Select a valid booking.");
        await using var db = new SqlConnection(configuration.GetConnectionString("dbconnection"));
        return await db.QuerySingleAsync<StartedOwnerBooking>(new CommandDefinition("dbo.startOwnerBooking",
            new { OwnerID=owner,SalonID=salon,BookingID=booking },commandType:CommandType.StoredProcedure,cancellationToken:ct));
    }
}
