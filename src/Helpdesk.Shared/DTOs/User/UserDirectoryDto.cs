namespace Helpdesk.Shared.DTOs.User;

/// <summary>Bounded administration projection; the legacy user array remains compatible.</summary>
public sealed record UserDirectoryDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public IReadOnlyList<string> RoleKeys { get; init; } = [];
    public bool IsTestUser { get; init; }
    public string? OrganizationId { get; init; }
    public string? OrganizationName { get; init; }
    public string AccountState { get; init; } = "Profile";
    public string? AccountId { get; init; }
    public bool CanManageAccount { get; init; }
    public bool CanChangeSignIn { get; init; }
    public bool CanDelete { get; init; }
    public string? DeleteBlockedReason { get; init; }
}
