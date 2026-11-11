using Helpdesk.Shared.Models;

namespace Helpdesk.Application.RequestTasks;

public interface IRequestTaskStartFence
{
    Task PersistAsync(RequestTask task, RequestTaskStartIntent intent, CancellationToken ct);
}

public sealed record RequestTaskStartIntent(DateTimeOffset StartedAt, string CorrelationId)
{
    public void Apply(RequestTask task)
    {
        if (task.Type == RequestTaskType.Automation)
            task.AutomationAttemptCorrelationId ??= CorrelationId;
        task.Status = RequestTaskStatus.InProgress;
        task.State = TicketState.InProgress;
        task.StartedAt = StartedAt;
        task.UpdatedAt = StartedAt.UtcDateTime;
        if (task.Type == RequestTaskType.Automation)
            AutomationExecutionDeadlinePolicy.StartAttempt(task, StartedAt);
        if (task.TaskSlaMinutes is > 0)
        {
            task.SlaStartedAt = StartedAt;
            task.DueAt = StartedAt.AddMinutes(task.TaskSlaMinutes.Value);
            task.Escalated = false;
            task.EscalatedAt = null;
            task.SlaBreached = false;
        }
    }
}
