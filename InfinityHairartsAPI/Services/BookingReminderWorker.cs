using Microsoft.Extensions.Options;

namespace InfinityHairartsAPI.Services;

public sealed class BookingReminderOptions
{
    public int MinutesBefore { get; set; } = 30;
    public int PollingIntervalSeconds { get; set; } = 60;
    public string TimeZoneId { get; set; } = "Asia/Kolkata";
}

public sealed class BookingReminderWorker : BackgroundService
{
    private readonly NotificationRepository _repository;
    private readonly FirebasePushNotificationSender _sender;
    private readonly BookingReminderOptions _options;
    private readonly ILogger<BookingReminderWorker> _logger;
    private readonly TimeZoneInfo _timeZone;

    public BookingReminderWorker(
        NotificationRepository repository,
        FirebasePushNotificationSender sender,
        IOptions<BookingReminderOptions> options,
        ILogger<BookingReminderWorker> logger)
    {
        _repository = repository;
        _sender = sender;
        _options = options.Value;
        _logger = logger;
        _timeZone = ResolveTimeZone(_options.TimeZoneId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollingSeconds = Math.Max(15, _options.PollingIntervalSeconds);
        var minutesBefore = Math.Max(1, _options.MinutesBefore);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_sender.IsConfigured)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollingSeconds), stoppingToken);
                    continue;
                }

                var nowUtc = DateTime.UtcNow;
                var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _timeZone);
                var dueReminders = await _repository.GetDueRemindersAsync(
                    nowLocal,
                    nowLocal.AddMinutes(minutesBefore),
                    nowUtc,
                    stoppingToken);

                foreach (var reminder in dueReminders)
                {
                    await ProcessReminderAsync(reminder, nowUtc, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The booking-reminder check failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollingSeconds), stoppingToken);
        }
    }

    private async Task ProcessReminderAsync(
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!await _repository.TryClaimReminderAsync(reminder, nowUtc, cancellationToken))
        {
            return;
        }

        try
        {
            var tokens = await _repository.GetActiveTokensAsync(reminder.CustomerID, cancellationToken);
            if (tokens.Count == 0)
            {
                await _repository.MarkFailedAsync(reminder, nowUtc, "No active device token.", cancellationToken);
                return;
            }

            var result = await _sender.SendBookingReminderAsync(reminder, tokens, cancellationToken);
            await _repository.DeactivateTokensAsync(result.InvalidTokens, cancellationToken);

            if (result.SuccessCount > 0)
            {
                await _repository.MarkSentAsync(reminder, nowUtc, cancellationToken);
                _logger.LogInformation(
                    "Sent booking reminder for {BookingId} to {DeviceCount} device(s).",
                    reminder.SeatBookingDetailsID,
                    result.SuccessCount);
                return;
            }

            var error = result.Errors.Count > 0
                ? string.Join(" | ", result.Errors)
                : "Firebase did not deliver the reminder to any active device.";
            await _repository.MarkFailedAsync(reminder, nowUtc, error, cancellationToken);
        }
        catch (Exception exception)
        {
            await _repository.MarkFailedAsync(reminder, nowUtc, exception.Message, cancellationToken);
            _logger.LogError(exception, "Could not send booking reminder for {BookingId}.", reminder.SeatBookingDetailsID);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string configuredId)
    {
        foreach (var timeZoneId in new[] { configuredId, "Asia/Kolkata", "India Standard Time" }.Distinct())
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("The configured notification time zone is unavailable.");
    }
}
