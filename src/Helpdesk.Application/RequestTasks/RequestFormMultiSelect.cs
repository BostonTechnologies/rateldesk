using System.Text.Json.Nodes;
using Helpdesk.Shared.DTOs.RequestForm;

namespace Helpdesk.Application.RequestTasks;

public static class RequestFormMultiSelect
{
    public static string? ValidateFields(IReadOnlyCollection<FormField> fields)
    {
        if (!fields.Any(field => field.MultiSelect))
        {
            return null;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_boundLabels" };
        foreach (var (field, index) in fields.Select((field, index) => (field, index)))
        {
            if (!keys.Add(FormFieldKeyResolver.Resolve(field, index)))
            {
                return $"Field '{field.Label}' needs a unique field key that is not reserved.";
            }

            if (!field.MultiSelect)
            {
                continue;
            }

            var type = field.Type?.Trim();
            var supported = field.DataBinding is null
                ? string.Equals(type, "predefined", StringComparison.OrdinalIgnoreCase)
                : string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
                  && !string.IsNullOrWhiteSpace(field.DataBinding.DatasetId);
            if (!supported)
            {
                return $"Field '{field.Label}' can use multi-select with a predefined list or dataset-bound Text field.";
            }

            if (field.DataBinding?.AllowFreeText == true)
            {
                return $"Field '{field.Label}' cannot allow free text with multi-select enabled.";
            }
        }

        return null;
    }

    public static string? ReadSelections(FormField field, JsonNode? value, out List<string> selections)
    {
        selections = [];
        if (value is null)
        {
            return field.Required ? $"Choose at least one option for '{field.Label}'." : null;
        }

        if (value is not JsonArray array)
        {
            return $"Field '{field.Label}' must contain an array of selected strings.";
        }

        if (array.Count > RequestFormLimits.MaxSelectionsPerField)
        {
            return $"Field '{field.Label}' allows up to {RequestFormLimits.MaxSelectionsPerField} selections.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array)
        {
            if (!TryReadString(item, out var text) || string.IsNullOrWhiteSpace(text))
            {
                return $"Field '{field.Label}' must contain only non-blank selected strings.";
            }

            var key = text.Trim();
            if (seen.Add(key))
            {
                selections.Add(key);
            }
        }

        return field.Required && selections.Count == 0 ? $"Choose at least one option for '{field.Label}'." : null;
    }

    public static bool TryReadString(JsonNode? node, out string? text)
    {
        text = null;
        try
        {
            return node is JsonValue value && value.TryGetValue(out text);
        }
        catch (InvalidOperationException)
        {
            // A JSON string can contain an incomplete Unicode escape.
            return false;
        }
    }

    public static JsonArray ToJsonArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
