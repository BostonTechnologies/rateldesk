using Microsoft.AspNetCore.DataProtection;
using Helpdesk.Shared.Models;

namespace Helpdesk.Infrastructure.Email;

public sealed class MailboxCredentialProtector(IDataProtectionProvider provider)
{
    private IDataProtector For(Guid id) => provider.CreateProtector("RatelDesk.InboundMailbox", "v1", id.ToString("D"));

    public string Protect(Guid id, string value) => value.Length == 0 ? string.Empty : For(id).Protect(value);

    public string Unprotect(EmailInboxSettings mailbox, string value)
    {
        if (value.Length == 0) return string.Empty;
        if (mailbox.CredentialVersion != 1)
            throw new InvalidOperationException("Mailbox credentials require migration.");
        return For(mailbox.Id).Unprotect(value);
    }
}
