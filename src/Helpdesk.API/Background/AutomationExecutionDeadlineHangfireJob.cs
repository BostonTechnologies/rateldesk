using Hangfire;

namespace Helpdesk.API.Background;

public sealed class AutomationExecutionDeadlineHangfireJob(AutomationExecutionDeadlineProcessor processor)
{
    [DisableConcurrentExecution(timeoutInSeconds: 10 * 60)]
    public Task<int> RunAsync(CancellationToken ct) => processor.ProcessAsync(ct);
}
