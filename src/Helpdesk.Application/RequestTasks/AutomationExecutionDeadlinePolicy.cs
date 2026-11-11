using Helpdesk.Shared.Models;

namespace Helpdesk.Application.RequestTasks;

/// <summary>Execution policy, independent of task/manual approval SLA clocks.</summary>
public static class AutomationExecutionDeadlinePolicy
{
    public const int DefaultExpectedRuntimeSeconds = 30 * 60;
    public const int DefaultGraceSeconds = 10 * 60;
    public static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan UnknownOutcomeReconciliationInterval = TimeSpan.FromMinutes(5);

    public static long ResolveDeadlineUtcTicks(RequestTask task, DateTimeOffset now)
    {
        if (task.AutomationDeadlineUtcTicks is > 0)
            return task.AutomationDeadlineUtcTicks.Value;

        // An active legacy row without a trusted attempt start cannot safely acquire
        // another execution window. Make its uncertainty visible on the first sweep.
        if (task.StartedAt is not { } startedAt)
            return now.UtcTicks;

        var runtime = task.ExpectedRuntimeSeconds is > 0
            ? task.ExpectedRuntimeSeconds.Value : DefaultExpectedRuntimeSeconds;
        var grace = task.GraceSeconds is >= 0 ? task.GraceSeconds.Value : DefaultGraceSeconds;
        var seconds = task.HardTimeoutSeconds is > 0
            ? task.HardTimeoutSeconds.Value : (long)runtime + grace;
        var availableTicks = DateTimeOffset.MaxValue.UtcTicks - startedAt.UtcTicks;
        return startedAt.UtcTicks + Math.Min(availableTicks, seconds * TimeSpan.TicksPerSecond);
    }

    public static void StartAttempt(RequestTask task, DateTimeOffset startedAt)
    {
        task.AutomationDeadlineUtcTicks = null;
        task.AutomationDeadlineUtcTicks = ResolveDeadlineUtcTicks(task, startedAt);
        task.AutomationNextCheckUtcTicks = Math.Min(task.AutomationDeadlineUtcTicks.Value,
            startedAt.Add(ReconciliationInterval).UtcTicks);
        task.AutomationTimedOutAt = null;
        task.AutomationTimedOutAttemptId = null;
        task.AutomationReconciliationLeaseId = null;
        task.AutomationReconciliationLeaseUntilUtcTicks = null;
        task.AutomationContinuationPending = false;
    }
}
