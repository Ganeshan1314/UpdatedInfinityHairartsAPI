using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

// Completes account deletions once their waiting period has ended. The host can stop an
// idle application, so due requests are checked soon after every start and then hourly.
public sealed class CustomerAccountDeletionWorker(IServiceScopeFactory scopes, CustomerSessionRegistry registry,
    ILogger<CustomerAccountDeletionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await ProcessDueAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Returns the number of requests completed. A failure is logged and retried on the
    // next run; it must never stop the application.
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var completed = 0;
        try
        {
            using var scope = scopes.CreateScope();
            var accounts = scope.ServiceProvider.GetRequiredService<LoginService>();
            foreach (var customerId in await accounts.GetDueAccountDeletionsAsync(cancellationToken))
            {
                try
                {
                    var deleted = await accounts.DeleteCustomerAccountAsync(customerId, cancellationToken);
                    if (deleted.Count == 0) continue;
                    // Ends any session the customer still holds.
                    foreach (var id in deleted) registry.MarkDeleted(id);
                    completed++;
                    logger.LogInformation("A scheduled account deletion was completed.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Scheduled account deletion failed: ErrorType={ErrorType} SqlErrorNumber={SqlErrorNumber}",
                        ex.GetType().Name, (ex as SqlException)?.Number);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Scheduled account deletions could not be checked: ErrorType={ErrorType} SqlErrorNumber={SqlErrorNumber}",
                ex.GetType().Name, (ex as SqlException)?.Number);
        }
        return completed;
    }
}
