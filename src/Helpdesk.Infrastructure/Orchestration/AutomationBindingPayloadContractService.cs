using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class AutomationBindingPayloadContractService(
    IRequestFormSchemaParser schemaParser,
    IAutomationBindingService automationBindings,
    IOrchestrationCatalogService? catalog = null) : IAutomationBindingPayloadContractService
{
    private readonly IRequestFormSchemaParser _schemaParser = schemaParser;
    private readonly IAutomationBindingService _automationBindings = automationBindings;

    public async Task<AutomationPayloadValidationResult> ValidateBoundRequestPayloadAsync(
        RequestForm requestForm,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        var schema = _schemaParser.Parse(requestForm.JsonSchema);
        var payloadResult = ParsePayload(payloadJson);
        if (!payloadResult.Success)
        {
            return AutomationPayloadValidationResult.Failed(payloadResult.Error!);
        }

        var bindings = await _automationBindings.ListAsync(requestForm.Id, cancellationToken);
        var enabledBindings = bindings
            .Where(x => x.Enabled)
            .ToDictionary(x => x.TaskTemplateId, x => x);

        if (enabledBindings.Count == 0)
        {
            return AutomationPayloadValidationResult.Passed();
        }

        var errors = new List<string>();
        var catalogs = new Dictionary<(string OrganizationId, Guid? ConnectionId), IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>>();
        foreach (var taskTemplate in schema.Tasks.Where(x => x.Id != Guid.Empty))
        {
            if (!enabledBindings.TryGetValue(taskTemplate.Id, out var binding))
            {
                continue;
            }

            if (!string.Equals(taskTemplate.Type, "automation", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Bound task '{taskTemplate.Name}' is not configured as an automation task.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(binding.OrchestrationRequestDefinitionId))
            {
                errors.Add($"Bound automation task '{taskTemplate.Name}' is missing an External orchestration request definition id.");
                continue;
            }

            if (binding.SyncState == AutomationBindingSyncState.Broken)
            {
                errors.Add($"Bound automation task '{taskTemplate.Name}' has a broken External orchestration binding.");
                continue;
            }

            var buildResult = BuildTaskInputCore(schema.Fields, taskTemplate, payloadResult.PayloadRoot!, enforceCurrentSchema: true);
            if (!buildResult.Success)
            {
                errors.Add($"Task '{taskTemplate.Name}': {buildResult.Error}");
                continue;
            }

            if (buildResult.MultiSelectInputs.Count == 0)
            {
                continue;
            }

            try
            {
                var selection = (binding.OrganizationId, binding.SystemConnectionId);
                if (catalog is not null && !catalogs.ContainsKey(selection))
                {
                    catalogs[selection] = await catalog.ListRequestDefinitionsAsync(selection.OrganizationId, selection.SystemConnectionId, cancellationToken);
                }

                var targetInputs = ResolveTargetInputs(catalogs.GetValueOrDefault(selection), binding);
                var error = AutomationMultiSelectMapping.Validate(buildResult.MultiSelectInputs, targetInputs, buildResult.InputNode);
                if (error is not null)
                {
                    errors.Add($"Task '{taskTemplate.Name}': {error}");
                }
            }
            catch (OrchestrationProviderException)
            {
                errors.Add($"Task '{taskTemplate.Name}': NetRatel inputs could not be verified. Retry or ask an administrator to check the connection.");
            }
        }

        return errors.Count == 0
            ? AutomationPayloadValidationResult.Passed()
            : AutomationPayloadValidationResult.Failed([.. errors]);
    }

    public AutomationTaskInputBuildResult BuildTaskInput(
        RequestForm requestForm,
        RequestTaskTemplateModel taskTemplate,
        string payloadJson)
    {
        var schema = _schemaParser.Parse(requestForm.JsonSchema);
        var payloadResult = ParsePayload(payloadJson);
        if (!payloadResult.Success)
        {
            return AutomationTaskInputBuildResult.Failed(payloadResult.Error!);
        }

        // A task may run after its form changed. Retain the submitted scalar or array shape.
        return BuildTaskInputCore(schema.Fields, taskTemplate, payloadResult.PayloadRoot!, enforceCurrentSchema: false);
    }

    private static AutomationTaskInputBuildResult BuildTaskInputCore(
        IReadOnlyCollection<FormField> fields,
        RequestTaskTemplateModel taskTemplate,
        JsonObject payloadRoot,
        bool enforceCurrentSchema)
    {
        if (taskTemplate.PayloadMapping is null || taskTemplate.PayloadMapping.Count == 0)
        {
            return AutomationTaskInputBuildResult.Succeeded(new JsonObject());
        }

        var fieldLookup = fields
            .Select((field, index) => new { Field = field, Key = FormFieldKeyResolver.Resolve(field, index) })
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        var inputNode = new JsonObject();
        var multiSelectInputs = new List<string>();
        var unresolvedMappedFields = new List<string>();
        foreach (var mapping in taskTemplate.PayloadMapping.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var payloadKey = mapping.Key?.Trim();
            var fieldKey = mapping.Value?.Trim();
            if (string.IsNullOrWhiteSpace(payloadKey) || string.IsNullOrWhiteSpace(fieldKey))
            {
                continue;
            }

            if (!fieldLookup.TryGetValue(fieldKey, out var source))
            {
                return AutomationTaskInputBuildResult.Failed($"Payload mapping references missing field '{fieldKey}'.");
            }

            var field = source.Field;
            var sourceValue = payloadRoot[source.Key];
            var isMultiSelect = enforceCurrentSchema ? field.MultiSelect
                : sourceValue is JsonArray || (field.MultiSelect && sourceValue is null);
            if (isMultiSelect)
            {
                multiSelectInputs.Add(payloadKey);
                var error = RequestFormMultiSelect.ReadSelections(field, sourceValue, out var selections);
                if (error is not null)
                {
                    return AutomationTaskInputBuildResult.Failed(error);
                }

                if (selections.Count != 0)
                {
                    // Keep literal data out of NetRatel's @node.*@ template expansion.
                    inputNode[payloadKey] = sourceValue!.ToJsonString().Replace("@", "\\u0040", StringComparison.Ordinal);
                }

                continue;
            }

            if (sourceValue is null)
            {
                unresolvedMappedFields.Add(fieldKey);
                if (field.Required)
                {
                    return AutomationTaskInputBuildResult.Failed(
                        $"Required mapped field '{fieldKey}' is missing from the request payload.");
                }

                continue;
            }

            if (IsMissingValue(field, sourceValue))
            {
                unresolvedMappedFields.Add(fieldKey);
                if (field.Required)
                {
                    return AutomationTaskInputBuildResult.Failed(
                        $"Required mapped field '{fieldKey}' is empty in the request payload.");
                }

                continue;
            }

            inputNode[payloadKey] = sourceValue.DeepClone();
        }

        if (inputNode.Count == 0 && multiSelectInputs.Count == 0)
        {
            var missingFields = unresolvedMappedFields.Count == 0
                ? "none"
                : string.Join(", ", unresolvedMappedFields.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return AutomationTaskInputBuildResult.Failed(
                $"Task '{taskTemplate.Name}' payload mapping did not resolve any External orchestration job inputs. Missing or empty Helpdesk fields: {missingFields}.");
        }

        return AutomationTaskInputBuildResult.Succeeded(inputNode, multiSelectInputs);
    }

    internal static IReadOnlyList<OrchestrationCatalogInputDefinitionDto> ResolveTargetInputs(
        IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>? definitions, AutomationBindingDto binding) =>
        definitions?.FirstOrDefault(candidate => candidate.RequestDefinitionId == binding.OrchestrationRequestDefinitionId &&
            (string.IsNullOrWhiteSpace(binding.OrchestrationJobDefinitionId)
             || candidate.OrchestrationJobDefinitionId == binding.OrchestrationJobDefinitionId))?.Inputs ?? [];

    private static (bool Success, JsonObject? PayloadRoot, string? Error) ParsePayload(string payloadJson)
    {
        try
        {
            var payloadRoot = JsonNode.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson)?.AsObject()
                              ?? new JsonObject();
            return (true, payloadRoot, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Request payload JSON is invalid: {Truncate(ex.Message, 200)}");
        }
    }

    private static bool IsMissingValue(FormField field, JsonNode value)
    {
        var normalizedType = (field.Type ?? string.Empty).Trim().ToLowerInvariant();

        if (value is JsonValue jsonValue)
        {
            try
            {
                return normalizedType switch
                {
                    "text" or "textarea" or "predefined" or "select" or "radio" or "date" or "datetime"
                        => string.IsNullOrWhiteSpace(jsonValue.GetValue<string?>()),
                    "number"
                        => jsonValue.TryGetValue<int>(out _)
                           || jsonValue.TryGetValue<long>(out _)
                           || jsonValue.TryGetValue<decimal>(out _)
                           || !string.IsNullOrWhiteSpace(jsonValue.GetValue<string?>())
                            ? false
                            : true,
                    "boolean" => false,
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
