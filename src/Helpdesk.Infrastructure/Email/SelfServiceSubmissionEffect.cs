using System.Diagnostics;
using Helpdesk.Application.Events;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Email;

public sealed record SelfServiceSubmissionEffect(string RequestId, string OrganizationId, string RecipientName,
    string CorrelationId, DateTimeOffset SubmittedAt);

public sealed class SelfServiceSubmissionEffectHandler(HelpdeskDbContext db, IRepository<Request> requests, IIngressEffectContext effects,
    MailboxOutboxStore outbox, IDomainEventPublisher events, ITicketNotificationService notifications,
    IWorkflowEngine workflow, ILogger<SelfServiceSubmissionEffectHandler> logger)
{
    public async Task ApplyAsync(MailboxOutboxEffect claim, CancellationToken ct)
    {
        var intent = MailboxOutboxStore.Deserialize<SelfServiceSubmissionEffect>(claim.Payload);
        using var organization = db.BindSelfServiceOrganization(intent.OrganizationId);
        var request = await requests.GetAsync(intent.RequestId);
        if (request is null || request.OrganizationId != intent.OrganizationId) return;

        var started = Stopwatch.GetTimestamp();
        using var capture = effects.Begin(claim.Id);
        effects.OrganizationId = intent.OrganizationId;
        if (!await db.RequestTasks.AnyAsync(task => task.RequestId == request.Id, ct))
            await workflow.RunAsync(request.Id, WorkflowRunReason.RequestCreated, ct);

        var form = await db.RequestForms.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.RequestFormId, ct);
        if (form is not null)
        {
            await events.PublishRequiredAsync(new SelfServiceRequestSubmittedEvent(request.Id, form.Id, form.ServiceId,
                request.TrackingId, request.CustomerId, intent.OrganizationId, intent.SubmittedAt, intent.CorrelationId), ct);
            _ = await notifications.SendSelfServiceRequestCreatedAsync(request, form,
                request.RequesterEmail ?? string.Empty, intent.RecipientName, request.CcRecipients, ct);
        }

        await outbox.FlushAsync(ct);
        logger.LogInformation("Prepared self-service submission notifications. RequestId={RequestId} DurationMs={DurationMs}",
            request.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
