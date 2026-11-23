using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Helpdesk.Infrastructure.Email;

internal static partial class PublicLinkMailContent
{
    private const string Link = "/public/link#";
    private const string Protected = Link + "!rd-mail.";

    [GeneratedRegex(Link + "([A-Za-z0-9_-]{43})(?![A-Za-z0-9_-])")]
    private static partial Regex LaunchLinks();

    [GeneratedRegex(Link + "!rd-mail\\.([A-Za-z0-9_-]{1,512})(?![A-Za-z0-9_-])")]
    private static partial Regex StoredLinks();

    internal static string Protect(string value, Guid deliveryId, IDataProtectionProvider? keys) =>
        LaunchLinks().Replace(value, match => Protected + Protector(deliveryId, keys).Protect(match.Groups[1].Value));

    internal static string Unprotect(string value, Guid deliveryId, IDataProtectionProvider? keys) =>
        StoredLinks().Replace(value, match => Link + Protector(deliveryId, keys).Unprotect(match.Groups[1].Value));

    private static IDataProtector Protector(Guid deliveryId, IDataProtectionProvider? keys) =>
        (keys ?? throw new InvalidOperationException("Public link delivery requires data protection."))
        .CreateProtector("RatelDesk.PublicLinkMail.v1", deliveryId.ToString("D"));
}
