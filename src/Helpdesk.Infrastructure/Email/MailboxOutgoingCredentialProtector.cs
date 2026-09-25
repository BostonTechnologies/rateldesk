using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.DataProtection;

namespace Helpdesk.Infrastructure.Email;

public sealed class MailboxOutgoingCredentialProtector(IDataProtectionProvider provider)
{
    private IDataProtector For(Guid mailboxId, string host, int port, string username) =>
        provider.CreateProtector("RatelDesk.OutboundMailbox", "v1", mailboxId.ToString("D"),
            host.Trim().ToLowerInvariant(), port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            username.Trim().ToLowerInvariant());

    public string Protect(MailboxOutgoingSettings settings, string value) => value.Length == 0
        ? string.Empty
        : For(settings.MailboxId, settings.SmtpHost, settings.SmtpPort, settings.SmtpUsername).Protect(value);

    public string Unprotect(MailboxOutgoingSettings settings) => settings.ProtectedSmtpPassword.Length == 0
        ? string.Empty
        : For(settings.MailboxId, settings.SmtpHost, settings.SmtpPort, settings.SmtpUsername)
            .Unprotect(settings.ProtectedSmtpPassword);
}
