using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class AutomationBindingDriftService(
    HelpdeskDbContext db,
    ITenantContext tenantContext,
    IOrchestrationCatalogService orchestrationCatalogService,
    IRequestFormSchemaParser schemaParser) : IAutomationBindingDriftService
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

        var remoteDefinitions = await _stoCatalogService.ListRequestDefinitionsAsync(binding.OrganizationId, binding.SystemConnectionId, cancellationToken);
        var remote = remoteDefinitions.FirstOrDefault(x =>
            string.Equals(x.RequestDefinitionId, binding.OrchestrationRequestDefinitionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The bound External orchestration request definition was not found.");

        return BuildPreview(binding, requestForm, remote);
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
        var fieldLookup = fields
            .Select((field, index) => new { Field = field, Key = FormFieldKeyResolver.Resolve(field, index) })
            .ToDictionary(x => x.Key, x => x.Field, StringComparer.OrdinalIgnoreCase);

        return (taskTemplate.PayloadMapping ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(mapping =>
            {
                fieldLookup.TryGetValue(mapping.Value, out var field);
                return new AutomationBindingInputSnapshotDto
                {
                    Key = mapping.Key,
                    FieldKey = mapping.Value,
                    Label = field?.Label ?? $"Missing field: {mapping.Value}",
                    Type = string.IsNullOrWhiteSpace(field?.Type)
                        ? (field is null ? "missing-field" : "text")
                        : NormalizeInputType(field.Type),
                    Required = field?.Required ?? false
                };
            })
            .ToList();
    }

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
        return string.Equals(helpdesk.Type, orchestration.Type, StringComparison.OrdinalIgnoreCase)
               && helpdesk.Required == orchestration.Required;
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
                Key = x.Key,
                FieldKey = null,
                Label = string.IsNullOrWhiteSpace(x.Label) ? x.Key : x.Label.Trim(),
                Type = NormalizeInputType(x.Type),
                Required = x.Required
            })
            .ToList();

        return new AutomationBindingDriftPreviewDto
        {
            BindingId = binding.Id,
            RequestFormId = binding.RequestFormId,
            TaskTemplateId = binding.TaskTemplateId,
            TaskTemplateName = taskTemplate.Name,
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
            var type => type
        };
    }
}
