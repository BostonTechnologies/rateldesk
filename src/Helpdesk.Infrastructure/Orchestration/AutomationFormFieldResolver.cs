using Helpdesk.Shared.DTOs.RequestForm;

namespace Helpdesk.Infrastructure.Orchestration;

internal static class AutomationFormFieldResolver
{
    public static (FormField? Field, string Key, bool Ambiguous) Resolve(
        IReadOnlyCollection<FormField> fields, string reference)
    {
        var key = reference.Trim();
        if (key.Length == 0) return (null, key, false);

        var candidates = fields.Select((field, index) => (Field: field, Key: FormFieldKeyResolver.Resolve(field, index)));
        var matches = candidates.Where(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matches.Length == 0)
        {
            // Older mappings may contain a display name. Only a unique match on this saved form can recover it.
            var name = WithoutWhitespace(key);
            matches = candidates.Where(candidate =>
                string.Equals(WithoutWhitespace(candidate.Field.Label), name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches is [var match] && candidates.Count(candidate =>
                string.Equals(candidate.Key, match.Key, StringComparison.OrdinalIgnoreCase)) > 1)
                return (null, key, true);
        }

        return matches.Length == 1 ? (matches[0].Field, matches[0].Key, false) : (null, key, matches.Length > 1);
    }

    public static string DescribeUnresolved(string reference, bool ambiguous) => ambiguous
        ? $"'{reference}' matches more than one Helpdesk field"
        : $"Missing field: {reference}";

    public static string UnresolvedMappingMessage(string reference, bool ambiguous)
        => (ambiguous ? $"Payload mapping {DescribeUnresolved(reference, true)}." : $"Payload mapping references missing field '{reference}'.")
           + " Ask a form administrator to select the Helpdesk field under Workflow → Payload Mapping.";

    private static string WithoutWhitespace(string? value)
        => string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
}
