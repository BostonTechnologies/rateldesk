using Dodo.Primitives;
using Helpdesk.Shared.Enums;

namespace Helpdesk.Shared.Models;

public class Role
{
    public string Id { get; set; } = Uuid.CreateVersion7().ToString();
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public RoleScopeKind Scope { get; set; } = RoleScopeKind.Tenant;
    public string? OwnerOrganizationId { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool IsProtected { get; set; }
    public TicketAccessScope TicketAccessScope { get; set; }
    public ICollection<RoleSupportGroup> SupportGroups { get; set; } = new List<RoleSupportGroup>();
    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
}

public enum RoleScopeKind
{
    Instance,
    Tenant,
    OwnResource
}
