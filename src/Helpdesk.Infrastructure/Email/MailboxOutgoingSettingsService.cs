using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

public sealed class MailboxOutgoingSettingsService(HelpdeskDbContext db, MailboxOutgoingCredentialProtector secrets)
{
    public async Task<MailboxOutgoingSettingsDto?> GetAsync(Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await db.EmailInboxSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mailboxId, ct);
        if (mailbox is null) return null;
        var outgoing = await db.Set<MailboxOutgoingSettings>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.MailboxId == mailboxId, ct);
        return ToDto(mailbox, outgoing);
    }

    public async Task<MailboxOutgoingSettingsDto> SaveAsync(Guid mailboxId,
        MailboxOutgoingSettingsRequest request, CancellationToken ct)
    {
        var mailbox = await db.EmailInboxSettings.SingleOrDefaultAsync(x => x.Id == mailboxId, ct)
            ?? throw new KeyNotFoundException("Mailbox was not found.");
        if (mailbox.Archived) throw new InvalidOperationException("Archived mailboxes cannot be configured for sending.");
        if (!Enum.IsDefined(request.Transport) || !Enum.IsDefined(request.SmtpTlsMode))
            throw new ArgumentException("Choose a supported outgoing transport and TLS mode.");
        if (request.DisplayName.Length > 200 || request.SmtpHost.Length > 253 || request.SmtpUsername.Length > 320 ||
            request.SmtpPassword.Length > 4096)
            throw new ArgumentException("Outgoing fields exceed supported lengths.");
        var existing = await db.Set<MailboxOutgoingSettings>().SingleOrDefaultAsync(x => x.MailboxId == mailboxId, ct);
        if ((existing?.Version ?? 0) != request.Version)
            throw new DbUpdateConcurrencyException("Outgoing configuration changed; reload before saving.");
        var outgoing = existing ?? new MailboxOutgoingSettings { MailboxId = mailboxId };
        var host = request.SmtpHost.Trim().ToLowerInvariant();
        var username = request.SmtpUsername.Trim();
        var changedDestination = existing is not null &&
            (!string.Equals(existing.SmtpHost, host, StringComparison.OrdinalIgnoreCase) ||
             existing.SmtpPort != request.SmtpPort ||
             !string.Equals(existing.SmtpUsername, username, StringComparison.OrdinalIgnoreCase));
        if (request.Transport == MailboxOutgoingTransport.Smtp)
        {
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || request.SmtpPort is < 1 or > 65535 ||
                username.Length == 0)
                throw new ArgumentException("SMTP host, port and username are required.");
            if (changedDestination && request.SmtpPassword.Length == 0 && !request.ClearSmtpPassword)
                throw new ArgumentException("Changing the SMTP destination requires a new password.");
        }
        else if (mailbox.Authentication != MailboxAuthentication.MicrosoftApplication || mailbox.ClientSecret.Length == 0)
            throw new ArgumentException("Graph sending requires Microsoft application credentials on this mailbox.");

        outgoing.Enabled = request.Enabled;
        outgoing.Transport = request.Transport;
        outgoing.DisplayName = request.DisplayName.Trim();
        outgoing.SmtpHost = host;
        outgoing.SmtpPort = request.SmtpPort;
        outgoing.SmtpTlsMode = request.SmtpTlsMode;
        outgoing.SmtpUsername = username;
        outgoing.ProtectedSmtpPassword = request.ClearSmtpPassword ? string.Empty
            : request.SmtpPassword.Length > 0 ? secrets.Protect(outgoing, request.SmtpPassword)
            : existing?.ProtectedSmtpPassword ?? string.Empty;
        if (request.Transport == MailboxOutgoingTransport.Graph)
            outgoing.ProtectedSmtpPassword = string.Empty;
        if (outgoing.Enabled && request.Transport == MailboxOutgoingTransport.Smtp && outgoing.ProtectedSmtpPassword.Length == 0)
            throw new ArgumentException("An enabled SMTP sender requires its own password.");
        if (existing is null) db.Set<MailboxOutgoingSettings>().Add(outgoing);
        else outgoing.Version++;
        outgoing.LastTestUnixMilliseconds = null;
        outgoing.TestedVersion = null;
        outgoing.LastTestCode = null;
        await db.SaveChangesAsync(ct);
        return ToDto(mailbox, outgoing);
    }

    private static MailboxOutgoingSettingsDto ToDto(EmailInboxSettings mailbox, MailboxOutgoingSettings? outgoing) =>
        new(mailbox.Id, outgoing?.Version ?? 0, outgoing?.Enabled ?? false,
            outgoing?.Transport ?? (mailbox.Provider == InboundMailboxProvider.Graph
                ? MailboxOutgoingTransport.Graph : MailboxOutgoingTransport.Smtp),
            outgoing?.DisplayName ?? string.Empty, mailbox.MailboxAddress,
            outgoing?.SmtpHost ?? string.Empty, outgoing?.SmtpPort ?? 587,
            outgoing?.SmtpTlsMode ?? MailboxTlsMode.StartTls,
            outgoing?.SmtpUsername ?? string.Empty, outgoing?.ProtectedSmtpPassword.Length > 0,
            outgoing?.LastTestUnixMilliseconds, outgoing?.TestedVersion, outgoing?.LastTestCode);
}
