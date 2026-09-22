using Helpdesk.Application.Incidents;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Shared.DTOs.Attachment;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Email;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Email;

public sealed class InboundEmailActionExecutor(
    HelpdeskDbContext db,
    IRequestSender requestSender,
    IRepository<Incident> incidentRepo,
    IRepository<TicketTimelineEvent> timelineRepo,
    ILogger<InboundEmailActionExecutor> logger,
    IInboundInlineImageResolver inlineImageResolver,
    ITicketAttachmentService attachmentService,
    RatelDeskIdentityDbContext? identityDb = null)
    : IInboundEmailActionExecutor
{
    public async Task<InboundEmailRuleProcessingResult> ExecuteAsync(
        InboundEmailRule rule,
        InboundEmailRuleActionConfig action,
        InboundEmailContext context,
        ForwardedEmailParseResult? forwarded,
        CancellationToken ct = default)
    {
        var actionKey = NormalizeActionKey(action);
        if (await HasProcessingLogAsync(context, rule.Id, actionKey, ct))
        {
            await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.Duplicate, null, "Duplicate message/rule/action.", ct);
            return new InboundEmailRuleProcessingResult(true, true, null);
        }

        await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.Matched, null, null, ct);

        var forwarder = await ResolveForwarderAsync(context.FromEmail, ct);
        var forwarderAccess = forwarder is null ? null : await InboundForwarderAuthorization.ResolveAsync(db, identityDb, forwarder, ct);
        if (!InboundForwarderAuthorization.CanForward(forwarderAccess))
        {
            await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.UnauthorizedSender, null, "Forwarding sender is not an authorized support user.", ct);
            return new InboundEmailRuleProcessingResult(true, true, null, "ForwarderUnauthorized");
        }

        if (forwarded?.HasConfidentRequester != true)
        {
            await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.ParserFailed, null, "Original forwarded requester could not be confidently extracted.", ct);
            return new InboundEmailRuleProcessingResult(true, true, null);
        }

        var tenantResult = await ResolveTenantAsync(rule, context, forwarderAccess!, ct);
        if (tenantResult.Ambiguous)
        {
            await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.TenantResolutionAmbiguous, null, "Tenant resolution was ambiguous.", ct);
            return new InboundEmailRuleProcessingResult(true, true, null);
        }

        if (tenantResult.Organization is null)
        {
            await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.Failed, null, "Tenant could not be resolved.", ct);
            return new InboundEmailRuleProcessingResult(true, true, null);
        }

        if (!forwarderAccess!.CanManageIncident(tenantResult.Organization.Id))
        {
            await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.UnauthorizedSender, null, "Forwarding sender cannot create incidents in the selected organization.", ct);
            return new InboundEmailRuleProcessingResult(true, true, null, "ForwarderUnauthorizedForTenant");
        }

        // A global rule can resolve an organization when the router could not. It must
        // acquire the same routing lock and enforce dedicated ingress before any writes.
        if (db.Database.CurrentTransaction is not null)
        {
            var ingressReason = await InboundTenantRouter.ValidateOrganizationIngressAsync(db,
                context.MailboxId, tenantResult.Organization.Id, ct);
            if (ingressReason is not null)
            {
                await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.Failed, null, ingressReason, ct);
                return new InboundEmailRuleProcessingResult(true, true, null, ingressReason);
            }
            if (context.SourceMessageKey?.StartsWith("ingress:", StringComparison.Ordinal) == true)
                db.RestrictIngressToOrganization(tenantResult.Organization.Id);
        }

        try
        {
            var requesterEmail = forwarded.OriginalFromEmail!.Trim().ToLowerInvariant();
            var customer = await db.Customers.SingleOrDefaultAsync(x => x.Email.ToLower() == requesterEmail, ct);
            if (customer is not null && (customer.OrganizationId != tenantResult.Organization.Id || customer.State != Helpdesk.Shared.Models.EntityState.Enabled))
            {
                await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.Failed, null, "Requester ownership conflict or blocked requester.", ct);
                return new InboundEmailRuleProcessingResult(true, true, null);
            }
            if (customer is null)
            {
                customer = new Customer { Email = requesterEmail, Name = forwarded.OriginalFromDisplayName ?? requesterEmail, OrganizationId = tenantResult.Organization.Id };
                db.Customers.Add(customer);
                await db.SaveChangesAsync(ct);
            }

            var incident = await requestSender.Send(new CreateIncidentCommand(
                string.IsNullOrWhiteSpace(forwarded.OriginalSubject) ? context.Subject : forwarded.OriginalSubject!,
                forwarded.OriginalBodyHtml ?? forwarded.OriginalBodyText ?? context.HtmlBody,
                null,
                customer.Id,
                tenantResult.Organization.Id,
                null,
                null,
                null,
                null,
                customer.Email,
                [],
                null), ct);

            incident.State = TicketState.New;
            incident.AssignedToId = null;
            var resolution = await inlineImageResolver.ResolveAsync(
                incident.Id, forwarded.OriginalBodyHtml ?? context.HtmlBody, context.Attachments,
                context.GraphMessageId, context.InternetMessageId, ct);
            incident.Description = forwarded.OriginalBodyHtml is null && forwarded.OriginalBodyText is not null
                ? forwarded.OriginalBodyText
                : resolution.Html;
            incident.OriginalEmailHtml = resolution.Html;
            incident.OriginalEmailText = forwarded.OriginalBodyText ?? context.TextBody;
            incident.EmailFrom = forwarded.OriginalFromEmail;
            incident.EmailReceivedUtc = context.ReceivedUtc;
            await incidentRepo.UpdateAsync(incident);

            var uploads = context.Attachments
                .Where((attachment, index) => attachment.ContentBytes is not null && !resolution.ConsumedAttachmentIndexes.Contains(index))
                .Select(attachment => new AttachmentUpload(attachment.Name, attachment.ContentType, attachment.ContentBytes!))
                .ToList();
            if (uploads.Count > 0)
            {
                await attachmentService.SaveAsync(incident.Id, uploads, null, ct);
            }

            await timelineRepo.CreateAsync(new TicketTimelineEvent
            {
                TicketId = incident.Id,
                EventType = TimelineEventType.InternalNote,
                CreatedByUserId = forwarder.Id,
                CreatedByUserName = forwarder.Email,
                MessageText = $"Forwarded email processed by inbound rule. Forwarder: {forwarder.Email}. Source Message-ID: {context.InternetMessageId}."
            });

            await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.Succeeded, incident.Id, null, ct);
            return new InboundEmailRuleProcessingResult(true, rule.StopProcessing, incident);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Inbound forwarded email action failed ({FailureType}).", ex.GetType().Name);
            if (db.Database.CurrentTransaction is not null)
                throw; // The receipt coordinator must roll back partial business effects.
            await TryLogAsync(context, rule, actionKey, true, InboundEmailProcessingStatus.Failed, null, ex.GetType().Name, ct);
            return new InboundEmailRuleProcessingResult(true, true, null);
        }
    }

    internal async Task<User?> ResolveForwarderAsync(string email, CancellationToken ct) =>
        await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Email.ToLower() == email.Trim().ToLowerInvariant(), ct);

    private async Task<TenantResolutionResult> ResolveTenantAsync(
        InboundEmailRule rule,
        InboundEmailContext context,
        CurrentUserAccessProfile access,
        CancellationToken ct)
    {
        var candidates = new List<Organization>();
        if (!string.IsNullOrWhiteSpace(context.ForwardedRequesterTenantId))
        {
            await AddOrganizationAsync(candidates, context.ForwardedRequesterTenantId, ct);
            if (rule.ScopeType == InboundEmailRuleScopeType.Tenant &&
                !string.Equals(rule.TenantId, context.ForwardedRequesterTenantId, StringComparison.OrdinalIgnoreCase))
                return new TenantResolutionResult(null, true);
            return new TenantResolutionResult(candidates.SingleOrDefault(), false);
        }
        if (!string.IsNullOrWhiteSpace(context.MailboxTenantId))
        {
            await AddOrganizationAsync(candidates, context.MailboxTenantId, ct);
            return new TenantResolutionResult(candidates.SingleOrDefault(), false);
        }

        if (rule.ScopeType == InboundEmailRuleScopeType.Tenant && !string.IsNullOrWhiteSpace(rule.TenantId))
        {
            await AddOrganizationAsync(candidates, rule.TenantId, ct);
        }

        var managed = await ResolveManagedOrganizationsAsync(access, ct);
        if (managed.Count == 1)
        {
            AddDistinct(candidates, managed[0]);
        }
        else if (candidates.Count == 0 && managed.Count > 1)
        {
            return new TenantResolutionResult(null, true);
        }


        return candidates.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            ? new TenantResolutionResult(null, true)
            : new TenantResolutionResult(candidates.FirstOrDefault(), false);
    }

    private async Task<List<Organization>> ResolveManagedOrganizationsAsync(CurrentUserAccessProfile access, CancellationToken ct)
    {
        var organizationIds = access.OrganizationIdsForAny(HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager);
        return await db.Organizations.AsNoTracking()
            .Where(organization => organization.IsEnabled && (access.IsHelpdeskAdmin || organizationIds.Contains(organization.Id)))
            .ToListAsync(ct);
    }

    private async Task AddOrganizationAsync(List<Organization> organizations, string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var organization = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsEnabled, ct);
        if (organization is not null)
        {
            AddDistinct(organizations, organization);
        }
    }

    private static void AddDistinct(List<Organization> organizations, Organization organization)
    {
        if (!organizations.Any(x => string.Equals(x.Id, organization.Id, StringComparison.OrdinalIgnoreCase)))
        {
            organizations.Add(organization);
        }
    }

    private async Task<bool> HasProcessingLogAsync(InboundEmailContext context, string ruleId, string actionKey, CancellationToken ct)
    {
        var mailboxKey = MailboxKey(context.MailboxId);
        var messageKey = InboundEmailRuleProcessor.MessageKey(context);
        return await db.InboundEmailProcessingLogs.AsNoTracking().AnyAsync(x =>
            x.MessageId == messageKey &&
            x.MailboxKey == mailboxKey &&
            x.RuleId == ruleId &&
            x.ActionKey == actionKey &&
            (x.Status == InboundEmailProcessingStatus.Succeeded || x.Status == InboundEmailProcessingStatus.Duplicate),
            ct);
    }

    private async Task TryLogAsync(
        InboundEmailContext context,
        InboundEmailRule rule,
        string actionKey,
        bool matched,
        InboundEmailProcessingStatus status,
        string? ticketId,
        string? error,
        CancellationToken ct)
    {
        db.InboundEmailProcessingLogs.Add(new InboundEmailProcessingLog
        {
            MessageId = InboundEmailRuleProcessor.MessageKey(context),
            MailboxId = context.MailboxId,
            MailboxKey = MailboxKey(context.MailboxId),
            TenantId = rule.TenantId ?? context.MailboxTenantId,
            RuleId = rule.Id,
            ActionKey = actionKey,
            Matched = matched,
            Status = status,
            TicketId = ticketId,
            Error = error
        });
        await db.SaveChangesAsync(ct);
    }

    private static string NormalizeActionKey(InboundEmailRuleActionConfig action) =>
        string.IsNullOrWhiteSpace(action.ActionKey)
            ? action.Type.ToString()
            : action.ActionKey.Trim();

    private static string MailboxKey(Guid? mailboxId) => mailboxId?.ToString("D") ?? "default";

    private sealed record TenantResolutionResult(Organization? Organization, bool Ambiguous);
}
