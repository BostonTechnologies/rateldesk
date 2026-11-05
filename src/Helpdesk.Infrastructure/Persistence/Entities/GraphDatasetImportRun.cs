namespace Helpdesk.Infrastructure.Persistence.Entities;

/// <summary>Small durable receipt for one Graph import, including already committed source results.</summary>
public sealed class GraphDatasetImportRun
{
    public string OrganizationId { get; set; } = string.Empty;
    public Guid RequestId { get; set; }
    public string? ActiveOrganizationId { get; set; }
    public string ConnectionFingerprint { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public bool ConfigurationSaved { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public long StartedAtUtcTicks { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string SourceResultsJson { get; set; } = "[]";
    public string? Message { get; set; }
}
