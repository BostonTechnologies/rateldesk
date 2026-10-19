using Dodo.Primitives;
using Helpdesk.Application.Events;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Application.RequestTasks;

public sealed class RequestTaskGenerationService(
    IRepository<RequestForm> requestForms,
    IRepository<RequestTask> requestTasks,
    IRequestFormSchemaParser schemaParser,
    IDomainEventPublisher domainEvents,
    ICorrelationContext correlationContext) : IRequestTaskGenerationService
{
    private readonly IRepository<RequestForm> _requestForms = requestForms;
    private readonly IRepository<RequestTask> _requestTasks = requestTasks;
    private readonly IRequestFormSchemaParser _schemaParser = schemaParser;
    private readonly IDomainEventPublisher _domainEvents = domainEvents;
    private readonly ICorrelationContext _correlationContext = correlationContext;

    public async Task<int> GenerateForRequestAsync(Request request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RequestFormId))
        {
            return 0;
        }

        var form = await _requestForms.GetAsync(request.RequestFormId);
        if (form is null)
        {
            return 0;
        }

        var schema = _schemaParser.Parse(form.JsonSchema);
        var templates = schema.Tasks
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Order)
            .ToList();
        var knownTemplateIds = templates
            .Where(x => x.Id != Guid.Empty)
            .Select(x => x.Id)
            .ToHashSet();
        var correlationId = GetCorrelationId();

        foreach (var template in templates)
        {
            var normalizedTemplateId = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id;
            var dependsOnIds = RequestTaskApprovalGate.GetEffectiveDependencies(template, templates)
                .Where(x => x != Guid.Empty && knownTemplateIds.Contains(x))
                .Distinct()
                .Select(x => x.ToString("D"))
                .ToList();
            var startsBlocked = dependsOnIds.Count > 0;
            var normalizedFailurePolicy = NormalizeFailurePolicy(template.FailurePolicy);
            var retryDelayMinutes = template.MaxRetries.HasValue
                ? template.RetryDelayMinutes ?? 0
                : template.RetryDelayMinutes;
            var taskType = ResolveType(template.Type);
            int? expectedRuntimeMinutes = template.ExpectedRuntimeMinutes;
            int? graceRuntimeMinutes = template.GraceRuntimeMinutes;
            if (taskType == RequestTaskType.Automation)
            {
                expectedRuntimeMinutes ??= 30;
                graceRuntimeMinutes ??= 10;
            }
            var expectedRuntimeSeconds = MinutesToSeconds(expectedRuntimeMinutes);
            var graceSeconds = MinutesToSeconds(graceRuntimeMinutes);
            int? hardTimeoutSeconds = expectedRuntimeSeconds.HasValue
                ? Math.Max(0, expectedRuntimeSeconds.Value) + Math.Max(0, graceSeconds ?? 0)
                : null;

            var task = new RequestTask
            {
                Id = Uuid.CreateVersion7().ToString(),
                RequestId = request.Id,
                TemplateId = normalizedTemplateId.ToString("D"),
                Title = template.Name,
                Description = template.Description ?? string.Empty,
                Type = taskType,
                AutoStart = template.AutoStart,
                Status = RequestTaskStatus.Pending,
                State = TicketState.New,
                TaskSlaMinutes = template.TaskSlaMinutes,
                EscalateAfterMinutes = template.EscalateAfterMinutes,
                EscalationUserId = template.EscalationUserId,
                EscalationRole = template.EscalationRole,
                Escalated = false,
                SlaBreached = false,
                IsBlocked = startsBlocked,
                ConditionExpression = template.ConditionExpression,
                IsCritical = template.IsCritical,
                FailurePolicy = normalizedFailurePolicy,
                MaxRetries = template.MaxRetries,
                RetryDelayMinutes = retryDelayMinutes,
                RetryCount = 0,
                NextRetryAt = null,
                FailureReason = null,
                AutomationBindingId = null,
                AssignedToId = template.DefaultAssigneeId,
                OrchestrationRequestDefinitionId = null,
                OrchestrationJobDefinitionId = null,
                OrchestrationExternalRequestId = null,
                OrchestrationExternalRunId = null,
                LastAutomationStatus = null,
                LastAutomationUpdatedAt = null,
                ExpectedRuntimeSeconds = expectedRuntimeSeconds,
                GraceSeconds = graceSeconds,
                HardTimeoutSeconds = hardTimeoutSeconds,
                TimeoutIncidentId = null,
                OrchestratorExecutionId = null,
                Order = template.Order,
                CustomerId = request.CustomerId,
                OrganizationId = request.OrganizationId,
                ServiceId = request.ServiceId,
                Priority = request.Priority
            };

            await _requestTasks.CreateAsync(task);

            if (startsBlocked)
            {
                await _domainEvents.PublishAsync(
                    new RequestTaskBlockedEvent(
                        task.Id,
                        request.Id,
                        dependsOnIds,
                        request.OrganizationId,
                        DateTimeOffset.UtcNow,
                        correlationId),
                    cancellationToken);
            }
        }

        await _domainEvents.PublishAsync(
            new RequestTasksGeneratedEvent(
                request.Id,
                templates.Count,
                request.OrganizationId,
                DateTimeOffset.UtcNow,
                correlationId),
            cancellationToken);

        return templates.Count;
    }

    private string GetCorrelationId()
    {
        return _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private static RequestTaskType ResolveType(string? type)
    {
        if (string.Equals(type, "automation", StringComparison.OrdinalIgnoreCase))
        {
            return RequestTaskType.Automation;
        }

        if (string.Equals(type, "approval", StringComparison.OrdinalIgnoreCase))
        {
            return RequestTaskType.Approval;
        }

        return RequestTaskType.Manual;
    }

    private static int? MinutesToSeconds(int? minutes)
    {
        if (!minutes.HasValue)
        {
            return null;
        }

        return Math.Max(0, minutes.Value) * 60;
    }

    private static string? NormalizeFailurePolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return null;
        }

        var normalized = policy.Trim();
        if (string.Equals(normalized, "FailRequest", StringComparison.OrdinalIgnoreCase))
        {
            return "FailRequest";
        }

        if (string.Equals(normalized, "BlockRequest", StringComparison.OrdinalIgnoreCase))
        {
            return "BlockRequest";
        }

        if (string.Equals(normalized, "Continue", StringComparison.OrdinalIgnoreCase))
        {
            return "Continue";
        }

        return null;
    }
}
