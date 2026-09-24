using Helpdesk.Application.Notifications;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Dispatches committed effects in fresh scopes, with bounded, fenced replica claims.</summary>
public sealed class MailboxOutboxDispatcher(IServiceScopeFactory scopes, ILogger<MailboxOutboxDispatcher> logger)
    : BackgroundService
{
    private readonly string owner = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("Mailbox outbox cycle failed. ErrorCode={ErrorCode}", error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> DispatchBatchAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Guid> candidates;
        await using (var discovery = scopes.CreateAsyncScope())
            candidates = await discovery.ServiceProvider.GetRequiredService<MailboxOutboxStore>().GetCandidatesAsync(16, ct);
        var completed = 0;
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<MailboxOutboxStore>();
            var claim = await store.TryClaimAsync(id, owner, ct);
            if (claim is null)
                continue;
            bool succeeded;
            bool requiresReview = false;
            string? errorCode = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var result = await DispatchAsync(claim, services, timeout.Token);
                succeeded = result.Status is "Accepted by provider" or "Suppressed";
                requiresReview = result.Status is "Needs configuration" or "Needs review" or "Outcome unknown";
                errorCode = result.Status == "Suppressed" ? "Suppressed" : result.ErrorCode;
                if (!succeeded && errorCode is null) errorCode = "DeliveryRejected";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Leave the claim to expire. Remote delivery may already have succeeded.
                throw;
            }
            catch (Exception error)
            {
                succeeded = false;
                requiresReview = claim.Kind == MailboxEffectKind.Email;
                errorCode = requiresReview ? "DispatchOutcomeUnknown"
                    : error is OperationCanceledException ? "DispatchTimedOut" : error.GetType().Name;
            }

            if (await store.CompleteAsync(claim, succeeded, errorCode, ct, requiresReview) && succeeded)
                completed++;
        }
        return completed;
    }

    private static async Task<MailboxSubmissionResult> DispatchAsync(MailboxOutboxEffect effect, IServiceProvider services, CancellationToken ct)
    {
        if (services.GetRequiredService<IIngressEffectContext>().IsActive)
            throw new InvalidOperationException("Outbox dispatch cannot run inside an ingress business scope.");
        switch (effect.Kind)
        {
            case MailboxEffectKind.Email:
                var email = MailboxOutboxStore.Deserialize<IngressEmailEffect>(effect.Payload);
                if (email.MailboxId is null)
                    return new("Needs review", email.SenderBindingError ?? "LegacySenderBindingMissing");
                return await services.GetRequiredService<MailboxEmailService>().SendPinnedAsync(email.MailboxId.Value,
                    email.OrganizationId, email.TicketId, email.Recipients, email.Cc,
                    email.Subject, email.Html, email.Attachments, email.ReplyTo, effect.Id, ct,
                    email.MailboxConfigurationVersion, email.OutgoingConfigurationVersion, email.Bcc);
            case MailboxEffectKind.Notification:
                services.GetRequiredService<INotificationEventBus>().Publish(
                    MailboxOutboxStore.Deserialize<NotificationDto>(effect.Payload));
                return new("Accepted by provider");
            case MailboxEffectKind.Timeline:
                var timeline = MailboxOutboxStore.Deserialize<TicketTimelineEventDto>(effect.Payload);
                // Another replica may have delivered an email before its queued Pending
                // publication. Publish the latest durable status instead of regressing the UI.
                if (timeline.EventType == TimelineEventType.EmailDelivery)
                {
                    var current = await services.GetRequiredService<HelpdeskDbContext>().TicketTimelineEvents
                        .AsNoTracking().SingleOrDefaultAsync(x => x.Id == timeline.Id, ct);
                    if (current is not null)
                    {
                        timeline.EmailStatus = current.EmailStatus;
                        timeline.MessageText = current.MessageText;
                        timeline.RetryCount = current.RetryCount;
                        timeline.IsRetryable = current.IsRetryable;
                    }
                }
                await services.GetRequiredService<ITimelineEventBus>().PublishAsync(timeline);
                return new("Accepted by provider");
            default:
                throw new InvalidOperationException("Unsupported durable ingress effect kind.");
        }
    }
}
