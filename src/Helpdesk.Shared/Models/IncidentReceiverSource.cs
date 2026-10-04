namespace Helpdesk.Shared.Models;

/// <summary>Administrator-approved permanent namespace, independent of a rotatable credential.</summary>
public sealed class IncidentReceiverSource
{
    public Guid SourceNamespaceId { get; set; }
    public Guid SourceInstanceId { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    public string CreatedBy { get; set; } = string.Empty;
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>PrincipalKind is extensible to approved durable machine identities; v1 binds api_credential only.</summary>
public sealed class IncidentReceiverPrincipalBinding
{
    public Guid SourceNamespaceId { get; set; }
    public string PrincipalKind { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public sealed class IncidentReceiverSourceAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceNamespaceId { get; set; }
    public long Revision { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public DateTimeOffset AtUtc { get; set; }
    public string Action { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string PrincipalKind { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

/// <summary>The reservation and its immutable accepted result commit with the incident and ingress effects.</summary>
public sealed class IncidentCreateReceipt
{
    public Guid Id { get; set; }
    public Guid SourceNamespaceId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string IncidentId { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string AcceptedJson { get; set; } = string.Empty;
    public DateTimeOffset CommittedAtUtc { get; set; }
}
