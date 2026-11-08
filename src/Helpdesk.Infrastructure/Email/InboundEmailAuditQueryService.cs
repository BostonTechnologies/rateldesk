using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Filters the complete independent audit history before counting and paging.</summary>
public sealed class InboundEmailAuditQueryService(HelpdeskDbContext db)
{
    public async Task<InboundEmailAuditPage> SearchAsync(InboundEmailAuditQuery input,
        CurrentUserAccessProfile profile, CancellationToken ct = default)
    {
        if (input.Page is < 0 or > 1000000 || input.PageSize is not (25 or 50 or 100) || input.From > input.To ||
            input.Status is { } status && !Enum.IsDefined(status) ||
            new[] { input.Search, input.MessageId, input.TicketId, input.TenantId, input.RuleId }.Any(x => x?.Length > 200))
            throw new ArgumentException("Use valid filters, a date range and 25, 50 or 100 rows per page.");

        var query = db.InboundEmailProcessingLogs.AsNoTracking();
        if (input.MailboxId is { } mailbox) query = query.Where(x => x.MailboxId == mailbox);
        if (!string.IsNullOrWhiteSpace(input.MessageId)) query = query.Where(x => x.MessageId == input.MessageId);
        if (!string.IsNullOrWhiteSpace(input.TicketId)) query = query.Where(x => x.TicketId == input.TicketId);
        if (!string.IsNullOrWhiteSpace(input.TenantId)) query = query.Where(x => x.TenantId == input.TenantId);
        if (!string.IsNullOrWhiteSpace(input.RuleId)) query = query.Where(x => x.RuleId == input.RuleId);
        if (input.Status is { } outcome) query = query.Where(x => x.Status == outcome);
        if (input.Search?.Trim().ToLowerInvariant() is { Length: > 0 } search)
            query = query.Where(x => x.ActionKey.ToLower().Contains(search) || x.MessageId.ToLower().Contains(search) ||
                x.Error != null && x.Error.ToLower().Contains(search) ||
                db.InboundEmailRules.Any(r => r.Id == x.RuleId && r.Name.ToLower().Contains(search)) ||
                db.Organizations.Any(o => o.Id == x.TenantId && o.Name.ToLower().Contains(search)) ||
                db.EmailInboxSettings.Any(m => m.Id == x.MailboxId &&
                    (m.DisplayName.ToLower().Contains(search) || m.MailboxAddress.ToLower().Contains(search))) ||
                db.Tickets.Any(t => t.Id == x.TicketId && t.TrackingId.ToLower().Contains(search)));
        if (input.From is { } from)
            query = db.Database.IsSqlite()
                ? query.Where(x => EF.Property<long>(x, "CreatedAtUtcSortTicks") >= from.UtcTicks)
                : query.Where(x => x.CreatedAtUtc >= from.ToUniversalTime());
        if (input.To is { } to)
            query = db.Database.IsSqlite()
                ? query.Where(x => EF.Property<long>(x, "CreatedAtUtcSortTicks") <= to.UtcTicks)
                : query.Where(x => x.CreatedAtUtc <= to.ToUniversalTime());
        var total = await query.CountAsync(ct);
        var page = total == 0 ? 0 : Math.Min(input.Page, (total - 1) / input.PageSize);
        var ordered = input.Sort switch
        {
            "created" => query.OrderByUtc(db, x => x.CreatedAtUtc, input.Descending),
            "status" => input.Descending ? query.OrderByDescending(x => x.Status) : query.OrderBy(x => x.Status),
            "action" => input.Descending ? query.OrderByDescending(x => x.ActionKey) : query.OrderBy(x => x.ActionKey),
            _ => throw new ArgumentException("Sort must be created, status or action.")
        };
        var rows = await ordered.ThenBy(x => x.Id).Skip(page * input.PageSize).Take(input.PageSize).ToListAsync(ct);
        var ruleIds = rows.Select(x => x.RuleId).Distinct().ToArray();
        var orgIds = rows.Select(x => x.TenantId).Distinct().ToArray();
        var mailboxIds = rows.Select(x => x.MailboxId).Distinct().ToArray();
        var ticketIds = rows.Select(x => x.TicketId).Distinct().ToArray();
        var rules = await db.InboundEmailRules.AsNoTracking().Where(x => ruleIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var organizations = await db.Organizations.AsNoTracking().Where(x => orgIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var mailboxes = await db.EmailInboxSettings.AsNoTracking().Where(x => mailboxIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName + " · " + x.MailboxAddress, ct);
        var tickets = await db.Tickets.AsNoTracking().Where(x => ticketIds.Contains(x.Id)).ToListAsync(ct);
        var authorizedTickets = tickets.Where(t => t switch
        {
            Incident => profile.CanViewIncident(t.OrganizationId, t.CustomerId, t.RequesterEmail),
            Request => profile.CanViewRequest(t.OrganizationId, t.CustomerId, t.RequesterEmail),
            Change => profile.CanViewChange(t.OrganizationId, t.CustomerId, t.RequesterEmail),
            _ => false
        }).ToDictionary(x => x.Id);
        return new(rows.Select(x =>
        {
            authorizedTickets.TryGetValue(x.TicketId ?? "", out var ticket);
            var route = ticket switch { Incident => "incidents", Request => "requests", Change => "changes", _ => null };
            return new InboundEmailAuditRow(new(x.Id, x.MessageId, x.MailboxId, x.TenantId, x.RuleId,
                x.ActionKey, x.Matched, x.Status, x.TicketId, x.Error, x.CreatedAtUtc),
                x.RuleId is null ? "No rule" : rules.GetValueOrDefault(x.RuleId, "Unknown rule"),
                x.TenantId is null ? "Instance" : organizations.GetValueOrDefault(x.TenantId, "Unknown organization"),
                x.MailboxId is { } id ? mailboxes.GetValueOrDefault(id, "Unknown mailbox") : "Default mailbox",
                ticket?.TrackingId, route is null ? null : $"/{route}/{Uri.EscapeDataString(ticket!.Id)}");
        }).ToArray(), total, page, input.PageSize);
    }
}
