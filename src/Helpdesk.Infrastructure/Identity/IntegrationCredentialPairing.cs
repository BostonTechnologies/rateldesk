namespace Helpdesk.Infrastructure.Identity;

/// <summary>Short-lived pairing state. Recoverable material is protected by the persisted API key ring.</summary>
public sealed class IntegrationCredentialPairing
{
    public Guid Id { get; set; }
    public string DeviceHash { get; set; } = "";
    public string HumanHash { get; set; } = "";
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string? McpResourceUri { get; set; }
    public string State { get; set; } = "pending";
    public long ExpiresAtUnixMilliseconds { get; set; }
    public long? DeliveryExpiresAtUnixMilliseconds { get; set; }
    public long? LastPollAtUnixMilliseconds { get; set; }
    public long Revision { get; set; }
    public string? OwnerUserId { get; set; }
    public string? ProtectedApproval { get; set; }
    public Guid? CredentialId { get; set; }
    public string? ProtectedDelivery { get; set; }
}

/// <summary>Provider-neutral, cross-process bounded rate counters; no raw IP addresses or human codes.</summary>
public sealed class IntegrationCredentialPairingRateLimit
{
    public string Key { get; set; } = "";
    public long WindowEndsAtUnixMilliseconds { get; set; }
    public int Attempts { get; set; }
    public long Revision { get; set; }
}
