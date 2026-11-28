using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace InfinityHairartsAPI.Services;

public sealed record BookingOfferQuote(string Reason, decimal Discount, string? DiscountType, decimal? DiscountValue,
    decimal? MaximumDiscount, decimal? MinimumBill, DateTime? EndDate)
{
    public static readonly BookingOfferQuote Unavailable = new("Off", 0, null, null, null, null, null);
    public bool IsAvailable => Reason == "Available";
}

public sealed record CustomerBookingOffer(Guid SeatBookingDetailsID, decimal OriginalAmount, decimal DiscountAmount,
    string State, decimal AmountToPay);

/// <summary>
/// First-booking offer, customer side. The database decides who qualifies and how much; the phone
/// only repeats the discount it showed, so a change is noticed before the booking is confirmed.
/// </summary>
public static class BookingOffers
{
    /// <summary>The customer's bookings that carry the offer, with what to pay at the salon.</summary>
    public static IReadOnlyList<CustomerBookingOffer> ForCustomer(IDbConnection db, Guid customer) => IsInstalled(db)
        ? db.Query<CustomerBookingOffer>("dbo.GetCustomerBookingOffers", new { CustomerID = customer },
            commandType: CommandType.StoredProcedure).ToList()
        : [];

    public const string NumberKeySetting = "BookingOffer:NumberKey";

    /// <summary>
    /// A keyed code of the mobile number. Without a long server secret the offer stays unavailable,
    /// because a plain code of a ten-digit number could easily be reversed. The secret must never
    /// change after launch, or numbers that already used the offer would no longer be recognised.
    /// </summary>
    public static byte[]? CustomerKey(string? secret, string? mobileNo)
    {
        var digits = new string((mobileNo ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length > 10) digits = digits[^10..];
        if (string.IsNullOrWhiteSpace(secret) || secret.Trim().Length < 32 || digits.Length != 10) return null;
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim()), Encoding.UTF8.GetBytes(digits));
    }

    /// <summary>False until Database/FirstBookingOffer.sql has been run.</summary>
    public static bool IsInstalled(IDbConnection db, IDbTransaction? tx = null) =>
        db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.QuoteBookingOffer', N'P') IS NULL THEN 0 ELSE 1 END",
            transaction: tx) == 1;

    public static (BookingOfferQuote Quote, byte[]? Key) Quote(IDbConnection db, IDbTransaction? tx, string? secret,
        Guid customer, Guid? salon, string? bookingDate, decimal? amount)
    {
        if (!IsInstalled(db, tx)) return (BookingOfferQuote.Unavailable, null);
        var mobileNo = db.QueryFirstOrDefault<string>(
            "SELECT TOP(1) MobileNo FROM dbo.CustomerRegistration WHERE CustomerID=@customer AND ISNULL(IsDeleted,0)=0;",
            new { customer }, tx);
        var key = CustomerKey(secret, mobileNo);
        if (key is null) return (BookingOfferQuote.Unavailable, null);
        var quote = db.QuerySingle<BookingOfferQuote>("dbo.QuoteBookingOffer", new
        {
            CustomerID = customer,
            CustomerKey = key,
            SalonID = salon,
            BookingDate = bookingDate,
            Amount = amount
        }, tx, commandType: CommandType.StoredProcedure);
        return (quote, key);
    }
}
