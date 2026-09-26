using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.Data.SqlClient;
using System.Data;

namespace InfinityHairartsAPI.Services;

public sealed class BookingQrService
{
    private const string Prefix = "infinityhairarts:booking:v1:";
    private readonly string _connectionString;

    public BookingQrService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is not configured.");
    }

    public async Task<CompleteBookingQrResponse> CompleteAsync(
        string qrReference,
        Guid salonOwnerId,
        Guid salonMasterId,
        CancellationToken cancellationToken)
    {
        var (bookingId, timeAllocationId) = ParseReference(qrReference);
        await using var connection = new SqlConnection(_connectionString);
        return await connection.QuerySingleAsync<CompleteBookingQrResponse>(
            new CommandDefinition(
                "dbo.completeBookingFromQr",
                new
                {
                    SeatBookingDetailsID = bookingId,
                    TimeAllocationID = timeAllocationId,
                    SalonOwnerID = salonOwnerId,
                    SalonMasterID = salonMasterId
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    private static (Guid BookingId, Guid TimeAllocationId) ParseReference(string value)
    {
        var reference = value?.Trim() ?? string.Empty;
        if (!reference.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("This is not an Infinity Hair Arts booking QR code.");
        }

        var parts = reference[Prefix.Length..].Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !Guid.TryParse(parts[0], out var bookingId) ||
            !Guid.TryParse(parts[1], out var timeAllocationId) ||
            bookingId == Guid.Empty ||
            timeAllocationId == Guid.Empty)
        {
            throw new FormatException("The booking QR code is invalid.");
        }

        return (bookingId, timeAllocationId);
    }
}
