using Dodo.Primitives;

namespace Helpdesk.Shared.Models;

public sealed class AutomationBinding
{
    public string Id { get; set; } = Uuid.CreateVersion7().ToString();
    public Guid? SystemConnectionId { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public string RequestFormId { get; set; } = string.Empty;
    public Guid TaskTemplateId { get; set; }
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
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
