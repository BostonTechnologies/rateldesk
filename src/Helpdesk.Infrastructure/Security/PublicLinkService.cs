using System.Net;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.PublicAccess;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.Security;

public sealed class PublicLinkService(HelpdeskDbContext db, IPublicTicketLinkSigner legacySigner, IDataProtectionProvider keys,
    IConfiguration options, TimeProvider clock) : IPublicLinkIssuer
{
    private readonly ITimeLimitedDataProtector _grants = keys.CreateProtector("RatelDesk.PublicLinkGrant.v1").ToTimeLimitedDataProtector();

    public async Task<string> IssueAsync(Ticket ticket, string recipient, PublicLinkPurpose purpose, string? applicationUrl,
        CancellationToken cancellationToken, string? taskId = null)
    {
        var normalized = recipient.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ticket.Id) || string.IsNullOrWhiteSpace(ticket.OrganizationId) ||
            !ValidRecipient(normalized) || !MatchesPurpose(ticket, purpose, taskId))
            return string.Empty;

        var launch = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var maxDays = purpose == PublicLinkPurpose.RequestApproval ? 14 : 7;
        var lifetime = Math.Clamp(options.GetValue("PublicLinks:LaunchDays", maxDays), 1, maxDays);
        db.Add(new PublicLinkCapability
        {
            LaunchDigest = Digest(launch),
            Purpose = purpose,
            TicketId = ticket.Id,
            OrganizationId = ticket.OrganizationId,
            TaskId = taskId,
            Recipient = normalized,
            ExpiresAt = clock.GetUtcNow().AddDays(lifetime).ToUnixTimeSeconds()
        });
        await db.SaveChangesAsync(cancellationToken);
        var baseUrl = string.IsNullOrWhiteSpace(applicationUrl) ? options["PublicWebAppUrl"] : applicationUrl;
        return $"{baseUrl?.TrimEnd('/')}/public/link#{launch}";
    }

    public async Task<PublicLinkExchangeResponse?> ExchangeAsync(PublicLinkExchangeRequest request, CancellationToken ct)
    {
        if (!ValidOpaqueKey(request.BrowserKey) || LaunchDigest(request) is not { } digest)
            return null;
        var browser = Digest(request.BrowserKey);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(digest, ct);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var link = await db.Set<PublicLinkCapability>().SingleOrDefaultAsync(row => row.LaunchDigest == digest, ct);
        if (request.Legacy is { } old)
        {
            var compatibility = await ValidateLegacyAsync(old, now, ct);
            if (compatibility is null || link is not null && !MatchesLegacyBinding(link, compatibility))
                return null;
            if (link is null)
            {
                link = compatibility;
                db.Add(link);
            }
        }
        if (link is null || link.ExpiresAt <= now || link.RevokedAt is not null ||
            !await IsCurrentBindingAsync(link, ct))
            return null;

        if (link.BrowserDigest is not null)
        {
            if (link.BrowserDigest != browser || link.GrantExpiresAt <= now)
                return null;
        }
        else
        {
            link.BrowserDigest = browser;
            var minutes = Math.Clamp(options.GetValue("PublicLinks:GrantMinutes", 30), 5, 120);
            link.GrantExpiresAt = Math.Min(link.ExpiresAt, checked(now + minutes * 60));
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        var expires = DateTimeOffset.FromUnixTimeSeconds(link.GrantExpiresAt!.Value);
        var grant = _grants.Protect($"{link.Id:D}:{browser}", expires);
        return new PublicLinkExchangeResponse(link.Id, link.Purpose, grant, expires);
    }

    public async Task<PublicLinkCapability?> AuthorizeAsync(Guid accessId, string? credential, CancellationToken ct, bool forDecision = false)
    {
        if (credential is null || credential.Length is 0 or > 512)
            return null;
        string data;
        try
        {
            data = _grants.Unprotect(credential);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var prefix = $"{accessId:D}:";
        if (data.Length != prefix.Length + 64 || !data.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var browserDigest = data[prefix.Length..];
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var matching = db.Set<PublicLinkCapability>().Where(row => row.Id == accessId &&
            row.BrowserDigest == browserDigest && row.GrantExpiresAt > now && row.RevokedAt == null);
        if (forDecision && await matching.ExecuteUpdateAsync(update => update.SetProperty(row => row.GrantExpiresAt, row => row.GrantExpiresAt), ct) != 1)
            return null;
        var link = await matching.AsNoTracking().SingleOrDefaultAsync(ct);
        return link is not null && await IsCurrentBindingAsync(link, ct) ? link : null;
    }

    public async Task ReplaceAsync(PublicLinkExchangeRequest request, ITenantBrandingResolver branding,
        MailboxSenderResolver mailboxes, MailboxOutboxStore outbox, CancellationToken ct)
    {
        if (LaunchDigest(request) is not { } digest)
            return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(digest, ct);
        var link = await db.Set<PublicLinkCapability>().SingleOrDefaultAsync(row => row.LaunchDigest == digest, ct);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (request.Legacy is { } old)
        {
            var compatibility = await ValidateLegacyAsync(old, now, ct, allowExpired: true);
            if (compatibility is null || link is not null && !MatchesLegacyBinding(link, compatibility))
                return;
            if (link is null)
            {
                link = compatibility;
                db.Add(link);
            }
        }
        if (link is null || link.RevokedAt is not null || link.ExpiresAt < now - 30 * 86400L ||
            !await IsCurrentBindingAsync(link, ct))
            return;
        var ticket = await db.Tickets.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == link.TicketId, ct);
        var purpose = link.Purpose;
        if (link.Legacy && ticket is Change && purpose == PublicLinkPurpose.ChangeView &&
            await db.Set<ChangeApproval>().AnyAsync(row => row.ChangeId == ticket.Id &&
                row.ApproverEmail.ToLower() == link.Recipient && row.TokenSentAtUtc != null, ct))
            purpose = PublicLinkPurpose.ChangeApproval;

        var sender = await branding.ResolveAsync(ticket.OrganizationId, ct);
        // A legacy link's recipient may be a customer or a designated approver.
        // Resolve the same sender that owns that ticket's ordinary notifications.
        var mailbox = await mailboxes.ResolveAsync(ticket.Id, link.OrganizationId, ct);
        var url = await IssueAsync(ticket, link.Recipient, purpose, sender.TemplateBrand.ApplicationUrl, ct, link.TaskId);
        if (string.IsNullOrEmpty(url))
            return;
        link.RevokedAt = now;
        var html = $"<p><a href=\"{WebUtility.HtmlEncode(url)}\">Open your RatelDesk link</a></p>";
        await outbox.QueueDirectAsync(new IngressEmailEffect([link.Recipient], "Your RatelDesk link", html,
            [], ticket.Id, [], sender.FromName, sender.ReplyTo, false, null, null)
        {
            OrganizationId = link.OrganizationId,
            MailboxId = mailbox.Mailbox?.Id,
            MailboxConfigurationVersion = mailbox.Mailbox?.Version,
            MailboxSourceKey = mailbox.Mailbox?.SourceKey,
            OutgoingConfigurationVersion = mailbox.Outgoing?.Version,
            SenderBindingError = mailbox.ErrorCode
        }, ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<PublicLinkCapability?> ValidateLegacyAsync(LegacyPublicLink legacy, long now, CancellationToken ct,
        bool allowExpired = false)
    {
        var cutoff = await db.Set<PublicLinkCompatibility>().AsNoTracking().Where(row => row.Id == 1)
            .Select(row => (long?)row.LegacyCutoffUnix).SingleOrDefaultAsync(ct) ?? 0;
        if (options.GetValue<DateTimeOffset?>("PublicLinks:LegacyCutoffUtc") is { } configured)
            cutoff = Math.Min(configured.ToUnixTimeSeconds(), cutoff);
        if (now >= cutoff || legacy.TrackingId is not { Length: > 0 and <= 128 } || legacy.TaskId?.Length > 128 ||
            !ValidRecipient(legacy.Email) || legacy.Token is not { Length: > 0 and <= 1024 } ||
            legacy.Purpose is not (PublicLinkPurpose.Ticket or PublicLinkPurpose.ChangeView or PublicLinkPurpose.RequestApproval))
            return null;

        var signedResource = legacy.Purpose == PublicLinkPurpose.RequestApproval
            ? $"{legacy.TrackingId}:{legacy.TaskId}" : legacy.TrackingId;
        var earliestExpiry = allowExpired ? now - 30 * 86400L : now;
        if (!legacySigner.TryReadExpiry(legacy.Token, signedResource, legacy.Email, out var expires) || expires <= earliestExpiry)
            return null;
        var ticket = await db.Tickets.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(row => row.TrackingId == legacy.TrackingId, ct);
        if (ticket is null || string.IsNullOrWhiteSpace(ticket.OrganizationId) || !MatchesPurpose(ticket, legacy.Purpose, legacy.TaskId))
            return null;

        return new PublicLinkCapability
        {
            LaunchDigest = LegacyDigest(legacy.Token), Legacy = true, TicketId = ticket.Id,
            OrganizationId = ticket.OrganizationId, TaskId = legacy.TaskId, Purpose = legacy.Purpose,
            Recipient = legacy.Email.ToLowerInvariant(), ExpiresAt = Math.Min(expires, cutoff)
        };
    }

    private static bool MatchesLegacyBinding(PublicLinkCapability link, PublicLinkCapability compatibility) =>
        link.Purpose == compatibility.Purpose && link.TicketId == compatibility.TicketId &&
        link.OrganizationId == compatibility.OrganizationId && link.TaskId == compatibility.TaskId &&
        link.Recipient == compatibility.Recipient;

    private async Task<bool> IsCurrentBindingAsync(PublicLinkCapability link, CancellationToken ct)
    {
        if (!await db.Tickets.IgnoreQueryFilters().AnyAsync(row => row.Id == link.TicketId && row.OrganizationId == link.OrganizationId, ct) ||
            !await db.Organizations.AnyAsync(row => row.Id == link.OrganizationId &&
                row.State == Helpdesk.Shared.Models.EntityState.Enabled, ct))
            return false;
        if (link.Purpose == PublicLinkPurpose.RequestApproval)
            return await db.RequestTasks.IgnoreQueryFilters().AnyAsync(row => row.Id == link.TaskId && row.RequestId == link.TicketId, ct) &&
                await db.RequestTaskApprovals.AnyAsync(row => row.RequestTaskId == link.TaskId && row.RequestId == link.TicketId &&
                    row.ApproverEmail.ToLower() == link.Recipient, ct);
        if (link.Purpose == PublicLinkPurpose.ChangeApproval)
            return await db.Set<ChangeApproval>().AnyAsync(row => row.ChangeId == link.TicketId &&
                row.ApproverEmail.ToLower() == link.Recipient, ct);
        return true;
    }

    private async Task LockAsync(string digest, CancellationToken ct)
    {
        // An immediate SQLite write transaction already serializes exchange.
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({digest}, 471))", ct);
    }

    private static bool MatchesPurpose(Ticket ticket, PublicLinkPurpose purpose, string? taskId) => purpose switch
    {
        PublicLinkPurpose.Ticket => taskId is null && ticket is Incident or Request or Change,
        PublicLinkPurpose.ChangeView or PublicLinkPurpose.ChangeApproval => ticket is Change && taskId is null,
        PublicLinkPurpose.RequestApproval => ticket is Request && !string.IsNullOrWhiteSpace(taskId),
        _ => false
    };

    private static bool ValidRecipient(string? recipient) =>
        recipient is { Length: > 0 and <= 254 } && System.Net.Mail.MailAddress.TryCreate(recipient, out var email) && email.Address == recipient;

    private static string? LaunchDigest(PublicLinkExchangeRequest request) =>
        request.Legacy is null && ValidOpaqueKey(request.Launch) ? Digest(request.Launch!) :
        request.Launch is null && request.Legacy?.Token is { Length: > 0 and <= 1024 } token ? LegacyDigest(token) : null;

    private static bool ValidOpaqueKey(string? value) =>
        value is { Length: 43 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string LegacyDigest(string token) => Digest($"legacy:{token}");
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
