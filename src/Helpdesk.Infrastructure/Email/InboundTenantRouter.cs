using System.Text.RegularExpressions;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

public sealed record InboundRoute(string? OrganizationId, string? Reason, string RequesterEmail, string RequesterName, string Domain,
    string? ReferencedTicketId = null);

public sealed class InboundTenantRouter(HelpdeskDbContext db, IForwardedEmailParser parser, RatelDeskIdentityDbContext identityDb)
{
    private static readonly Regex Reference = new(@"\b(?:INC|REQ)-[A-Z0-9-]+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex MessageReference = new(@"<([^<>\s]+)>|([^<>\s]+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<InboundRoute> ResolveAsync(EmailInboxSettings mailbox, InboundEmailContext message, CancellationToken ct)
    {
        var sender = message.FromEmail.Trim().ToLowerInvariant();
        var name = message.FromDisplayName ?? sender;
        var forwarder = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email.ToLower() == sender, ct);
        var access = forwarder is null ? null : await InboundForwarderAuthorization.ResolveAsync(db, identityDb, forwarder, ct);
        var forwarded = parser.Parse(message.HtmlBody, message.TextBody);
        if (InboundForwarderAuthorization.CanForward(access) && forwarded.HasConfidentRequester)
        {
            sender = forwarded.OriginalFromEmail!.Trim().ToLowerInvariant();
            name = forwarded.OriginalFromDisplayName ?? sender;
        }
        var domain = sender.Contains('@') ? sender.Split('@')[^1] : string.Empty;
        InboundRoute Hold(string reason, string? organizationId = null) => new(organizationId, reason, sender, name, domain);
        if (domain.Length == 0) return Hold("InvalidSender");
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Email.ToLower() == sender, ct);
        if (customer?.State == Helpdesk.Shared.Models.EntityState.Blocked) return Hold("BlockedRequester", customer.OrganizationId);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        if (mailbox.OrganizationId is not null) candidates.Add(mailbox.OrganizationId);
        if (customer is not null) candidates.Add(customer.OrganizationId);
        if (mailbox.OrganizationId is null)
        {
            var domains = await db.Organizations.AsNoTracking()
                .Where(x => x.DnsName != null && x.DnsName.ToLower() == domain || x.Name.ToLower() == domain)
                .Select(x => x.Id).ToListAsync(ct);
            candidates.UnionWith(domains);
        }
        var rawReferences = message.References.Concat(string.IsNullOrWhiteSpace(message.InReplyTo) ? [] : new[] { message.InReplyTo })
            .Concat(message.RepeatedHeaders.Where(x => x.Key.Equals("References", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase))
                .SelectMany(x => x.Value))
            .Concat(message.Headers.Where(x => x.Key.Equals("References", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)).Distinct(StringComparer.Ordinal).Take(129).ToArray();
        if (rawReferences.Length > 128 || rawReferences.Any(x => x.Length > 8192)) return Hold("ReferenceLimitExceeded", mailbox.OrganizationId);
        var messageIds = rawReferences.SelectMany(value => MessageReference.Matches(value)
                .SelectMany(match => MessageIdCandidates(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)))
            .Distinct(StringComparer.Ordinal).Take(257).ToArray();
        if (messageIds.Length > 256) return Hold("ReferenceLimitExceeded", mailbox.OrganizationId);
        var references = new[] { message.Subject }.Concat(rawReferences).SelectMany(value => Reference.Matches(value)
            .Select(x => x.Value.ToUpperInvariant())).Distinct().Take(129).ToArray();
        if (references.Length > 128) return Hold("ReferenceLimitExceeded", mailbox.OrganizationId);
        var receiptTickets = await db.Set<InboundMessageReceipt>().AsNoTracking()
            .Where(x => x.Outcome == InboundReceiptOutcome.Succeeded && x.TicketId != null && x.InternetMessageId != null && messageIds.Contains(x.InternetMessageId))
            .Select(x => x.TicketId!).Distinct().Take(129).ToListAsync(ct);
        var legacyTickets = await db.TicketTimelineEvents.AsNoTracking()
            .Where(x => x.EventType == TimelineEventType.CustomerReply && messageIds.Contains(x.CreatedByUserId))
            .Select(x => x.TicketId).Distinct().Take(129).ToListAsync(ct);
        var threadTickets = receiptTickets.Concat(legacyTickets).Distinct().ToArray();
        if (threadTickets.Length > 128) return Hold("AmbiguousTicketReference", mailbox.OrganizationId);
        var tickets = await db.Tickets.IgnoreQueryFilters().AsNoTracking()
            .Where(x => references.Contains(x.TrackingId.ToUpper()) || threadTickets.Contains(x.Id))
            .Select(x => new { x.Id, x.OrganizationId, x.TrackingId, x.RequesterEmail, x.CcRecipients }).Take(129).ToListAsync(ct);
        foreach (var ticket in tickets)
        {
            if (mailbox.OrganizationId is not null && mailbox.OrganizationId != ticket.OrganizationId)
                return Hold("CrossTenantReference", mailbox.OrganizationId);
            candidates.Add(ticket.OrganizationId);
            if (!string.Equals(ticket.RequesterEmail, message.FromEmail, StringComparison.OrdinalIgnoreCase) &&
                !ticket.CcRecipients.Contains(message.FromEmail, StringComparer.OrdinalIgnoreCase) &&
                access?.CanManageIncident(ticket.OrganizationId) != true)
                return Hold("UnauthorizedTicketReference", ticket.OrganizationId);
        }
        if (tickets.Count > 1) return Hold("AmbiguousTicketReference", mailbox.OrganizationId);
        if (candidates.Count > 1) return Hold(customer is not null && mailbox.OrganizationId is not null ? "RequesterOwnershipConflict" : "AmbiguousOrganization", mailbox.OrganizationId);
        var target = candidates.SingleOrDefault();
        if (target is not null)
        {
            var ingressReason = await ValidateOrganizationIngressAsync(db, mailbox.Id, target, ct);
            if (ingressReason is not null) return Hold(ingressReason, target);
        }
        return new(target, null, sender, name, domain, tickets.SingleOrDefault()?.Id);
    }

    internal static string[] MessageIdCandidates(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var normalized = value.Trim().Trim('<', '>');
        return normalized.Length == 0 ? [] : [normalized, $"<{normalized}>"];
    }

    public static async Task LockOrganizationAsync(HelpdeskDbContext db, string? organizationId, CancellationToken ct)
    {
        if (organizationId is null || !db.Database.IsRelational()) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Organization ingress locking requires a transaction.");
        // Use a non-key column to avoid escalating PostgreSQL foreign-key lock modes.
        await db.Organizations.Where(x => x.Id == organizationId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Name, x => x.Name), ct);
    }

