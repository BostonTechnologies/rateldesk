using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class AutomationBindingService(
    HelpdeskDbContext db,
    ITenantContext tenantContext,
    IRequestFormSchemaParser schemaParser, PairingConnectionResolver? connections = null) : IAutomationBindingService
{
    private readonly HelpdeskDbContext _db = db;
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly IRequestFormSchemaParser _schemaParser = schemaParser;

    public async Task<IReadOnlyList<AutomationBindingDto>> ListAsync(string? requestFormId = null, CancellationToken cancellationToken = default)
    {
        var query = _db.AutomationBindings.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(_tenantContext.TenantId))
        {
            query = query.Where(x => x.OrganizationId == _tenantContext.TenantId);
        }

        if (!string.IsNullOrWhiteSpace(requestFormId))
        {
            var normalizedRequestFormId = requestFormId.Trim();
            query = query.Where(x => x.RequestFormId == normalizedRequestFormId);
        }

        // SQLite cannot translate ordering by DateTimeOffset. Request-form ordering remains
        // server-side; creation-time ordering is applied after materialization so this service
        // works with both the supported SQLite and PostgreSQL deployments.
        var bindings = (await query
            .OrderBy(x => x.RequestFormId)
            .ToListAsync(cancellationToken))
            .OrderBy(x => x.RequestFormId, StringComparer.Ordinal)
            .ThenBy(x => x.CreatedAtUtc)
            .ToList();

        if (bindings.Count == 0)
        {
            return [];
        }

        var requestFormIds = bindings
            .Select(x => x.RequestFormId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var requestForms = await _db.RequestForms.AsNoTracking()
            .Where(x => requestFormIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return bindings
            .Select(x => MapDto(x, requestForms.TryGetValue(x.RequestFormId, out var form) ? form : null))
            .ToList();
    }

    public async Task<AutomationBindingDto?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var binding = await QueryForTenant()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (binding is null)
        {
            return null;
        }

        var form = await _db.RequestForms.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == binding.RequestFormId, cancellationToken);

        return MapDto(binding, form);
    }

    public async Task<AutomationBindingDto?> GetByTaskTemplateAsync(
        string requestFormId,
        Guid taskTemplateId,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequestFormId = NormalizeRequired(requestFormId, nameof(requestFormId));
        if (taskTemplateId == Guid.Empty)
        {
            throw new InvalidOperationException("TaskTemplateId is required.");
        }

        var binding = await QueryForTenant()
            .FirstOrDefaultAsync(
                x => x.RequestFormId == normalizedRequestFormId && x.TaskTemplateId == taskTemplateId,
                cancellationToken);
        if (binding is null)
        {
            return null;
        }

        var form = await _db.RequestForms.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == binding.RequestFormId, cancellationToken);

        return MapDto(binding, form);
    }

    public async Task<AutomationBindingDto> CreateAsync(CreateAutomationBindingDto dto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var requestFormId = NormalizeRequired(dto.RequestFormId, nameof(dto.RequestFormId));
        var orchestrationRequestDefinitionId = NormalizeRequired(dto.OrchestrationRequestDefinitionId, nameof(dto.OrchestrationRequestDefinitionId));
        if (dto.TaskTemplateId == Guid.Empty)
        {
            throw new InvalidOperationException("TaskTemplateId is required.");
        }

        var requestForm = await GetOwnedRequestFormAsync(requestFormId, cancellationToken);
        var tenantId = ResolveBindingOrganizationId(requestForm);
        var taskTemplate = GetAutomationTaskTemplate(requestForm, dto.TaskTemplateId);

        var existing = await _db.AutomationBindings
            .FirstOrDefaultAsync(
                x => x.OrganizationId == tenantId
                     && x.RequestFormId == requestFormId
                     && x.TaskTemplateId == dto.TaskTemplateId,
                cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("An automation binding already exists for this request form task template.");
        }

        var selectedConnection = connections is null ? null : await connections.ResolveAsync(tenantId, dto.SystemConnectionId, cancellationToken);
        if (connections is not null && selectedConnection?.Enabled != true) throw new InvalidOperationException("Save an authorized automation system connection before binding this task.");
        var binding = new AutomationBinding
        {
            SystemConnectionId = selectedConnection?.Pairing is { } selected ? Guid.Parse(selected.MappingId) : dto.SystemConnectionId,
            OrganizationId = tenantId,
            RequestFormId = requestFormId,
            TaskTemplateId = dto.TaskTemplateId,
            OrchestrationRequestDefinitionId = orchestrationRequestDefinitionId,
            OrchestrationRequestDefinitionName = Normalize(dto.OrchestrationRequestDefinitionName),
            OrchestrationJobDefinitionId = Normalize(dto.OrchestrationJobDefinitionId),
            OrchestrationJobDefinitionName = Normalize(dto.OrchestrationJobDefinitionName),
            SyncState = AutomationBindingSyncState.InSync,
            LastSyncDirection = "HelpdeskToOrchestration",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        _db.AutomationBindings.Add(binding);
        await _db.SaveChangesAsync(cancellationToken);

        return MapDto(binding, requestForm, taskTemplate);
    }

    public async Task<AutomationBindingDto?> UpdateAsync(string id, UpdateAutomationBindingDto dto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var binding = await QueryForTenant()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (binding is null)
        {
            return null;
        }

        binding.OrchestrationRequestDefinitionId = UpdateRequiredString(
            binding.OrchestrationRequestDefinitionId,
            dto.OrchestrationRequestDefinitionId,
            allowClear: false,
            propertyName: nameof(dto.OrchestrationRequestDefinitionId));
        binding.OrchestrationRequestDefinitionName = UpdateOptionalString(
            binding.OrchestrationRequestDefinitionName,
            dto.OrchestrationRequestDefinitionName,
            dto.ClearOrchestrationRequestDefinitionName);
        binding.OrchestrationJobDefinitionId = UpdateOptionalString(
            binding.OrchestrationJobDefinitionId,
            dto.OrchestrationJobDefinitionId,
            dto.ClearOrchestrationJobDefinitionId);
        binding.OrchestrationJobDefinitionName = UpdateOptionalString(
            binding.OrchestrationJobDefinitionName,
            dto.OrchestrationJobDefinitionName,
            dto.ClearOrchestrationJobDefinitionName);
        binding.LastSyncHash = UpdateOptionalString(
            binding.LastSyncHash,
            dto.LastSyncHash,
            dto.ClearLastSyncHash);
        binding.LastSyncVersion = UpdateOptionalString(
            binding.LastSyncVersion,
            dto.LastSyncVersion,
            dto.ClearLastSyncVersion);
        binding.LastSyncDirection = UpdateOptionalString(
            binding.LastSyncDirection,
            dto.LastSyncDirection,
            dto.ClearLastSyncDirection);
        binding.LastCorrelationId = UpdateOptionalString(
            binding.LastCorrelationId,
            dto.LastCorrelationId,
            dto.ClearLastCorrelationId);

        if (dto.SyncState.HasValue)
        {
            binding.SyncState = dto.SyncState.Value;
        }

        if (dto.LastSyncedAtUtc.HasValue)
        {
            binding.LastSyncedAtUtc = dto.LastSyncedAtUtc.Value;
        }
        else if (dto.ClearLastSyncedAtUtc is true)
        {
            binding.LastSyncedAtUtc = null;
        }

        if (dto.LastReviewedDriftAtUtc.HasValue)
        {
            binding.LastReviewedDriftAtUtc = dto.LastReviewedDriftAtUtc.Value;
        }
        else if (dto.ClearLastReviewedDriftAtUtc is true)
        {
            binding.LastReviewedDriftAtUtc = null;
        }

        if (dto.SystemConnectionId is { } connectionId)
        {
            var selected = connections is null ? null : await connections.ResolveAsync(binding.OrganizationId, connectionId, cancellationToken);
            if (connections is not null && selected?.Enabled != true) throw new InvalidOperationException("The selected system connection is unavailable for this organization.");
            binding.SystemConnectionId = connectionId;
        }

        if (dto.Enabled.HasValue)
        {
            binding.Enabled = dto.Enabled.Value;
        }

        binding.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        var requestForm = await _db.RequestForms.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == binding.RequestFormId, cancellationToken);

        return MapDto(binding, requestForm);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var binding = await QueryForTenant()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (binding is null)
        {
            return false;
        }

        _db.AutomationBindings.Remove(binding);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private IQueryable<AutomationBinding> QueryForTenant()
    {
        var query = _db.AutomationBindings.AsQueryable();
        if (!string.IsNullOrWhiteSpace(_tenantContext.TenantId))
        {
            query = query.Where(x => x.OrganizationId == _tenantContext.TenantId);
        }

        return query;
    }

    private async Task<RequestForm> GetOwnedRequestFormAsync(string requestFormId, CancellationToken cancellationToken)
    {
        var requestForm = await _db.RequestForms.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == requestFormId, cancellationToken);
        if (requestForm is null)
        {
            throw new InvalidOperationException("Request form was not found.");
        }

        if (!_tenantContext.IsHelpdeskAdmin
            && !string.IsNullOrWhiteSpace(_tenantContext.TenantId)
            && !string.IsNullOrWhiteSpace(requestForm.OrganizationId)
            && !string.Equals(requestForm.OrganizationId, _tenantContext.TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Request form does not belong to the current tenant.");
        }

        return requestForm;
    }

    private static string ResolveBindingOrganizationId(RequestForm requestForm)
    {
        if (!string.IsNullOrWhiteSpace(requestForm.OrganizationId))
        {
            return requestForm.OrganizationId.Trim();
        }

        if (requestForm.AllowedOrganizationIds.Count == 1
            && !string.IsNullOrWhiteSpace(requestForm.AllowedOrganizationIds[0]))
        {
            return requestForm.AllowedOrganizationIds[0].Trim();
        }

        return string.Empty;
    }

    private RequestTaskTemplateModel GetAutomationTaskTemplate(RequestForm requestForm, Guid taskTemplateId)
    {
        var schema = _schemaParser.Parse(requestForm.JsonSchema);
        var taskTemplate = schema.Tasks.FirstOrDefault(x => x.Id == taskTemplateId);
        if (taskTemplate is null)
        {
            throw new InvalidOperationException("Task template was not found in the request form schema.");
        }

        if (!string.Equals(taskTemplate.Type, "automation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only automation task templates can be bound to External orchestration.");
        }

        return taskTemplate;
    }

    private static AutomationBindingDto MapDto(
        AutomationBinding binding,
        RequestForm? requestForm,
        RequestTaskTemplateModel? taskTemplate = null)
    {
        taskTemplate ??= ResolveTaskTemplate(requestForm, binding.TaskTemplateId);

        return new AutomationBindingDto
        {
            Id = binding.Id,
            SystemConnectionId = binding.SystemConnectionId,
            OrganizationId = binding.OrganizationId,
            RequestFormId = binding.RequestFormId,
            RequestFormTitle = requestForm?.Title ?? string.Empty,
            TaskTemplateId = binding.TaskTemplateId,
            TaskTemplateName = taskTemplate?.Name ?? string.Empty,
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

    private static RequestTaskTemplateModel? ResolveTaskTemplate(RequestForm? requestForm, Guid taskTemplateId)
    {
        if (requestForm?.JsonSchema is null)
        {
            return null;
        }

        try
        {
            var schema = new RequestFormSchemaParser().Parse(requestForm.JsonSchema);
            return schema.Tasks.FirstOrDefault(x => x.Id == taskTemplateId);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeRequired(string? value, string propertyName)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{propertyName} is required.");
        }

        return normalized;
    }

    private static string UpdateRequiredString(string currentValue, string? incomingValue, bool allowClear, string propertyName)
    {
        if (incomingValue is null)
        {
            return currentValue;
        }

        var normalized = Normalize(incomingValue);
        if (string.IsNullOrWhiteSpace(normalized) && !allowClear)
        {
            throw new InvalidOperationException($"{propertyName} cannot be empty.");
        }

        return normalized ?? string.Empty;
    }

    private static string? UpdateOptionalString(string? currentValue, string? incomingValue, bool? clearRequested)
    {
        if (incomingValue is not null)
        {
            return Normalize(incomingValue);
        }

        if (clearRequested is true)
        {
            return null;
        }

        return currentValue;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
