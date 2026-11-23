using Helpdesk.Shared.DTOs.PublicAccess;

namespace Helpdesk.Infrastructure.Security;

public sealed class PublicLinkCapability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LaunchDigest { get; set; } = string.Empty;
    public PublicLinkPurpose Purpose { get; set; }
    public string TicketId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string? TaskId { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public long ExpiresAt { get; set; }
    public bool Legacy { get; set; }
    public string? BrowserDigest { get; set; }
    public long? GrantExpiresAt { get; set; }
    public long? RevokedAt { get; set; }
}

public sealed class PublicLinkCompatibility
{
    public int Id { get; set; }
    public long LegacyCutoffUnix { get; set; }
}
