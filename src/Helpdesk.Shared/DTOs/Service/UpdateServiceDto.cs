namespace Helpdesk.Shared.DTOs.Service;

public class UpdateServiceDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ParentId { get; set; }
    // Set this flag with a null ParentId to move the folder to the catalogue root.
    public bool ParentIdHasValue { get; set; }
    public List<string>? AllowedCustomerIds { get; set; }
    public List<string>? AllowedOrganizationIds { get; set; }
}

