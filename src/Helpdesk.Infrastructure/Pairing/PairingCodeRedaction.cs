using System.Text.RegularExpressions;

namespace Helpdesk.Infrastructure.Pairing;

internal static class PairingCodeRedaction
{
    public static string? Apply(string? value, string? code)
    {
        if (value is null || string.IsNullOrEmpty(code)) return value;
        if (value.Length > 65536) return "The peer message exceeded the supported size.";
        var compact = code.Replace("-", "", StringComparison.Ordinal).Trim();
        if (compact.Length != 8 || compact.Any(x => !char.IsAsciiLetterOrDigit(x))) return value;
        var pattern = string.Join("-*", compact.Select(x => Regex.Escape(x.ToString())));
        return Regex.Replace(value, pattern, "[redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
