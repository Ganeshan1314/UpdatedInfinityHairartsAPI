using Dapper;
using Microsoft.Data.SqlClient;

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
    private const string SchemaSql = """
        IF OBJECT_ID(N'dbo.CustomerPushDevice', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.CustomerPushDevice
            (
                CustomerPushDeviceID uniqueidentifier NOT NULL
                    CONSTRAINT PK_CustomerPushDevice PRIMARY KEY
                    CONSTRAINT DF_CustomerPushDevice_ID DEFAULT NEWID(),
                CustomerID uniqueidentifier NOT NULL,
                DeviceToken nvarchar(512) NOT NULL,
                Platform varchar(20) NOT NULL,
                IsActive bit NOT NULL CONSTRAINT DF_CustomerPushDevice_IsActive DEFAULT (1),
                CreatedUtc datetime2(0) NOT NULL CONSTRAINT DF_CustomerPushDevice_CreatedUtc DEFAULT SYSUTCDATETIME(),
                LastSeenUtc datetime2(0) NOT NULL CONSTRAINT DF_CustomerPushDevice_LastSeenUtc DEFAULT SYSUTCDATETIME()
            );

            CREATE UNIQUE INDEX UX_CustomerPushDevice_DeviceToken
                ON dbo.CustomerPushDevice(DeviceToken);
            CREATE INDEX IX_CustomerPushDevice_Customer
                ON dbo.CustomerPushDevice(CustomerID, IsActive);
        END;

        IF OBJECT_ID(N'dbo.BookingReminderLog', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.BookingReminderLog
            (
                BookingReminderLogID uniqueidentifier NOT NULL
                    CONSTRAINT PK_BookingReminderLog PRIMARY KEY
                    CONSTRAINT DF_BookingReminderLog_ID DEFAULT NEWID(),
                SeatBookingDetailsID uniqueidentifier NOT NULL,
                AppointmentStartLocal datetime2(0) NOT NULL,
                Status varchar(20) NOT NULL,
                AttemptCount int NOT NULL CONSTRAINT DF_BookingReminderLog_AttemptCount DEFAULT (0),
                ClaimExpiresUtc datetime2(0) NULL,
                SentUtc datetime2(0) NULL,
                LastError nvarchar(1000) NULL,
                CreatedUtc datetime2(0) NOT NULL CONSTRAINT DF_BookingReminderLog_CreatedUtc DEFAULT SYSUTCDATETIME(),
                UpdatedUtc datetime2(0) NOT NULL CONSTRAINT DF_BookingReminderLog_UpdatedUtc DEFAULT SYSUTCDATETIME()
            );

            CREATE UNIQUE INDEX UX_BookingReminderLog_BookingStart
                ON dbo.BookingReminderLog(SeatBookingDetailsID, AppointmentStartLocal);
        END;
        """;

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
            await connection.ExecuteAsync(new CommandDefinition(SchemaSql, cancellationToken: cancellationToken));
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

        const string sql = """
            MERGE dbo.CustomerPushDevice WITH (HOLDLOCK) AS Target
            USING (SELECT @DeviceToken AS DeviceToken) AS Source
                ON Target.DeviceToken = Source.DeviceToken
            WHEN MATCHED THEN
                UPDATE SET
                    CustomerID = @CustomerID,
                    Platform = @Platform,
                    IsActive = 1,
                    LastSeenUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (CustomerID, DeviceToken, Platform)
                VALUES (@CustomerID, @DeviceToken, @Platform);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { CustomerID = customerId, DeviceToken = token, Platform = platform },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BookingReminderCandidate>> GetDueRemindersAsync(
        DateTime nowLocal,
        DateTime reminderCutoffLocal,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = """
            WITH AppointmentTimes AS
            (
                SELECT
                    SB.SeatBookingDetailsID,
                    SB.CustomerID,
                    SB.SeatCount,
                    DATEADD(
                        SECOND,
                        DATEDIFF(SECOND, CAST('00:00:00' AS time), MIN(TA.FullTiming)),
                        CAST(SB.BookingDate AS datetime2(0))) AS AppointmentStartLocal
                FROM dbo.SeatBookingDetails SB
                INNER JOIN dbo.CustomerTimeSelection CTS
                    ON CTS.SeatBookingDetailsID = SB.SeatBookingDetailsID
                INNER JOIN dbo.TimeAllocation TA
                    ON TA.TimeAllocationID = CTS.TimeAllocationID
                WHERE SB.BookingStatusMasterID = 2
                  AND ISNULL(TA.IsDeleted, 0) = 0
                GROUP BY
                    SB.SeatBookingDetailsID,
                    SB.CustomerID,
                    SB.SeatCount,
                    SB.BookingDate
            )
            SELECT TOP (100)
                A.SeatBookingDetailsID,
                A.CustomerID,
                A.AppointmentStartLocal,
                A.SeatCount
            FROM AppointmentTimes A
            WHERE A.AppointmentStartLocal > @NowLocal
              AND A.AppointmentStartLocal <= @ReminderCutoffLocal
              AND EXISTS
              (
                  SELECT 1
                  FROM dbo.CustomerPushDevice D
                  WHERE D.CustomerID = A.CustomerID
                    AND D.IsActive = 1
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM dbo.BookingReminderLog L
                  WHERE L.SeatBookingDetailsID = A.SeatBookingDetailsID
                    AND L.AppointmentStartLocal = A.AppointmentStartLocal
                    AND
                    (
                        L.Status = 'Sent'
                        OR L.AttemptCount >= 5
                        OR (L.Status = 'Processing' AND L.ClaimExpiresUtc > @NowUtc)
                    )
              )
            ORDER BY A.AppointmentStartLocal;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var reminders = await connection.QueryAsync<BookingReminderCandidate>(new CommandDefinition(
            sql,
            new { NowLocal = nowLocal, ReminderCutoffLocal = reminderCutoffLocal, NowUtc = nowUtc },
            cancellationToken: cancellationToken));
        return reminders.AsList();
    }

    public async Task<bool> TryClaimReminderAsync(
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SET XACT_ABORT ON;
            SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
            BEGIN TRANSACTION;

            DECLARE @Claimed bit = 0;

            IF NOT EXISTS
            (
                SELECT 1
                FROM dbo.BookingReminderLog WITH (UPDLOCK, HOLDLOCK)
                WHERE SeatBookingDetailsID = @SeatBookingDetailsID
                  AND AppointmentStartLocal = @AppointmentStartLocal
            )
            BEGIN
                INSERT dbo.BookingReminderLog
                    (SeatBookingDetailsID, AppointmentStartLocal, Status, AttemptCount, ClaimExpiresUtc)
                VALUES
                    (@SeatBookingDetailsID, @AppointmentStartLocal, 'Processing', 1, DATEADD(MINUTE, 5, @NowUtc));
                SET @Claimed = 1;
            END
            ELSE
            BEGIN
                UPDATE dbo.BookingReminderLog
                SET
                    Status = 'Processing',
                    AttemptCount = AttemptCount + 1,
                    ClaimExpiresUtc = DATEADD(MINUTE, 5, @NowUtc),
                    LastError = NULL,
                    UpdatedUtc = @NowUtc
                WHERE SeatBookingDetailsID = @SeatBookingDetailsID
                  AND AppointmentStartLocal = @AppointmentStartLocal
                  AND AttemptCount < 5
                  AND
                  (
                      Status = 'Failed'
                      OR (Status = 'Processing' AND ClaimExpiresUtc <= @NowUtc)
                  );

                IF @@ROWCOUNT > 0 SET @Claimed = 1;
            END;

            COMMIT TRANSACTION;
            SELECT CAST(@Claimed AS int);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var claimed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new
            {
                reminder.SeatBookingDetailsID,
                reminder.AppointmentStartLocal,
                NowUtc = nowUtc
            },
            cancellationToken: cancellationToken));
        return claimed == 1;
    }

    public async Task<IReadOnlyList<string>> GetActiveTokensAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DeviceToken
            FROM dbo.CustomerPushDevice
            WHERE CustomerID = @CustomerID
              AND IsActive = 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var tokens = await connection.QueryAsync<string>(new CommandDefinition(
            sql,
            new { CustomerID = customerId },
            cancellationToken: cancellationToken));
        return tokens.Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task MarkSentAsync(
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.BookingReminderLog
            SET Status = 'Sent', SentUtc = @NowUtc, ClaimExpiresUtc = NULL, UpdatedUtc = @NowUtc
            WHERE SeatBookingDetailsID = @SeatBookingDetailsID
              AND AppointmentStartLocal = @AppointmentStartLocal;
            """;

        await ExecuteReminderUpdateAsync(sql, reminder, nowUtc, null, cancellationToken);
    }

    public async Task MarkFailedAsync(
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        string error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.BookingReminderLog
            SET Status = 'Failed', LastError = @Error, ClaimExpiresUtc = NULL, UpdatedUtc = @NowUtc
            WHERE SeatBookingDetailsID = @SeatBookingDetailsID
              AND AppointmentStartLocal = @AppointmentStartLocal;
            """;

        await ExecuteReminderUpdateAsync(sql, reminder, nowUtc, error[..Math.Min(error.Length, 1000)], cancellationToken);
    }

    public async Task DeactivateTokensAsync(
        IReadOnlyCollection<string> tokens,
        CancellationToken cancellationToken)
    {
        if (tokens.Count == 0)
        {
            return;
        }

        const string sql = """
            UPDATE dbo.CustomerPushDevice
            SET IsActive = 0, LastSeenUtc = SYSUTCDATETIME()
            WHERE DeviceToken IN @Tokens;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Tokens = tokens }, cancellationToken: cancellationToken));
    }

    private async Task ExecuteReminderUpdateAsync(
        string sql,
        BookingReminderCandidate reminder,
        DateTime nowUtc,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                reminder.SeatBookingDetailsID,
                reminder.AppointmentStartLocal,
                NowUtc = nowUtc,
                Error = error
            },
            cancellationToken: cancellationToken));
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
