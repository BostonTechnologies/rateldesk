using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Logging;

internal sealed class NotificationLogger(string categoryName, Action<NotificationLoggerProvider.NotificationLogEntry> enqueue,
    NotificationLoggerFailureReporter failures) : ILogger
{
    private readonly string _categoryName = categoryName;
    private readonly Action<NotificationLoggerProvider.NotificationLogEntry> _enqueue = enqueue;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.Trace || logLevel == LogLevel.Debug || logLevel == LogLevel.Information)
            return false;

        return logLevel == LogLevel.Warning || logLevel == LogLevel.Error || logLevel == LogLevel.Critical;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        if (_categoryName.StartsWith("Helpdesk.Infrastructure.Logging", StringComparison.Ordinal) ||
            NotificationLoggerProvider.IsDispatchingNotification)
            return;

        try
        {
            var formatted = formatter(state, exception);
            var message = string.IsNullOrWhiteSpace(formatted)
                ? exception?.Message ?? "Log entry"
                : formatted;

            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            _enqueue(new NotificationLoggerProvider.NotificationLogEntry(
                logLevel,
                _categoryName,
                message,
                DateTime.UtcNow));
        }
        catch (Exception)
        {
            failures.Report();
        }
    }
}
