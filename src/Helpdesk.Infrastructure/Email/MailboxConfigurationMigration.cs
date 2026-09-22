using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Runs after schema migration, before any ingress worker. Cryptography requires the application key ring.</summary>
public sealed class MailboxConfigurationMigration(HelpdeskDbContext db, MailboxCredentialProtector secrets, IConfiguration configuration)
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (await db.Set<MailboxMigrationState>().AnyAsync(x => x.Id == 1 && x.Completed, ct)) return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var modern = await db.EmailInboxSettings.ToListAsync(ct);
        var selected = ImapEmailService.OrderByCurrentInboxSettings(modern).FirstOrDefault();
        if (selected is null)
        {
            var legacy = await db.ImapEmailSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            var address = legacy?.UserEmail ?? configuration["ExchangeEmail:MailboxAddress"];
            if (!string.IsNullOrWhiteSpace(address))
            {
                selected = new EmailInboxSettings
                {
                    Id = Guid.NewGuid(), MailboxAddress = address,
                    MailHost = legacy?.Host ?? "outlook.office365.com", Port = legacy?.Port ?? 993,
                    TenantId = legacy?.TenantId ?? configuration["ExchangeEmail:TenantId"] ?? string.Empty,
                    ClientId = legacy?.ClientId ?? configuration["ExchangeEmail:ClientId"] ?? string.Empty,
                    ClientSecret = legacy?.ClientSecret ?? configuration["ExchangeEmail:ClientSecret"] ?? string.Empty,
                    MailboxFolder = legacy?.Mailbox ?? "inbox", Enabled = legacy?.Enabled ?? false,
                    BackgroundSyncEnabled = legacy?.Enabled ?? false,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                };
                modern.Add(selected);
                db.EmailInboxSettings.Add(selected);
            }
        }
        // Freeze the legacy sender selection before archiving additional sources. Ingress
        // selection has a separate, deterministic precedence; adding overrides must never
        // change the outgoing identity on subsequent restarts.
        var outgoing = modern.FirstOrDefault(x => x.Enabled && x.BackgroundSyncEnabled)?.MailboxAddress
            ?? modern.FirstOrDefault(x => x.Enabled)?.MailboxAddress
            ?? configuration["ExchangeEmail:MailboxAddress"] ?? string.Empty;
        foreach (var mailbox in modern)
        {
            mailbox.MailboxAddress = mailbox.MailboxAddress.Trim().ToLowerInvariant();
            mailbox.Archived = mailbox != selected;
            mailbox.Scope = MailboxScope.Global;
            mailbox.OrganizationId = null;
            mailbox.Provider = InboundMailboxProvider.Graph;
            mailbox.Authentication = MailboxAuthentication.MicrosoftApplication;
            mailbox.DisplayName = mailbox.MailboxAddress;
            mailbox.InitialImport = InitialMailImport.ExistingUnread;
            mailbox.LegacySource = mailbox == selected;
            mailbox.SourceKey = MailboxSettingsService.SourceKey(mailbox);
            if (mailbox.CredentialVersion == 0)
            {
                var plaintext = mailbox.ClientSecret ?? string.Empty;
                mailbox.ClientSecret = secrets.Protect(mailbox.Id, plaintext);
                mailbox.CredentialVersion = 1;
                if (secrets.Unprotect(mailbox, mailbox.ClientSecret) != plaintext)
                    throw new InvalidOperationException("Mailbox credential verification failed.");
            }
            db.Set<MailboxLease>().Add(new() { MailboxId = mailbox.Id });
            db.Set<MailboxIngestionState>().Add(new() { MailboxId = mailbox.Id, SourceKey = mailbox.SourceKey });
        }
        // Superseded plaintext legacy credentials must not remain an alternate configuration path.
        foreach (var legacy in await db.ImapEmailSettings.ToListAsync(ct)) legacy.ClientSecret = string.Empty;
        db.Set<MailboxMigrationState>().Add(new() { Completed = true, OutboundMailboxAddress = outgoing });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
