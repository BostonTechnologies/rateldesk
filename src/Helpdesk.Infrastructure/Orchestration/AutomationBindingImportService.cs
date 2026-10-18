using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class AutomationBindingImportService(
    HelpdeskDbContext db,
    ITenantContext tenantContext,
    IRequestFormSchemaParser schemaParser,
    IOrchestrationCatalogService orchestrationCatalogService) : IAutomationBindingImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HelpdeskDbContext _db = db;
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly IRequestFormSchemaParser _schemaParser = schemaParser;
    private readonly IOrchestrationCatalogService _stoCatalogService = orchestrationCatalogService;

    public async Task<AutomationBindingDto> ImportAsync(string bindingId, CancellationToken cancellationToken = default)
    {
        var binding = await QueryForTenant()
            .FirstOrDefaultAsync(x => x.Id == bindingId, cancellationToken)
            ?? throw new InvalidOperationException("Automation binding was not found.");

        var requestForm = await _db.RequestForms
            .FirstOrDefaultAsync(x => x.Id == binding.RequestFormId, cancellationToken)
            ?? throw new InvalidOperationException("Request form was not found for the automation binding.");

        var remoteDefinitions = await _stoCatalogService.ListRequestDefinitionsAsync(binding.OrganizationId, binding.SystemConnectionId, cancellationToken);
        var remote = remoteDefinitions.FirstOrDefault(x =>
            string.Equals(x.RequestDefinitionId, binding.OrchestrationRequestDefinitionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The bound External orchestration request definition was not found.");

        var schema = _schemaParser.Parse(requestForm.JsonSchema);
        var taskTemplate = schema.Tasks.FirstOrDefault(x => x.Id == binding.TaskTemplateId)
            ?? throw new InvalidOperationException("Task template was not found in the request form schema.");

        if (!string.Equals(taskTemplate.Type, "automation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only automation task templates can import External orchestration inputs.");
        }

        await AutomationBindingSelectionFence.SaveAsync(_db, [binding], () =>
        {
            ApplyRemoteInputsToSchema(schema.Fields, taskTemplate, remote.Inputs);
            requestForm.JsonSchema = RebuildSchemaDocument(requestForm.JsonSchema, schema.Fields, schema.Tasks);

            binding.OrchestrationRequestDefinitionName = FirstNonEmpty(remote.RequestDefinitionName, binding.OrchestrationRequestDefinitionName);
            binding.OrchestrationJobDefinitionId = FirstNonEmpty(remote.OrchestrationJobDefinitionId, binding.OrchestrationJobDefinitionId);
            binding.OrchestrationJobDefinitionName = FirstNonEmpty(remote.OrchestrationJobDefinitionName, binding.OrchestrationJobDefinitionName);
            binding.LastSyncHash = AutomationBindingSchemaHash.Compute(remote.Inputs.ToList());
            binding.LastSyncVersion = "schema:v1";
            binding.LastSyncedAtUtc = DateTimeOffset.UtcNow;
            binding.LastSyncDirection = "OrchestrationToHelpdesk";
            binding.LastReviewedDriftAtUtc = DateTimeOffset.UtcNow;
            binding.SyncState = AutomationBindingSyncState.InSync;
            binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }, cancellationToken);

        return new AutomationBindingDto
        {
            Id = binding.Id,
            SystemConnectionId = binding.SystemConnectionId,
            OrganizationId = binding.OrganizationId,
            RequestFormId = binding.RequestFormId,
            RequestFormTitle = requestForm.Title,
            TaskTemplateId = binding.TaskTemplateId,
            TaskTemplateName = taskTemplate.Name,
            OrchestrationRequestDefinitionId = binding.OrchestrationRequestDefinitionId,
            OrchestrationRequestDefinitionName = binding.OrchestrationRequestDefinitionName,
            OrchestrationJobDefinitionId = binding.OrchestrationJobDefinitionId,
            OrchestrationJobDefinitionName = binding.OrchestrationJobDefinitionName,
            SyncState = binding.SyncState,
            LastSyncHash = binding.LastSyncHash,
            LastSyncVersion = binding.LastSyncVersion,
            LastSyncedAtUtc = binding.LastSyncedAtUtc,
            LastSyncDirection = binding.LastSyncDirection,
            LastReviewedDriftAtUtc = binding.LastReviewedDriftAtUtc,
            LastCorrelationId = binding.LastCorrelationId,
            Enabled = binding.Enabled,
            CreatedAtUtc = binding.CreatedAtUtc,
            UpdatedAtUtc = binding.UpdatedAtUtc
        };
    }

    private IQueryable<AutomationBinding> QueryForTenant()
    {
        var query = _db.AutomationBindings.AsQueryable();
        if (!_tenantContext.IsHelpdeskAdmin
            && !string.IsNullOrWhiteSpace(_tenantContext.TenantId))
        {
            query = query.Where(x => x.OrganizationId == _tenantContext.TenantId);
        }

        return query;
    }

    private static void ApplyRemoteInputsToSchema(
        IList<FormField> fields,
        RequestTaskTemplateModel taskTemplate,
        IReadOnlyList<OrchestrationCatalogInputDefinitionDto> remoteInputs)
    {
        var fieldLookup = fields
            .Select((field, index) => new { Field = field, Key = FormFieldKeyResolver.Resolve(field, index) })
            .ToDictionary(x => x.Key, x => x.Field, StringComparer.OrdinalIgnoreCase);

        var newMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in remoteInputs.OrderBy(x => x.Order).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var fieldKey = input.Key.Trim();
            if (!fieldLookup.TryGetValue(fieldKey, out var field))
            {
                field = new FormField
                {
                    Key = fieldKey
                };
                fields.Add(field);
                fieldLookup[fieldKey] = field;
            }

            field.Key = fieldKey;
            field.Label = string.IsNullOrWhiteSpace(input.Label) ? fieldKey : input.Label.Trim();
            field.Type = NormalizeFieldType(input.Type);
            field.Required = input.Required;

            newMapping[input.Key.Trim()] = fieldKey;
        }

        taskTemplate.PayloadMapping = newMapping;
    }

    private static JsonDocument RebuildSchemaDocument(
        JsonDocument existingSchema,
        IReadOnlyList<FormField> fields,
        IReadOnlyList<RequestTaskTemplateModel> tasks)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(existingSchema.RootElement.GetRawText())?.AsObject() ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root["fields"] = JsonSerializer.SerializeToNode(fields, JsonOptions);
        root["tasks"] = JsonSerializer.SerializeToNode(tasks, JsonOptions);

        return JsonDocument.Parse(root.ToJsonString(JsonOptions));
    }

    private static string NormalizeFieldType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "text";
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "textarea" => "textarea",
            "number" => "number",
            "boolean" => "boolean",
            "helpdeskdata" => "text",
            "select" => "text",
            "radio" => "text",
            "date" => "text",
            "datetime" => "text",
            _ => "text"
        };
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }
}
