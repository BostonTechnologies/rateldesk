using Helpdesk.Application.Orchestration;

namespace Helpdesk.Application.AiAssistant.Chat;

public sealed record AiAssistantChatRuntimeSnapshot(
    bool Enabled,
    string Instance,
    string Endpoint,
    string DeviceToken,
    bool AllowPrivateHttp,
    int IdleMinutes,
    int ConnectionCapacity,
    TimeSpan TurnInactivityTimeout,
    TimeSpan ActivityHeartbeatInterval,
    string ProfileFingerprint)
{
    public static AiAssistantChatRuntimeSnapshot From(NetclawResolvedSettings settings)
        => new(
            settings.Enabled,
            settings.Instance,
            settings.Endpoint ?? string.Empty,
            settings.DeviceToken ?? string.Empty,
            settings.AllowPrivateHttp,
            settings.IdleMinutes,
            settings.ConnectionCapacity,
            settings.TurnInactivityTimeout,
            settings.ActivityHeartbeatInterval,
            settings.ProfileFingerprint);
}
