// FILE: helpdesk.shared/DTOs/Service/ServiceItemDto.cs
using Helpdesk.Shared.Models;
using Helpdesk.Shared.DTOs.Request;

namespace Helpdesk.Shared.DTOs.Service;

public enum ServiceItemType { Service, RequestForm }

public class ServiceItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ServiceItemType ItemType { get; set; }
    public int AvailableRequestCount { get; set; }
    public List<string>? AllowedOrganizationIds { get; set; }
    public IReadOnlyList<string>? ParentOrganizationIds { get; set; }
    public IReadOnlyList<SelfServiceTargetOrganizationDto> EligibleOrganizations { get; set; } = [];
    public RequestFormReleaseStatus? ReleaseStatus { get; set; }
}
