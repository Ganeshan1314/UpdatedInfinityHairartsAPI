using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public interface IOwnerBookingPushSender
{
    bool IsConfigured { get; }
    Task<PushSendResult> SendOwnerBookingAsync(OwnerBookingPushDelivery delivery, CancellationToken cancellationToken);
}

public sealed class OwnerBookingPushWorker(OwnerBookingNotificationStore store, IOwnerBookingPushSender sender,
    ILogger<OwnerBookingPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var missingSchemaLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            try
            {
                if (sender.IsConfigured) await ProcessPendingAsync(stoppingToken);
                missingSchemaLogged = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (SqlException error) when (error.Number is 208 or 2812)
            {
                if (!missingSchemaLogged) logger.LogWarning("Owner booking alerts require Database/OwnerBookingNotifications.sql to be installed.");
                missingSchemaLogged = true;
                delay = TimeSpan.FromSeconds(60);
            }
            catch (Exception error) { logger.LogError(error, "The owner booking push check failed."); }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        if (!sender.IsConfigured) return;
        var deliveries = await store.ClaimAsync(cancellationToken);
        // A bounded batch completes before its two-minute delivery lease expires.
        await Task.WhenAll(deliveries.Select(async delivery => {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var result = await sender.SendOwnerBookingAsync(delivery, timeout.Token);
                await store.FinishAsync(delivery, result.SuccessCount > 0, result.InvalidTokens.Contains(delivery.DeviceToken),
                    result.SuccessCount > 0 ? null : string.Join(" | ", result.Errors), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* Lease makes interrupted work retryable. */ }
            catch (Exception error)
            {
                await store.FinishAsync(delivery, false, false, error.Message, cancellationToken);
                logger.LogWarning("Owner booking push delivery {DeliveryID} failed; a retry is queued.", delivery.DeliveryID);
            }
        }));
    }
}
