namespace Helpdesk.Application.RequestTasks;

public interface IRequestTaskAssignees
{
    Task<HashSet<string>> EligibleDefaultsAsync(string? organizationId, string[] defaultIds, CancellationToken ct);
}
