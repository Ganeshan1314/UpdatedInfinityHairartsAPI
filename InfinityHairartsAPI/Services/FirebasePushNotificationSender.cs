using System.Globalization;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace InfinityHairartsAPI.Services;

public sealed record PushSendResult(
    int SuccessCount,
    IReadOnlyCollection<string> InvalidTokens,
    IReadOnlyCollection<string> Errors);

public sealed class FirebasePushNotificationSender
{
    private readonly FirebaseMessaging? _messaging;

    public bool IsConfigured => _messaging is not null;
    private readonly ILogger<FirebasePushNotificationSender> _logger;

    public FirebasePushNotificationSender(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<FirebasePushNotificationSender> logger)
    {
        _logger = logger;
        var projectId = configuration["Fcm:ProjectId"]?.Trim();
        var configuredPath = configuration["Fcm:ServiceAccountJson"]?.Trim();

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(configuredPath))
        {
            _logger.LogWarning("Firebase notifications are disabled because Fcm configuration is incomplete.");
            return;
        }

        var credentialPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        if (!File.Exists(credentialPath))
        {
            _logger.LogWarning("Firebase notifications are disabled because the service-account file was not found.");
            return;
        }

        try
        {
            var firebaseApp = FirebaseApp.Create(
                new AppOptions
                {
                    Credential = CredentialFactory
                        .FromFile<ServiceAccountCredential>(credentialPath)
                        .ToGoogleCredential(),
                    ProjectId = projectId
                },
                "InfinityHairArtsBookingReminders");
            _messaging = FirebaseMessaging.GetMessaging(firebaseApp);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Firebase notifications are disabled because Firebase initialization failed.");
        }
    }

    public async Task<PushSendResult> SendBookingReminderAsync(
        BookingReminderCandidate reminder,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        var appointmentTime = reminder.AppointmentStartLocal.ToString("hh:mm tt", CultureInfo.InvariantCulture);

        return await SendAsync(
            tokens,
            "Appointment reminder",
            $"Your Infinity Hair Arts booking starts at {appointmentTime}.",
            new Dictionary<string, string>
            {
                ["type"] = "booking-reminder",
                ["route"] = "mybooking",
                ["bookingId"] = reminder.SeatBookingDetailsID.ToString("D"),
                ["appointmentStart"] = reminder.AppointmentStartLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            },
            $"booking-{reminder.SeatBookingDetailsID:D}",
            cancellationToken);
    }

    private async Task<PushSendResult> SendAsync(
        IReadOnlyList<string> tokens,
        string title,
        string body,
        Dictionary<string, string> data,
        string tag,
        CancellationToken cancellationToken)
    {
        if (_messaging is null)
        {
            throw new InvalidOperationException("Firebase messaging is not configured on the API server.");
        }

        var successCount = 0;
        var invalidTokens = new List<string>();
        var errors = new List<string>();

        foreach (var tokenBatch in tokens.Chunk(500))
        {
            var batch = tokenBatch.ToArray();
            var response = await _messaging.SendEachForMulticastAsync(
                new MulticastMessage
                {
                    Tokens = batch,
                    Notification = new Notification
                    {
                        Title = title,
                        Body = body
                    },
                    Data = data,
                    Android = new AndroidConfig
                    {
                        Priority = Priority.High,
                        Notification = new AndroidNotification
                        {
                            ChannelId = "booking_reminders",
                            DefaultSound = true,
                            Tag = tag
                        }
                    }
                },
                cancellationToken);

            successCount += response.SuccessCount;
            for (var index = 0; index < response.Responses.Count; index++)
            {
                var sendResponse = response.Responses[index];
                if (sendResponse.IsSuccess)
                {
                    continue;
                }

                if (sendResponse.Exception is FirebaseMessagingException firebaseException &&
                    firebaseException.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                {
                    invalidTokens.Add(batch[index]);
                }

                errors.Add(sendResponse.Exception?.Message ?? "Firebase rejected a reminder notification.");
            }
        }

        return new PushSendResult(successCount, invalidTokens, errors);
    }
}
