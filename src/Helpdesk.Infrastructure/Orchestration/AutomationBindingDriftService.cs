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

public sealed partial class AutomationBindingDriftService(
    HelpdeskDbContext db,
    ITenantContext tenantContext,
    IOrchestrationCatalogService orchestrationCatalogService,
    IRequestFormSchemaParser schemaParser, ICorrelationContext? correlation = null) : IAutomationBindingDriftService
{
    private readonly HelpdeskDbContext _db = db;
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly IOrchestrationCatalogService _stoCatalogService = orchestrationCatalogService;
    private readonly IRequestFormSchemaParser _schemaParser = schemaParser;

    public async Task<IReadOnlyList<AutomationBindingDto>> RefreshAsync(string? requestFormId = null, CancellationToken cancellationToken = default)
    {
        var bindingsQuery = QueryForTenant();
        if (!string.IsNullOrWhiteSpace(requestFormId))
        {
            var normalizedRequestFormId = requestFormId.Trim();
            bindingsQuery = bindingsQuery.Where(x => x.RequestFormId == normalizedRequestFormId);
        }

        var bindings = await bindingsQuery
            .OrderBy(x => x.RequestFormId)
            .ThenBy(x => x.TaskTemplateId)
            .ToListAsync(cancellationToken);
        if (bindings.Count == 0)
        {
            return [];
        }

        var catalogs = new Dictionary<(string OrganizationId, Guid? ConnectionId), Dictionary<string, OrchestrationCatalogRequestDefinitionDto>>();
        foreach (var selection in bindings.Where(x => x.Enabled).Select(x => (x.OrganizationId, x.SystemConnectionId)).Distinct())
        {
            var definitions = await _stoCatalogService.ListRequestDefinitionsAsync(selection.OrganizationId, selection.SystemConnectionId, cancellationToken);
            catalogs.Add(selection, definitions.ToDictionary(x => x.RequestDefinitionId, StringComparer.OrdinalIgnoreCase));
        }
        var requestFormIds = bindings.Select(x => x.RequestFormId).Distinct(StringComparer.Ordinal).ToList();
        var requestForms = await _db.RequestForms.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => requestFormIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        await AutomationBindingSelectionFence.SaveAsync(_db, bindings.Where(x => x.Enabled), () =>
        {
            foreach (var binding in bindings)
            {
                if (!binding.Enabled)
                {
                    continue;
                }

                if (!catalogs[(binding.OrganizationId, binding.SystemConnectionId)].TryGetValue(binding.OrchestrationRequestDefinitionId, out var remote))
                {
                    binding.SyncState = AutomationBindingSyncState.Broken;
                    binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    continue;
                }

                if (!requestForms.TryGetValue(binding.RequestFormId, out var requestForm))
                {
                    binding.SyncState = AutomationBindingSyncState.Broken;
                    binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    continue;
                }

                var preview = BuildPreview(binding, requestForm, remote);
                var hasDiffs = preview.Differences.Any(x => !string.Equals(x.ChangeType, "Unchanged", StringComparison.Ordinal));
                var remoteHash = AutomationBindingSchemaHash.Compute(remote.Inputs.ToList());

                if (!hasDiffs)
                {
                    if (binding.SyncState != AutomationBindingSyncState.ImportPending)
                    {
                        binding.SyncState = AutomationBindingSyncState.InSync;
                    }

                    binding.LastSyncHash = remoteHash;
                    binding.LastSyncVersion ??= "schema:v1";
                }
                else if (binding.SyncState != AutomationBindingSyncState.ImportPending)
                {
                    binding.SyncState = AutomationBindingSyncState.Drifted;
                }

                binding.OrchestrationRequestDefinitionName = FirstNonEmpty(remote.RequestDefinitionName, binding.OrchestrationRequestDefinitionName);
                binding.OrchestrationJobDefinitionId = FirstNonEmpty(remote.OrchestrationJobDefinitionId, binding.OrchestrationJobDefinitionId);
                binding.OrchestrationJobDefinitionName = FirstNonEmpty(remote.OrchestrationJobDefinitionName, binding.OrchestrationJobDefinitionName);
                binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }, cancellationToken);

        return bindings
            .Select(x => MapDto(x, requestForms.TryGetValue(x.RequestFormId, out var form) ? form : null))
            .ToList();
    }

    public async Task<AutomationBindingDto> MarkImportPendingAsync(string bindingId, CancellationToken cancellationToken = default)
    {
        var binding = await QueryForTenant()
            .FirstOrDefaultAsync(x => x.Id == bindingId, cancellationToken)
            ?? throw new InvalidOperationException("Automation binding was not found.");

        binding.SyncState = AutomationBindingSyncState.ImportPending;
        binding.LastReviewedDriftAtUtc = DateTimeOffset.UtcNow;
        binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var requestForm = await _db.RequestForms.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == binding.RequestFormId, cancellationToken);

        return MapDto(binding, requestForm);
    }

    public async Task<AutomationBindingDriftPreviewDto> PreviewAsync(string bindingId, CancellationToken cancellationToken = default)
    {
        var binding = await QueryForTenant()
            .FirstOrDefaultAsync(x => x.Id == bindingId, cancellationToken)
            ?? throw new InvalidOperationException("Automation binding was not found.");

        var requestForm = await _db.RequestForms.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == binding.RequestFormId, cancellationToken)
            ?? throw new InvalidOperationException("Request form was not found for the automation binding.");

        var revision = await ConnectionRevisionAsync(binding, cancellationToken);
        var remote = await ReadReviewTargetAsync(binding, cancellationToken);
        var preview = BuildPreview(binding, requestForm, remote);
        if (binding.Enabled && revision is not null && revision == await ConnectionRevisionAsync(binding, cancellationToken))
            preview.ReviewVersion = ReviewVersion(binding, requestForm, remote, revision.Value);
        return preview;
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

    private AutomationBindingDto MapDto(AutomationBinding binding, RequestForm? requestForm)
    {
        var taskTemplateName = string.Empty;
        if (requestForm is not null)
        {
            try
            {
                var schema = _schemaParser.Parse(requestForm.JsonSchema);
                taskTemplateName = schema.Tasks.FirstOrDefault(x => x.Id == binding.TaskTemplateId)?.Name ?? string.Empty;
            }
            catch
            {
            }
        }

        return new AutomationBindingDto
        {
            Id = binding.Id,
            SystemConnectionId = binding.SystemConnectionId,
            OrganizationId = binding.OrganizationId,
            RequestFormId = binding.RequestFormId,
            RequestFormTitle = requestForm?.Title ?? string.Empty,
            TaskTemplateId = binding.TaskTemplateId,
            TaskTemplateName = taskTemplateName,
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

    private static string? FirstNonEmpty(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    private static List<AutomationBindingInputSnapshotDto> BuildHelpdeskSnapshots(
        IReadOnlyList<FormField> fields,
        RequestTaskTemplateModel taskTemplate)
    {
        var fieldLookup = fields.Select((field, index) => (Field: field, Key: FormFieldKeyResolver.Resolve(field, index)))
            .ToDictionary(pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        return (taskTemplate.PayloadMapping ?? []).Select(mapping =>
        {
            var key = mapping.Key.Trim();
            var fieldKey = mapping.Value.Trim();
            return fieldLookup.TryGetValue(fieldKey, out var field)
                ? FieldSnapshot(field.Field, field.Key, key)
                : new AutomationBindingInputSnapshotDto
                {
                    Key = key, FieldKey = fieldKey, Label = $"Missing field: {fieldKey}", Type = "missing-field"
                };
        }).ToList();
    }

    private static AutomationBindingInputSnapshotDto FieldSnapshot(FormField field, string key, string? inputKey = null) => new()
    {
        Key = inputKey ?? key, FieldKey = key, Label = string.IsNullOrWhiteSpace(field.Label) ? inputKey ?? key : field.Label.Trim(),
        Type = field.MultiSelect ? "text" : AutomationBindingSchemaSyncService.NormalizeFieldType(field.Type),
        PresentationType = field.Type, Required = field.Required, MultiSelect = field.MultiSelect,
        Choices = field.PredefinedOptions, UsesDataset = field.DataBinding is not null
    };

    private static List<AutomationBindingInputDiffDto> BuildDiffs(
        IReadOnlyList<AutomationBindingInputSnapshotDto> helpdeskInputs,
        IReadOnlyList<AutomationBindingInputSnapshotDto> orchestrationInputs)
    {
        var helpdeskByKey = helpdeskInputs.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var orchestrationByKey = orchestrationInputs.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var keys = helpdeskByKey.Keys
            .Union(orchestrationByKey.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        var diffs = new List<AutomationBindingInputDiffDto>();
        foreach (var key in keys)
        {
            helpdeskByKey.TryGetValue(key, out var helpdesk);
            orchestrationByKey.TryGetValue(key, out var orchestration);

            var changeType = helpdesk is null
                ? "AddedInOrchestration"
                : orchestration is null
                    ? "MissingInOrchestration"
                    : SnapshotsEqual(helpdesk, orchestration)
                        ? "Unchanged"
                        : "Modified";

            diffs.Add(new AutomationBindingInputDiffDto
            {
                Key = key,
                ChangeType = changeType,
                Helpdesk = helpdesk,
                Orchestration = orchestration
            });
        }

        return diffs;
    }

    private static bool SnapshotsEqual(AutomationBindingInputSnapshotDto helpdesk, AutomationBindingInputSnapshotDto orchestration)
    {
        return CompatibleTypes(helpdesk, orchestration)
               && helpdesk.Required == orchestration.Required
               && string.Equals(helpdesk.Label, orchestration.Label, StringComparison.Ordinal)
               && (orchestration.Type != "select" || helpdesk.Choices.SequenceEqual(orchestration.Choices));
    }

    private AutomationBindingDriftPreviewDto BuildPreview(
        AutomationBinding binding,
        RequestForm requestForm,
        OrchestrationCatalogRequestDefinitionDto remote)
    {
        var schema = _schemaParser.Parse(requestForm.JsonSchema);
        var taskTemplate = schema.Tasks.FirstOrDefault(x => x.Id == binding.TaskTemplateId)
            ?? throw new InvalidOperationException("Task template was not found in the request form schema.");

        var helpdeskInputs = BuildHelpdeskSnapshots(schema.Fields, taskTemplate);
        var orchestrationInputs = remote.Inputs
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new AutomationBindingInputSnapshotDto
            {
                Key = x.Key.Trim(),
                FieldKey = null,
                Label = string.IsNullOrWhiteSpace(x.Label) ? x.Key : x.Label.Trim(),
                Type = NormalizeInputType(x.Type),
                Choices = NormalizeInputType(x.Type) == "select" ? ReadChoiceValues(x.OptionsJson) : [],
                Required = x.Required
            })
            .ToList();

        return new AutomationBindingDriftPreviewDto
        {
            BindingId = binding.Id,
            RequestFormId = binding.RequestFormId,
            TaskTemplateId = binding.TaskTemplateId,
            TaskTemplateName = taskTemplate.Name,
            OrganizationId = binding.OrganizationId,
            ConnectionId = binding.SystemConnectionId,
            JobName = FirstNonEmpty(remote.OrchestrationJobDefinitionName, remote.RequestDefinitionName),
            AvailableFields = schema.Fields.Select((field, index) => FieldSnapshot(field, FormFieldKeyResolver.Resolve(field, index))).ToList(),
            SyncState = binding.SyncState,
            HelpdeskInputs = helpdeskInputs,
            OrchestrationInputs = orchestrationInputs,
            Differences = BuildDiffs(helpdeskInputs, orchestrationInputs)
        };
    }

    private static string NormalizeInputType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "text";
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "helpdeskdata" or "string" => "text",
            "bool" => "boolean",
            var type => type
        };
    }
}
