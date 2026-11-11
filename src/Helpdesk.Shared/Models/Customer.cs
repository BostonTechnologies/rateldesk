using Dodo.Primitives;

namespace Helpdesk.Shared.Models;

public class Customer
{
    public string Id { get; set; } = Uuid.CreateVersion7().ToString();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? IdentityUserId { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public EntityState State { get; set; } = EntityState.Enabled;

    public bool IsEnabled
    {
        get => State == EntityState.Enabled;
        set => State = value ? EntityState.Enabled : EntityState.Blocked;
    }
}
