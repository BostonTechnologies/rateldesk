using System.Text.Json.Serialization;
using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Organization;

public class OrganizationDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DnsName { get; set; }
    public bool EnableAiIntake { get; set; }
    public string? ContactInfo { get; set; }
    public string? ItSupportOrganizationId { get; set; }
    public string? SupportOrganizationName { get; set; }
    public int? OrchestrationTenantId { get; set; }
    public string? OrchestrationTenantName { get; set; }
    public DateTimeOffset? OrchestrationTenantLinkedAtUtc { get; set; }

    [JsonIgnore]
    public EntityState State { get; set; }

    public bool IsEnabled
    {
        get => State == EntityState.Enabled;
        set => State = value ? EntityState.Enabled : EntityState.Blocked;
    }
}
