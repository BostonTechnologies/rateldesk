using System.Globalization;
using System.Text.Json;

namespace Helpdesk.Mcp.Tools;

internal static class TicketListPagination
{
    public const int Minimum = 1;
    public const int MaximumPage = 1_000_000;
    public const int MaximumPageSize = 100;

    public static string? Validate(JsonElement? request, out int? page, out int? pageSize)
    {
        page = pageSize = null;
        if (request is not { ValueKind: JsonValueKind.Object } element) return null;
        foreach (var property in element.EnumerateObject())
        {
            var canonicalName = property.Name.Equals("page", StringComparison.OrdinalIgnoreCase) ? "page" :
                property.Name.Equals("pageSize", StringComparison.OrdinalIgnoreCase) ? "pageSize" : null;
            // REST query binding ignores case; do not let unsupported spellings
            // bypass the bounds or introduce a second effective parameter.
            if (canonicalName is not null && property.Name != canonicalName)
                return $"request.{property.Name} is not supported; use request.{canonicalName}.";
            if (property.Name is not ("page" or "pageSize")) continue;
            var maximum = property.Name == "page" ? MaximumPage : MaximumPageSize;
            if ((property.Name == "page" ? page : pageSize).HasValue)
                return $"request.{property.Name} must not be supplied more than once.";
            if (!TryBoundedInteger(property.Value, maximum, out var value))
                return $"request.{property.Name} must be an integer from {Minimum} to {maximum}.";
            if (property.Name == "page") page = value;
            else pageSize = value;
        }
        return null;
    }

    private static bool TryBoundedInteger(JsonElement element, int maximum, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number) return false;
        // JSON Schema integers are mathematical values, so 1.0 and 1e0 are valid.
        // Inspect the decimal digits exactly: floating-point/decimal conversion can
        // round a tiny fractional remainder into an apparently integral value.
        var raw = element.GetRawText();
        if (raw[0] == '-') return false;
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var exponent = 0;
        if (exponentIndex >= 0 &&
            !int.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return false;
        var mantissa = exponentIndex >= 0 ? raw[..exponentIndex] : raw;
        var point = mantissa.IndexOf('.');
        var fractionalDigits = point >= 0 ? mantissa.Length - point - 1 : 0;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return false;
        var significant = digits.TrimEnd('0');
        var shift = (long)exponent - fractionalDigits + digits.Length - significant.Length;
        if (shift < 0 || significant.Length + shift > maximum.ToString(CultureInfo.InvariantCulture).Length ||
            !int.TryParse(significant, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
        for (var index = 0; index < shift; index++) value *= 10;
        return value >= Minimum && value <= maximum;
    }
}
