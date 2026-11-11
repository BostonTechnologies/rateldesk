using Helpdesk.Application.Events;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Application.RequestTasks;

public sealed class WorkflowDependencyEvaluator(
    IRepository<Request> requests,
    IRepository<RequestForm> requestForms,
    IRepository<RequestTask> requestTasks,
    IRequestFormSchemaParser schemaParser,
    IDomainEventPublisher domainEvents,
    ICorrelationContext correlationContext,
    IRequestTaskReadStore? requestTaskReadStore = null) : IWorkflowDependencyEvaluator
{
    private readonly IRepository<Request> _requests = requests;
    private readonly IRepository<RequestForm> _requestForms = requestForms;
    private readonly IRepository<RequestTask> _requestTasks = requestTasks;
    private readonly IRequestFormSchemaParser _schemaParser = schemaParser;
    private readonly IDomainEventPublisher _domainEvents = domainEvents;
    private readonly ICorrelationContext _correlationContext = correlationContext;

    public async Task<WorkflowDependencyEvaluationResult> EvaluateAsync(string requestId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return new WorkflowDependencyEvaluationResult(0);
        }

        var request = await _requests.GetAsync(requestId);
        if (request is null || string.IsNullOrWhiteSpace(request.RequestFormId))
        {
            return new WorkflowDependencyEvaluationResult(0);
        }

        var requestForm = await _requestForms.GetAsync(request.RequestFormId);
        if (requestForm is null)
        {
            return new WorkflowDependencyEvaluationResult(0);
        }

        var schema = _schemaParser.Parse(requestForm.JsonSchema);
        var orderedTemplates = schema.Tasks
            .Where(x => x.Id != Guid.Empty)
            .OrderBy(x => x.Order)
            .ToList();
        var dependenciesByTemplateId = orderedTemplates
            .Where(x => x.Id != Guid.Empty)
            .ToDictionary(
                x => x.Id,
                x => RequestTaskApprovalGate.GetEffectiveDependencies(x, orderedTemplates)
                    .Where(dep => dep != Guid.Empty)
                    .Distinct()
                    .ToArray());

        ct.ThrowIfCancellationRequested();

        var tasks = await RequestTaskQueries.ForRequestAsync(_requestTasks, requestTaskReadStore, requestId, ct);

        if (tasks.Count == 0)
        {
            return new WorkflowDependencyEvaluationResult(0);
        }

        var tasksByTemplateId = tasks
            .Select(task => new { Task = task, TemplateId = ParseGuid(task.TemplateId) })
            .Where(x => x.TemplateId.HasValue)
            .GroupBy(x => x.TemplateId!.Value)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Task).ToList());

        var now = DateTimeOffset.UtcNow;
        var correlationId = GetCorrelationId();
        var unblockedCount = 0;

        foreach (var task in tasks)
        {
            if (task.Status != RequestTaskStatus.Pending || !task.IsBlocked)
            {
                continue;
            }

            var templateId = ParseGuid(task.TemplateId);
            if (!templateId.HasValue)
            {
                continue;
            }

            if (!dependenciesByTemplateId.TryGetValue(templateId.Value, out var dependencyTemplateIds)
                || dependencyTemplateIds.Length == 0)
            {
                if (await UnblockTaskAsync(task, now, correlationId, ct))
                {
                    unblockedCount++;
                }
                continue;
            }

            var allSatisfied = true;
            foreach (var dependencyTemplateId in dependencyTemplateIds)
            {
                if (!tasksByTemplateId.TryGetValue(dependencyTemplateId, out var dependencyTasks)
                    || dependencyTasks.Count == 0)
                {
                    allSatisfied = false;
                    break;
                }

                if (dependencyTasks.Any(dep => dep.Status is not (RequestTaskStatus.Completed or RequestTaskStatus.Skipped)))
                {
                    allSatisfied = false;
                    break;
                }
            }

            if (!allSatisfied)
            {
                continue;
            }

            if (await UnblockTaskAsync(task, now, correlationId, ct))
            {
                unblockedCount++;
            }
        }

        return new WorkflowDependencyEvaluationResult(unblockedCount);
    }

    private async Task<bool> UnblockTaskAsync(
        RequestTask task,
        DateTimeOffset now,
        string correlationId,
        CancellationToken ct)
    {
        if (!task.IsBlocked)
        {
            return false;
        }

        task.IsBlocked = false;
        task.UnblockedAt = now;
        task.UpdatedAt = DateTime.UtcNow;

        await _requestTasks.UpdateAsync(task);

        await _domainEvents.PublishAsync(
            new RequestTaskUnblockedEvent(
                task.Id,
                task.RequestId,
                task.OrganizationId,
                now,
                correlationId),
            ct);

        return true;
    }

    private string GetCorrelationId()
    {
        return _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private static Guid? ParseGuid(string? value)
    {
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }
}
