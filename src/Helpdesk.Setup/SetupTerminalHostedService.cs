using Microsoft.Extensions.Hosting;
using Termina;

namespace Helpdesk.Setup;

// Termina 0.17 disposes its internal shutdown source before clearing the field.
// Cancelling our own token avoids a second Shutdown call racing that disposal.
internal sealed class SetupTerminalHostedService(TerminaApplication application, IHostApplicationLifetime lifetime)
    : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            try { await application.RunAsync(linked.Token); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { lifetime.StopApplication(); }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        if (_run is not null) await _run.WaitAsync(cancellationToken);
    }

    public void Dispose() => _stop.Dispose();
}
