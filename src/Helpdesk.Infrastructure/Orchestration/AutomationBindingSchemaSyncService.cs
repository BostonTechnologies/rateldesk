using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class AutomationBindingSchemaSyncService(
    HelpdeskDbContext db,
    ITenantContext tenantContext,
    IRequestFormSchemaParser schemaParser,
    IOrchestrationCatalogService orchestrationCatalogService,
    ICorrelationContext correlationContext) : IAutomationBindingSchemaSyncService
{
    private readonly HelpdeskDbContext _db = db;
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly IRequestFormSchemaParser _schemaParser = schemaParser;
    private readonly IOrchestrationCatalogService _stoCatalogService = orchestrationCatalogService;
    private readonly ICorrelationContext _correlationContext = correlationContext;

    public async Task<AutomationBindingDto> SyncAsync(string bindingId, CancellationToken cancellationToken = default)
    {
        var binding = await QueryForTenant()
            .FirstOrDefaultAsync(x => x.Id == bindingId, cancellationToken)
            ?? throw new InvalidOperationException("Automation binding was not found.");

        var requestForm = await _db.RequestForms.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == binding.RequestFormId, cancellationToken)
            ?? throw new InvalidOperationException("Request form was not found for the automation binding.");

        var schema = _schemaParser.Parse(requestForm.JsonSchema);
        var taskTemplate = schema.Tasks.FirstOrDefault(x => x.Id == binding.TaskTemplateId)
            ?? throw new InvalidOperationException("Task template was not found in the request form schema.");

        if (!string.Equals(taskTemplate.Type, "automation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only automation task templates can be synced to External orchestration.");
        }

        var relatedBindings = await QueryForTenant()
            .AsNoTracking()
            .Where(x => x.RequestFormId == binding.RequestFormId
                && x.Enabled
                && x.SystemConnectionId == binding.SystemConnectionId
                && x.OrchestrationRequestDefinitionId == binding.OrchestrationRequestDefinitionId)
            .ToListAsync(cancellationToken);
        var relatedTaskIds = relatedBindings
            .Select(x => x.TaskTemplateId)
            .ToHashSet();
        var relatedTasks = schema.Tasks
            .Where(x => relatedTaskIds.Contains(x.Id)
                && string.Equals(x.Type, "automation", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (relatedTasks.Count == 0)
        {
            relatedTasks.Add(taskTemplate);
        }

        var inputs = BuildDesiredInputs(requestForm.Title, schema.Fields, relatedTasks);
        var syncHash = AutomationBindingSchemaHash.Compute(inputs);
        var correlationId = _correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";

        if (inputs.Count == 0)
        {
            var definitions = await _stoCatalogService.ListRequestDefinitionsAsync(binding.OrganizationId, binding.SystemConnectionId, cancellationToken);
            if (!definitions.Any(x => string.Equals(x.RequestDefinitionId, binding.OrchestrationRequestDefinitionId, StringComparison.Ordinal)))
                throw new InvalidOperationException("The bound automation target is unavailable in the selected connection tenant.");
            await AutomationBindingSelectionFence.SaveAsync(_db, [binding], () =>
            {
                binding.LastSyncHash = syncHash;
                binding.LastSyncVersion = "schema:v1";
                binding.LastSyncedAtUtc = DateTimeOffset.UtcNow;
                binding.LastSyncDirection = "HelpdeskToOrchestration";
                binding.LastCorrelationId = correlationId;
                binding.SyncState = AutomationBindingSyncState.InSync;
                binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }, cancellationToken);
            return MapDto(binding, requestForm, taskTemplate);
        }

        try
        {
            var remote = await _stoCatalogService.SyncRequestDefinitionInputsAsync(
                binding.OrchestrationRequestDefinitionId,
                inputs,
                binding.OrganizationId,
                binding.SystemConnectionId,
                cancellationToken);

            await AutomationBindingSelectionFence.SaveAsync(_db, [binding], () =>
            {
                binding.OrchestrationRequestDefinitionName = FirstNonEmpty(remote.RequestDefinitionName, binding.OrchestrationRequestDefinitionName);
                binding.OrchestrationJobDefinitionId = FirstNonEmpty(remote.OrchestrationJobDefinitionId, binding.OrchestrationJobDefinitionId);
                binding.OrchestrationJobDefinitionName = FirstNonEmpty(remote.OrchestrationJobDefinitionName, binding.OrchestrationJobDefinitionName);
                binding.LastSyncHash = syncHash;
                binding.LastSyncVersion = "schema:v1";
                binding.LastSyncedAtUtc = DateTimeOffset.UtcNow;
                binding.LastSyncDirection = "HelpdeskToOrchestration";
                binding.LastCorrelationId = correlationId;
                binding.SyncState = AutomationBindingSyncState.InSync;
                binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not AutomationBindingSelectionChangedException)
        {
            await AutomationBindingSelectionFence.SaveAsync(_db, [binding], () =>
            {
                binding.SyncState = AutomationBindingSyncState.Broken;
                binding.LastCorrelationId = correlationId;
                binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }, cancellationToken);
            throw;
        }

        return MapDto(binding, requestForm, taskTemplate);
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

    private static List<OrchestrationCatalogInputDefinitionDto> BuildDesiredInputs(
        string requestFormTitle,
        IReadOnlyCollection<FormField> fields,
        RequestTaskTemplateModel taskTemplate)
        => BuildDesiredInputs(requestFormTitle, fields, [taskTemplate]);

    private static List<OrchestrationCatalogInputDefinitionDto> BuildDesiredInputs(
        string requestFormTitle,
        IReadOnlyCollection<FormField> fields,
        IReadOnlyCollection<RequestTaskTemplateModel> taskTemplates)
    {
        if (taskTemplates.All(x => x.PayloadMapping is null || x.PayloadMapping.Count == 0))
        {
            return [];
        }

        var fieldLookup = fields
            .Select((field, index) => new { Field = field, Index = index })
            .ToDictionary(
                x => FormFieldKeyResolver.Resolve(x.Field, x.Index),
                x => x.Field,
                StringComparer.OrdinalIgnoreCase);

        var inputs = new List<OrchestrationCatalogInputDefinitionDto>();
        var seenInputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var mapping in taskTemplates
            .OrderBy(x => x.Order)
            .SelectMany(x => x.PayloadMapping ?? new Dictionary<string, string>())
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var inputKey = mapping.Key?.Trim();
            var fieldKey = mapping.Value?.Trim();
            if (string.IsNullOrWhiteSpace(inputKey) || string.IsNullOrWhiteSpace(fieldKey))
            {
                continue;
            }

            if (!seenInputKeys.Add(inputKey))
            {
                continue;
            }

            if (!fieldLookup.TryGetValue(fieldKey, out var field))
            {
                throw new InvalidOperationException($"Payload mapping references missing field '{fieldKey}'.");
            }

            inputs.Add(new OrchestrationCatalogInputDefinitionDto
            {
                Key = inputKey,
                Label = string.IsNullOrWhiteSpace(field.Label) ? inputKey : field.Label.Trim(),
                Type = NormalizeFieldType(field.Type),
                Required = field.Required,
                HelpText = $"Helpdesk field '{fieldKey}' from request form '{requestFormTitle}'.",
                Order = order++
            });
        }

        return inputs;
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
            "select" => "select",
            "radio" => "radio",
            "date" => "date",
            "datetime" => "datetime",
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

    private static AutomationBindingDto MapDto(
        AutomationBinding binding,
        RequestForm requestForm,
        RequestTaskTemplateModel taskTemplate)
    {
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
}
