using System.Text.Json.Nodes;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;

namespace Helpdesk.Infrastructure.Orchestration;

internal static class AutomationMultiSelectMapping
{
    public static IEnumerable<string> FindInputs(IReadOnlyList<FormField> fields, RequestTaskTemplateModel task)
    {
        if (task.PayloadMapping is null)
        {
            return [];
        }

        var multiSelectKeys = fields.Select((field, index) => (Field: field, Key: FormFieldKeyResolver.Resolve(field, index)))
            .Where(pair => pair.Field.MultiSelect)
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return task.PayloadMapping.Where(mapping => multiSelectKeys.Contains(mapping.Value.Trim()))
            .Select(mapping => mapping.Key.Trim()).Where(key => key.Length > 0);
    }

    public static string? Validate(IEnumerable<string> multiSelectInputs,
        IReadOnlyList<OrchestrationCatalogInputDefinitionDto> targetInputs, JsonObject? values = null)
    {
        foreach (var key in multiSelectInputs)
        {
            var input = targetInputs.FirstOrDefault(input => string.Equals(input.Key, key, StringComparison.OrdinalIgnoreCase));
            if (input is null || !IsText(input.Type) || IsEnvelopeKey(key))
            {
                return $"Multi-select mapping for '{key}' requires an existing NetRatel Text input.";
            }

            if (values is not null && input.Required && string.IsNullOrWhiteSpace(input.DefaultValue) && !values.ContainsKey(key))
            {
                return $"Choose at least one option for required NetRatel input '{key}'.";
            }
        }

        return null;
    }

    public static bool IsText(string? type) => type?.Trim().ToLowerInvariant() is "text" or "string";

    private static bool IsEnvelopeKey(string key) =>
        string.Equals(key, "meta", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "input", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "expectedRuntimeSeconds", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "graceSeconds", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "hardTimeoutSeconds", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("node.", StringComparison.OrdinalIgnoreCase);
}
