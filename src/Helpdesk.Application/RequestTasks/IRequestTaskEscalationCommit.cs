using Helpdesk.Shared.Models;

namespace Helpdesk.Application.RequestTasks;

public interface IRequestTaskEscalationCommit
{
    Task<bool> TryCommitAsync(RequestTask task, Func<Task> publish, CancellationToken ct);
}