    public static async Task<string?> ValidateOrganizationIngressAsync(HelpdeskDbContext db, Guid? mailboxId,
        string organizationId, CancellationToken ct)
    {
        await LockOrganizationAsync(db, organizationId, ct);
        if (!await db.Organizations.AnyAsync(x => x.Id == organizationId && x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct))
            return "OrganizationUnavailable";
        if (mailboxId is not { } id) return null; // Legacy callers have no assigned source.
        var mailbox = await db.EmailInboxSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (mailbox is null) return "MailboxUnavailable";
        if (mailbox.Scope == MailboxScope.Organization && mailbox.OrganizationId != organizationId)
            return "CrossTenantReference";
        if (mailbox.Scope == MailboxScope.Global && await db.EmailInboxSettings.AnyAsync(x => !x.Archived && x.OrganizationId == organizationId, ct))
            return "TenantUsesDedicatedMailbox";
        return null;
    }

    public async Task<Customer> GetRequesterAsync(InboundRoute route, CancellationToken ct)
    {
        if (route.Reason is not null) throw new InvalidOperationException("Cannot provision a held message.");
        var organizationId = route.OrganizationId;
        if (organizationId is null)
        {
            var organization = new Organization { Name = route.Domain, DnsName = route.Domain };
            db.Organizations.Add(organization);
            organizationId = organization.Id;
        }
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Email.ToLower() == route.RequesterEmail, ct);
        if (customer is not null)
        {
            if (customer.OrganizationId != organizationId) throw new InvalidOperationException("RequesterOwnershipConflict");
            return customer;
        }
        customer = new Customer { Email = route.RequesterEmail, Name = route.RequesterName, OrganizationId = organizationId };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer;
    }
}
