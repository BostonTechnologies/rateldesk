using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.Models;

namespace Helpdesk.Tests.Application.RequestTasks;

public sealed class AutomationExecutionDeadlinePolicyTests
{
    [Theory]
    [InlineData(null, null, null, 2400)]
    [InlineData(120, 30, null, 150)]
    [InlineData(120, 0, null, 120)]
    [InlineData(120, -1, null, 720)]
    [InlineData(120, 30, 900, 900)]
    [InlineData(-1, -1, -1, 2400)]
    [InlineData(86400, 600, 172800, 172800)]
    public void Deadline_uses_saved_hard_limit_or_runtime_grace_and_bounded_legacy_defaults(int? expected, int? grace, int? hard, int seconds)
    {
        var start = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var task = new RequestTask { StartedAt = start, ExpectedRuntimeSeconds = expected, GraceSeconds = grace, HardTimeoutSeconds = hard };
        var deadline = AutomationExecutionDeadlinePolicy.ResolveDeadlineUtcTicks(task, start.AddSeconds(30));
        Assert.Equal(start.AddSeconds(seconds).UtcTicks, deadline);
        Assert.True(start.AddSeconds(seconds - 1).UtcTicks < deadline);
        Assert.True(start.AddSeconds(seconds).UtcTicks >= deadline);
        Assert.True(start.AddSeconds(seconds + 1).UtcTicks >= deadline);
    }

    [Fact]
    public void Persisted_attempt_deadline_survives_runtime_policy_changes_and_missing_legacy_start_is_immediately_reconciled()
    {
        var now = DateTimeOffset.UtcNow;
        var task = new RequestTask { AutomationDeadlineUtcTicks = now.AddHours(2).UtcTicks, StartedAt = now, HardTimeoutSeconds = 1 };
        Assert.Equal(now.AddHours(2).UtcTicks, AutomationExecutionDeadlinePolicy.ResolveDeadlineUtcTicks(task, now));
        task.AutomationDeadlineUtcTicks = null;
        task.StartedAt = null;
        Assert.Equal(now.UtcTicks, AutomationExecutionDeadlinePolicy.ResolveDeadlineUtcTicks(task, now));
    }
}
