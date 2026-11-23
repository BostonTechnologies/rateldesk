namespace Helpdesk.Shared.DTOs.PublicAccess;

public enum PublicLinkPurpose
{
    Ticket,
    ChangeView,
    ChangeApproval,
    RequestApproval
}

public sealed record LegacyPublicLink(PublicLinkPurpose Purpose, string TrackingId, string? TaskId, string Email, string Token);
public sealed record PublicLinkExchangeRequest(string? Launch, LegacyPublicLink? Legacy, string BrowserKey);
public sealed record PublicLinkExchangeResponse(Guid AccessId, PublicLinkPurpose Purpose, string Grant, DateTimeOffset ExpiresAt);
public sealed record PublicApprovalDecision(bool Approve, string? Reason = null);

public static class PublicAccessProtocol
{
    public const string GrantHeader = "X-RatelDesk-Public-Grant";
    public const string ApiPrefix = "/api/v1/public/access";
    public const string BrowserPrefix = "/public/access";
}
