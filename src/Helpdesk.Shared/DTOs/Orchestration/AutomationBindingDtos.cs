using System.ComponentModel.DataAnnotations;
using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Orchestration;

public sealed class AutomationBindingDto
{
    public Guid? SystemConnectionId { get; set; }
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string RequestFormId { get; set; } = string.Empty;
    public string RequestFormTitle { get; set; } = string.Empty;
    public Guid TaskTemplateId { get; set; }
    public string TaskTemplateName { get; set; } = string.Empty;
    public string OrchestrationRequestDefinitionId { get; set; } = string.Empty;
    public string? OrchestrationRequestDefinitionName { get; set; }
    public string? OrchestrationJobDefinitionId { get; set; }
    public string? OrchestrationJobDefinitionName { get; set; }
    public AutomationBindingSyncState SyncState { get; set; } = AutomationBindingSyncState.InSync;
    public string? LastSyncHash { get; set; }
    public string? LastSyncVersion { get; set; }
    public DateTimeOffset? LastSyncedAtUtc { get; set; }
    public string? LastSyncDirection { get; set; }
    public DateTimeOffset? LastReviewedDriftAtUtc { get; set; }
    public string? LastCorrelationId { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class CreateAutomationBindingDto
{
    public Guid? SystemConnectionId { get; set; }
    [Required]
    public string RequestFormId { get; set; } = string.Empty;

    [Required]
    public Guid TaskTemplateId { get; set; }

    [Required]
    public string OrchestrationRequestDefinitionId { get; set; } = string.Empty;

    public string? OrchestrationRequestDefinitionName { get; set; }
    public string? OrchestrationJobDefinitionId { get; set; }
    public string? OrchestrationJobDefinitionName { get; set; }
}

public sealed class UpdateAutomationBindingDto
{
    public Guid? SystemConnectionId { get; set; }
    public string? OrchestrationRequestDefinitionId { get; set; }
    public string? OrchestrationRequestDefinitionName { get; set; }
    public bool? ClearOrchestrationRequestDefinitionName { get; set; }
    public string? OrchestrationJobDefinitionId { get; set; }
    public bool? ClearOrchestrationJobDefinitionId { get; set; }
    public string? OrchestrationJobDefinitionName { get; set; }
    public bool? ClearOrchestrationJobDefinitionName { get; set; }
    public AutomationBindingSyncState? SyncState { get; set; }
    public string? LastSyncHash { get; set; }
    public bool? ClearLastSyncHash { get; set; }
    public string? LastSyncVersion { get; set; }
    public bool? ClearLastSyncVersion { get; set; }
    public DateTimeOffset? LastSyncedAtUtc { get; set; }
    public bool? ClearLastSyncedAtUtc { get; set; }
    public string? LastSyncDirection { get; set; }
    public bool? ClearLastSyncDirection { get; set; }
    public DateTimeOffset? LastReviewedDriftAtUtc { get; set; }
    public bool? ClearLastReviewedDriftAtUtc { get; set; }
    public string? LastCorrelationId { get; set; }
    public bool? ClearLastCorrelationId { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class AutomationBindingDriftPreviewDto
{
    public string BindingId { get; set; } = string.Empty;
    public string RequestFormId { get; set; } = string.Empty;
    public Guid TaskTemplateId { get; set; }
    public string TaskTemplateName { get; set; } = string.Empty;
    public AutomationBindingSyncState SyncState { get; set; } = AutomationBindingSyncState.InSync;
    public IReadOnlyList<AutomationBindingInputSnapshotDto> HelpdeskInputs { get; set; } = [];
    public IReadOnlyList<AutomationBindingInputSnapshotDto> OrchestrationInputs { get; set; } = [];
    public IReadOnlyList<AutomationBindingInputDiffDto> Differences { get; set; } = [];
}

public sealed class AutomationBindingInputSnapshotDto
{
    public string Key { get; set; } = string.Empty;
    public string? FieldKey { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
}

public sealed class AutomationBindingInputDiffDto
{
    public string Key { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public AutomationBindingInputSnapshotDto? Helpdesk { get; set; }
    public AutomationBindingInputSnapshotDto? Orchestration { get; set; }
}
