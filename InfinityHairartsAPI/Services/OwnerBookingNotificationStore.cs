using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public sealed class OwnerBookingNotification
{
    public long NotificationID { get; set; }
    public Guid SeatBookingDetailsID { get; set; }
    public Guid SalonMasterID { get; set; }
    public string CustomerName { get; set; } = "";
    public DateTime BookingDate { get; set; }
    public TimeSpan? StartTime { get; set; }
    public int SeatCount { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool IsRead { get; set; }
}
public sealed record OwnerBookingInbox(IReadOnlyList<OwnerBookingNotification> Items, int UnreadCount);
public sealed class OwnerBookingPushDelivery
{
    public long DeliveryID { get; set; }
    public Guid LeaseID { get; set; }
    public Guid BindingID { get; set; }
    public Guid OwnerPushDeviceID { get; set; }
    public long NotificationID { get; set; }
    public Guid SeatBookingDetailsID { get; set; }
    public Guid SalonMasterID { get; set; }
    public string CustomerName { get; set; } = "";
    public DateTime BookingDate { get; set; }
    public TimeSpan? StartTime { get; set; }
    public int SeatCount { get; set; }
    public string DeviceToken { get; set; } = "";
}

public sealed class OwnerBookingNotificationStore(IConfiguration configuration)
{
    private SqlConnection Connection() => new(configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The dbconnection connection string is required."));
    private static CommandDefinition Command(string procedure, object values, CancellationToken cancellationToken) =>
        new(procedure, values, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);

    public async Task<OwnerBookingInbox> GetInboxAsync(Guid ownerId, Guid salonId, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        using var results = await connection.QueryMultipleAsync(Command("dbo.GetOwnerBookingNotifications",
            new { OwnerID = ownerId, SalonID = salonId }, cancellationToken));
        var items = (await results.ReadAsync<OwnerBookingNotification>()).ToList();
        return new(items, await results.ReadSingleAsync<int>());
    }
    public Task RegisterAsync(Guid ownerId, string token, CancellationToken cancellationToken) =>
        ExecuteAsync("dbo.RegisterOwnerPushDevice", new { OwnerID = ownerId, Token = token }, cancellationToken);
    public Task UnregisterAsync(Guid ownerId, string token, CancellationToken cancellationToken) =>
        ExecuteAsync("dbo.UnregisterOwnerPushDevice", new { OwnerID = ownerId, Token = token }, cancellationToken);
    public Task ReadAsync(Guid ownerId, Guid salonId, long throughId, CancellationToken cancellationToken) =>
        ExecuteAsync("dbo.ReadOwnerBookingNotifications", new { OwnerID = ownerId, SalonID = salonId, ThroughID = throughId }, cancellationToken);
    public async Task<IReadOnlyList<OwnerBookingPushDelivery>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return (await connection.QueryAsync<OwnerBookingPushDelivery>(Command("dbo.ClaimOwnerBookingPush",
            new { LeaseID = Guid.NewGuid() }, cancellationToken))).ToList();
    }
    public Task FinishAsync(OwnerBookingPushDelivery delivery, bool success, bool invalidToken, string? error, CancellationToken cancellationToken) =>
        ExecuteAsync("dbo.FinishOwnerBookingPush", new {
            delivery.DeliveryID, delivery.LeaseID, Success = success, InvalidToken = invalidToken,
            Error = error is { Length: > 1000 } ? error[..1000] : error
        }, cancellationToken);
    private async Task ExecuteAsync(string procedure, object values, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(Command(procedure, values, cancellationToken));
    }
}
