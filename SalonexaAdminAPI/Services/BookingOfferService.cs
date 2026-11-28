using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SalonexaAdminAPI.Services;

// Column order matches the procedures in InfinityHairartsAPI/Database/FirstBookingOffer.sql.
public sealed record BookingOfferSettings(bool IsEnabled, string DiscountType, decimal DiscountValue,
    decimal? MaximumDiscount, decimal MinimumBill, DateTime? EndDate, decimal TotalBudget, int SalonMonthlyLimit,
    DateTime? LaunchedUtc, int Version, int WaitingCount, decimal WaitingAmount, int ToCheckCount,
    decimal ToCheckAmount, int ToPayCount, decimal ToPayAmount, int PaidCount, decimal PaidAmount, decimal BudgetUsed);
public sealed record BookingOfferSettingsChange(bool IsEnabled, string DiscountType, decimal DiscountValue,
    decimal? MaximumDiscount, decimal MinimumBill, DateOnly? EndDate, decimal TotalBudget, int SalonMonthlyLimit,
    int ExpectedVersion);
public sealed record BookingOfferSalonRow(Guid SalonMasterID, string SalonName, bool IsActive, bool IsParticipating,
    int UsedThisMonth, decimal ToCheckAmount, decimal ToPayAmount, decimal PaidAmount);
public sealed record BookingOfferSalonPage(int Total, int Page, int PageSize, IReadOnlyList<BookingOfferSalonRow> Items);
public sealed record BookingOfferClaim(Guid SeatBookingDetailsID, Guid SalonMasterID, string SalonName,
    string CustomerName, string MobileNo, DateTime BookingDate, decimal OriginalAmount, decimal DiscountAmount,
    DateTime BookedUtc, DateTime ScannedUtc, string Status, string? PaymentReference, string? ReviewNote,
    bool ScannedSoonAfterBooking, bool ManyOffersSameDay, bool NoCustomerName, bool UsesSalonPhoneNumber,
    bool AccountDeleted);
public sealed record BookingOfferClaimPage(int Total, int Page, int PageSize, IReadOnlyList<BookingOfferClaim> Items);
public sealed record SalonBookingOffer(Guid SeatBookingDetailsID, DateTime BookingDate, decimal OriginalAmount,
    decimal DiscountAmount, string State, decimal AmountToCollect, string? ReviewStatus, string? PaymentReference,
    DateTime DeadlineUtc);

/// <summary>The first-booking offer: settings and repayment claims for Super Admin, amounts to collect for salons.</summary>
public sealed class BookingOfferService(IConfiguration configuration)
{
    private const int PageSize = 20;

    private SqlConnection Connection() => new(configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));

    private static CommandDefinition Procedure(string name, object? values, CancellationToken cancellationToken) =>
        new(name, values, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public async Task<BookingOfferSettings> GetSettings(CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        var settings = await connection.QuerySingleAsync<BookingOfferSettings>(
            Procedure("dbo.GetBookingOfferSettings", null, cancellationToken));
        return settings with { LaunchedUtc = settings.LaunchedUtc is { } launched ? Utc(launched) : null };
    }

    public async Task SaveSettings(BookingOfferSettingsChange change, string actor, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(Procedure("dbo.SaveBookingOfferSettings", new
        {
            change.IsEnabled,
            change.DiscountType,
            change.DiscountValue,
            change.MaximumDiscount,
            change.MinimumBill,
            EndDate = change.EndDate?.ToDateTime(TimeOnly.MinValue),
            change.TotalBudget,
            change.SalonMonthlyLimit,
            change.ExpectedVersion,
            Actor = actor
        }, cancellationToken));
    }

    public async Task<BookingOfferSalonPage> GetSalons(string search, int page, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        using var results = await connection.QueryMultipleAsync(Procedure("dbo.GetBookingOfferSalons",
            new { Search = search, Page = page, PageSize }, cancellationToken));
        var total = await results.ReadSingleAsync<int>();
        return new(total, page, PageSize, (await results.ReadAsync<BookingOfferSalonRow>()).ToList());
    }

    public async Task SetSalon(Guid salonId, bool participating, string actor, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(Procedure("dbo.SetBookingOfferSalon",
            new { SalonID = salonId, Participating = participating, Actor = actor }, cancellationToken));
    }

    public async Task<BookingOfferClaimPage> GetClaims(string status, int page, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        using var results = await connection.QueryMultipleAsync(Procedure("dbo.GetBookingOfferClaims",
            new { Status = status, Page = page, PageSize }, cancellationToken));
        var total = await results.ReadSingleAsync<int>();
        var items = (await results.ReadAsync<BookingOfferClaim>())
            .Select(claim => claim with { BookedUtc = Utc(claim.BookedUtc), ScannedUtc = Utc(claim.ScannedUtc) })
            .ToList();
        return new(total, page, PageSize, items);
    }

    public async Task Review(Guid bookingId, string action, string? paymentReference, string? note, string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(Procedure("dbo.ReviewBookingOfferClaim",
            new { SeatBookingDetailsID = bookingId, Action = action, PaymentReference = paymentReference, Note = note, Actor = actor },
            cancellationToken));
    }

    public async Task<IReadOnlyList<SalonBookingOffer>> GetForSalon(Guid ownerId, Guid salonId,
        CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        var items = await connection.QueryAsync<SalonBookingOffer>(Procedure("dbo.GetSalonBookingOffers",
            new { OwnerID = ownerId, SalonID = salonId }, cancellationToken));
        return items.Select(item => item with { DeadlineUtc = Utc(item.DeadlineUtc) }).ToList();
    }
}
