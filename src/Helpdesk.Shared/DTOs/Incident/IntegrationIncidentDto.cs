namespace Helpdesk.Shared.DTOs.Incident;

/// <summary>Additive keyed-create response; ordinary IncidentDto serialization is unchanged.</summary>
public sealed class IntegrationIncidentDto : IncidentDto
{
    public required IncidentIntegrationReceipt IntegrationReceipt { get; set; }
}

public sealed record IncidentIntegrationReceipt(
    string ContractVersion, string ReceiverInstanceId, string SourceNamespaceId, string SourceInstanceId,
    string Key, string Fingerprint, string Outcome, string IncidentId, string TrackingId,
    string OrganizationId, string CustomerId, DateTimeOffset CommittedAtUtc, string Location);

public sealed class ValidateIncidentTargetDto
{
    public string? OrganizationId { get; set; }
    public string? CustomerId { get; set; }
    public string? AssignedToId { get; set; }
    public List<Guid>? CategoryIds { get; set; }
}
