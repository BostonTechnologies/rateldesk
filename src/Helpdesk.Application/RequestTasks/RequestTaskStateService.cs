using Helpdesk.Application.Events;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Application.RequestTasks;

public sealed class RequestTaskStateService(
    IRepository<Request> requests,
    IRepository<RequestTask> requestTasks,
    IDomainEventPublisher domainEvents,
    ICorrelationContext correlationContext,
    ITicketNotificationService? ticketNotificationService = null,
    IRequestTaskReadStore? requestTaskReadStore = null) : IRequestTaskStateService
{
    private readonly IRepository<Request> _requests = requests;
    private readonly IRepository<RequestTask> _requestTasks = requestTasks;
    private readonly IDomainEventPublisher _domainEvents = domainEvents;
    private readonly ICorrelationContext _correlationContext = correlationContext;
    private readonly ITicketNotificationService? _ticketNotificationService = ticketNotificationService;

    public async Task<bool> EvaluateParentRequestState(string requestId, CancellationToken cancellationToken = default)
    {
        var request = await _requests.GetAsync(requestId);
        if (request is null)
        {
            return false;
        }

        if (string.Equals(request.WorkflowStatus, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.WorkflowStatus, "Blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.WorkflowStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            if (request.State == TicketState.OnHold)
            {
                return false;
            }

            var blockedPreviousState = request.State;
            request.State = TicketState.OnHold;
            request.UpdatedAt = DateTime.UtcNow;
            request.ClosedAt = null;

            await _requests.UpdateAsync(request);

            await _domainEvents.PublishAsync(
                new RequestStateChangedEvent(
                    request.Id,
                    blockedPreviousState,
                    request.State,
                    request.OrganizationId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId()),
                cancellationToken);

            return true;
        }

        var tasks = await RequestTaskQueries.ForRequestAsync(_requestTasks, requestTaskReadStore, requestId, cancellationToken);

        var needsReconciliation = tasks.Any(task => task.Type == RequestTaskType.Automation
            && AutomationTaskStatuses.RequiresManualReconciliation(task.LastAutomationStatus));
        var nextState = needsReconciliation ? TicketState.OnHold : ResolveState(tasks);
        var nextWorkflowStatus = needsReconciliation ? "NeedsReconciliation" : nextState switch
        {
            TicketState.Resolved => "Completed",
            TicketState.PendingApproval => "PendingApproval",
            TicketState.InProgress => "InProgress",
            TicketState.New when tasks.Count > 0 => "InProgress",
            _ => null
        };
        var timeoutOwnedHold = string.Equals(request.WorkflowStatus, "NeedsReconciliation", StringComparison.Ordinal)
            && request.WorkflowBlockReason == AutomationTaskStatuses.ReconciliationHoldReason;
        // Preserve an independently recorded hold. Only the status installed for
        // uncertainty is ours to remove when this exact attempt becomes known.
        if (!timeoutOwnedHold && request.State == TicketState.OnHold && !string.IsNullOrWhiteSpace(request.WorkflowBlockReason))
            return false;
        if (request.State == nextState && request.WorkflowStatus == nextWorkflowStatus
            && (!timeoutOwnedHold || needsReconciliation))
            return false;

        var previousState = request.State;
        request.State = nextState;
        request.UpdatedAt = DateTime.UtcNow;
        request.ClosedAt = nextState == TicketState.Resolved ? DateTimeOffset.UtcNow : null;
        request.WorkflowStatus = nextWorkflowStatus;
        request.WorkflowBlockReason = needsReconciliation
            ? AutomationTaskStatuses.ReconciliationHoldReason : null;
        request.WorkflowUpdatedAt = DateTimeOffset.UtcNow;

        await _requests.UpdateAsync(request);

        await _domainEvents.PublishAsync(
            new RequestStateChangedEvent(
                request.Id,
                previousState,
                nextState,
                request.OrganizationId,
                DateTimeOffset.UtcNow,
                GetCorrelationId()),
            cancellationToken);

        if (previousState != TicketState.Resolved &&
            nextState == TicketState.Resolved &&
            !string.IsNullOrWhiteSpace(request.RequestFormId))
        {
            await SendSelfServiceCompletionAsync(request, cancellationToken);
        }

        return true;
    }

    private static TicketState ResolveState(List<RequestTask> tasks)
    {
        if (tasks.Any(x => x.Status == RequestTaskStatus.Failed))
        {
            return TicketState.OnHold;
        }

        if (tasks.Any(x => x.Status == RequestTaskStatus.Cancelled))
        {
            return TicketState.OnHold;
        }

        if (tasks.Any(x => x.Status == RequestTaskStatus.PendingApproval))
        {
            return TicketState.PendingApproval;
        }

        if (tasks.Count > 0
            && tasks.All(x => x.Status is RequestTaskStatus.Completed or RequestTaskStatus.Skipped))
        {
            return TicketState.Resolved;
        }

        if (tasks.Any(x => x.Status == RequestTaskStatus.InProgress))
        {
            return TicketState.InProgress;
        }

        return TicketState.New;
    }

    private string GetCorrelationId()
    {
        return _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private async Task SendSelfServiceCompletionAsync(Request request, CancellationToken cancellationToken)
    {
        if (_ticketNotificationService is null || string.IsNullOrWhiteSpace(request.RequesterEmail))
        {
            return;
        }

        await _ticketNotificationService.SendSelfServiceRequestCompletedAsync(
            request,
            request.RequesterEmail,
            request.RequesterEmail,
            request.CcRecipients,
            cancellationToken);
    }
}
