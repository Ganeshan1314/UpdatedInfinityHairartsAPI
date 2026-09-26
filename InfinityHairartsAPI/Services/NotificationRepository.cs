using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;

namespace InfinityHairartsAPI.Services;

public sealed class BookingReminderCandidate
{
    public Guid SeatBookingDetailsID { get; set; }
    public Guid CustomerID { get; set; }
    public DateTime AppointmentStartLocal { get; set; }
    public int SeatCount { get; set; }
}

public sealed class NotificationRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public NotificationRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("ConnectionStrings:dbconnection is required.");
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "dbo.ensurePushNotificationSchema",
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    public async Task RegisterDeviceAsync(
        Guid customerId,
        string token,
        string platform,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.registerCustomerPushDevice",
            new { CustomerID = customerId, DeviceToken = token, Platform = platform },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BookingReminderCandidate>> GetDueRemindersAsync(
        DateTime nowLocal,
        DateTime reminderCutoffLocal,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var reminders = await connection.QueryAsync<BookingReminderCandidate>(new CommandDefinition(
            "dbo.getDueBookingReminders",
            new { NowLocal = nowLocal, ReminderCutoffLocal = reminderCutoffLocal, NowUtc = nowUtc },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
        return reminders.AsList();
    }

    public async Task<bool> TryClaimReminderAsync(
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var claimed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.tryClaimBookingReminder",
            new
            {
                reminder.SeatBookingDetailsID,
                reminder.AppointmentStartLocal,
                NowUtc = nowUtc
            },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
        return claimed == 1;
    }

    public async Task<IReadOnlyList<string>> GetActiveTokensAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var tokens = await connection.QueryAsync<string>(new CommandDefinition(
            "dbo.getActiveCustomerPushTokens",
            new { CustomerID = customerId },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
        return tokens.Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task MarkSentAsync(
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await ExecuteReminderUpdateAsync(
            "dbo.markBookingReminderSent",
            reminder,
            nowUtc,
            null,
            cancellationToken);
    }

    public async Task MarkFailedAsync(
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        string error,
        CancellationToken cancellationToken)
    {
        await ExecuteReminderUpdateAsync(
            "dbo.markBookingReminderFailed",
            reminder,
            nowUtc,
            error[..Math.Min(error.Length, 1000)],
            cancellationToken);
    }

    public async Task DeactivateTokensAsync(
        IReadOnlyCollection<string> tokens,
        CancellationToken cancellationToken)
    {
        if (tokens.Count == 0)
        {
            return;
        }

        var tokenTable = new DataTable();
        tokenTable.Columns.Add("DeviceToken", typeof(string));
        foreach (var token in tokens)
        {
            tokenTable.Rows.Add(token);
        }

        var parameters = new DynamicParameters();
        parameters.Add("Tokens", tokenTable.AsTableValuedParameter("dbo.DeviceTokenList"));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.deactivateCustomerPushDevices",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    private async Task ExecuteReminderUpdateAsync(
        string procedureName,
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            procedureName,
            new
            {
                reminder.SeatBookingDetailsID,
                reminder.AppointmentStartLocal,
                NowUtc = nowUtc,
                Error = error
            },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
