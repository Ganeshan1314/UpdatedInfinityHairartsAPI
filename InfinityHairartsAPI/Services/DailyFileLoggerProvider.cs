using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace InfinityHairartsAPI.Services;

internal static class DailyFileLogWriter
{
    private static readonly object Sync = new();
    private static string _logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
    private static int _retainedDays = 30;
    private static DateOnly? _lastCleanupDate;

    public static void Configure(IConfiguration configuration)
    {
        var configuredDirectory = configuration["FileLogging:Directory"]?.Trim();
        var retainedDays = configuration.GetValue<int?>("FileLogging:RetainedDays") ?? 30;

        lock (Sync)
        {
            _logDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "Logs")
                : Path.IsPathRooted(configuredDirectory)
                    ? configuredDirectory
                    : Path.Combine(AppContext.BaseDirectory, configuredDirectory);
            _retainedDays = Math.Max(1, retainedDays);
        }
    }

    public static void Write(
        LogLevel level,
        string category,
        EventId eventId,
        string message,
        Exception? exception,
        IReadOnlyCollection<string>? scopes = null)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var entry = new StringBuilder()
                .Append(now.ToString("O", CultureInfo.InvariantCulture))
                .Append(" [").Append(level).Append("] ")
                .Append('[').Append(category).Append(']');

            if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
            {
                entry.Append(" EventId=").Append(eventId.Id);
                if (!string.IsNullOrWhiteSpace(eventId.Name))
                {
                    entry.Append(':').Append(eventId.Name);
                }
            }

            if (scopes is { Count: > 0 })
            {
                entry.Append(" Scope=").Append(string.Join(" => ", scopes));
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                entry.AppendLine().Append(message);
            }

            if (exception != null)
            {
                entry.AppendLine().Append(exception);
            }

            entry.AppendLine().AppendLine(new string('-', 100));

            lock (Sync)
            {
                Directory.CreateDirectory(_logDirectory);
                var filePath = Path.Combine(_logDirectory, $"api-{now:yyyy-MM-dd}.log");
                File.AppendAllText(filePath, entry.ToString(), new UTF8Encoding(false));
                DeleteExpiredLogs(now);
            }
        }
        catch
        {
            // Logging must never interrupt an API request.
        }
    }

    private static void DeleteExpiredLogs(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.Date);
        if (_lastCleanupDate == today)
        {
            return;
        }

        _lastCleanupDate = today;
        var cutoffUtc = now.UtcDateTime.AddDays(-_retainedDays);
        foreach (var filePath in Directory.EnumerateFiles(_logDirectory, "api-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(filePath) < cutoffUtc)
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // A locked log file can be retried during the next daily cleanup.
            }
        }
    }
}

public sealed class DailyFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly LogLevel _minimumLevel;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public DailyFileLoggerProvider(IConfiguration configuration)
    {
        DailyFileLogWriter.Configure(configuration);
        _minimumLevel = Enum.TryParse<LogLevel>(
            configuration["FileLogging:MinimumLevel"],
            true,
            out var configuredLevel)
            ? configuredLevel
            : LogLevel.Information;
    }

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public void Dispose()
    {
    }

    private sealed class DailyFileLogger : ILogger
    {
        private readonly string _category;
        private readonly DailyFileLoggerProvider _provider;

        public DailyFileLogger(string category, DailyFileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            _provider._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _provider._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null)
            {
                return;
            }

            var scopes = new List<string>();
            _provider._scopeProvider.ForEachScope((scope, values) =>
            {
                var scopeText = scope?.ToString();
                if (!string.IsNullOrWhiteSpace(scopeText))
                {
                    values.Add(scopeText);
                }
            }, scopes);

            DailyFileLogWriter.Write(logLevel, _category, eventId, message, exception, scopes);
        }
    }
}
