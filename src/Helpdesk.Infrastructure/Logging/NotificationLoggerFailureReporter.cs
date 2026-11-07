namespace Helpdesk.Infrastructure.Logging;

/// <summary>At most one nonsecret failure record per provider lifetime; never logs through ILogger.</summary>
internal sealed class NotificationLoggerFailureReporter
{
    private int _reported;

    public void Report()
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0)
            return;
        try
        {
            Console.Error.WriteLine("RatelDesk notification logging failed. Subsequent provider failures are suppressed until restart.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("RatelDesk notification logging could not write its bounded failure diagnostic.");
        }
    }
}
