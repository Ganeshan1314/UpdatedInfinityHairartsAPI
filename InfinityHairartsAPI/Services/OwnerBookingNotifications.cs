using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public class OwnerBookingNotification
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

public sealed class OwnerPushDelivery : OwnerBookingNotification
{
    public long DeliveryID { get; set; }
    public long DeviceID { get; set; }
    public int Generation { get; set; }
    public Guid LeaseID { get; set; }
    public string DeviceToken { get; set; } = "";
}

public sealed record OwnerNotificationInbox(IReadOnlyList<OwnerBookingNotification> Items, int UnreadCount);

public sealed class OwnerBookingNotificationRepository(IConfiguration configuration)
{
    private SqlConnection Connection() => new(configuration.GetConnectionString("dbconnection"));

    public async Task<OwnerNotificationInbox> GetAsync(Guid owner, Guid salon, CancellationToken ct)
    {
        await using var db = Connection();
        using var results = await db.QueryMultipleAsync(new CommandDefinition("dbo.getOwnerBookingNotifications",
            new { SalonOwnerID = owner, SalonMasterID = salon }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return new((await results.ReadAsync<OwnerBookingNotification>()).AsList(), await results.ReadSingleAsync<int>());
    }

    public Task ReadAsync(Guid owner, Guid salon, long throughId, CancellationToken ct) => ExecuteAsync(
        "dbo.readOwnerBookingNotifications", new { SalonOwnerID = owner, SalonMasterID = salon, ThroughID = throughId }, ct);

    public Task RegisterAsync(Guid owner, Guid salon, string token, CancellationToken ct) => ExecuteAsync(
        "dbo.registerOwnerPushDevice", new { SalonOwnerID = owner, SalonMasterID = salon, DeviceToken = token }, ct);

    public Task UnregisterAsync(Guid owner, string token, CancellationToken ct) => ExecuteAsync(
        "dbo.unregisterOwnerPushDevice", new { SalonOwnerID = owner, DeviceToken = token }, ct);

    public async Task<IReadOnlyList<OwnerPushDelivery>> ClaimAsync(CancellationToken ct)
    {
        await using var db = Connection();
        return (await db.QueryAsync<OwnerPushDelivery>(new CommandDefinition("dbo.claimOwnerBookingPush",
            commandType: CommandType.StoredProcedure, cancellationToken: ct))).AsList();
    }

    public Task FinishAsync(OwnerPushDelivery delivery, bool success, bool invalid, string? error, CancellationToken ct) => ExecuteAsync(
        "dbo.finishOwnerBookingPush", new { delivery.DeliveryID, delivery.LeaseID, Success = success, InvalidToken = invalid,
            Error = error?[..Math.Min(error.Length, 1000)] }, ct);

    public async Task<bool> IsCurrentAsync(OwnerPushDelivery delivery, CancellationToken ct)
    {
        await using var db = Connection();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition("dbo.isOwnerBookingPushCurrent",
            new { delivery.DeliveryID, delivery.LeaseID }, commandType: CommandType.StoredProcedure, cancellationToken: ct)) == 1;
    }

    private async Task ExecuteAsync(string procedure, object parameters, CancellationToken ct)
    {
        await using var db = Connection();
        await db.ExecuteAsync(new CommandDefinition(procedure, parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }
}

public sealed class OwnerBookingPushWorker(
    OwnerBookingNotificationRepository repository,
    FirebasePushNotificationSender sender,
    ILogger<OwnerBookingPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (sender.IsConfigured)
                    foreach (var delivery in await repository.ClaimAsync(stoppingToken))
                    {
                        try
                        {
                            if (!await repository.IsCurrentAsync(delivery, stoppingToken)) continue;
                            var result = await sender.SendOwnerBookingAsync(delivery, stoppingToken);
                            await repository.FinishAsync(delivery, result.SuccessCount > 0, result.InvalidTokens.Count > 0,
                                result.Errors.FirstOrDefault(), stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            logger.LogError(exception, "Owner booking push delivery {DeliveryID} failed; it will retry.", delivery.DeliveryID);
                            await repository.FinishAsync(delivery, false, false, "Push delivery failed.", stoppingToken);
                        }
                    }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Owner booking notification delivery check failed."); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
