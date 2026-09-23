namespace Helpdesk.Infrastructure.Email;

internal static class EmailAddressGuard
{
    internal static bool IsSingleAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320) return false;
        var address = value.Trim();
        return System.Net.Mail.MailAddress.TryCreate(address, out var parsed) &&
            string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSameAddress(string? address, string? mailboxAddress)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(mailboxAddress))
        {
            return false;
        }

        return string.Equals(
            address.Trim(),
            mailboxAddress.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static List<string> NormalizeRecipients(
        IEnumerable<string>? recipients,
        string? excludedMailboxAddress = null)
    {
        return (recipients ?? Enumerable.Empty<string>())
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Where(address => !IsSameAddress(address, excludedMailboxAddress))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
