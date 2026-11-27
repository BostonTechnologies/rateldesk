using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Application.RequestTasks;

public interface IRequestTaskLifecycleService
{
    Task<RequestTask> StartAsync(string taskId, CancellationToken ct, RequestTaskQueueAuthority? queueAuthority = null);
    Task<RequestTask> CompleteAsync(string taskId, string? notes, CancellationToken ct, RequestTaskQueueAuthority? queueAuthority = null);
    Task<RequestTask> FailAsync(string taskId, string reason, CancellationToken ct, RequestTaskQueueAuthority? queueAuthority = null);
    Task<RequestTask> RetryAsync(string taskId, CancellationToken ct, RequestTaskQueueAuthority? queueAuthority = null);
}

public sealed record RequestTaskQueueAuthority(string TaskId, string RequestId, long ExpectedAssignmentVersion,
    Func<CancellationToken, Task<CurrentUserAccessProfile>> ResolveAccessAsync);
