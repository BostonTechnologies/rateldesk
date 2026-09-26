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
    string ProfileFingerprint,
    int Revision = 0,
    string Source = "deployment",
    bool ManagedByDeployment = true,
    string? SourceKey = null,
    bool CanAdoptLegacySessions = false)
{
    public string RuntimeSourceKey => SourceKey ?? Source;

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
            settings.ProfileFingerprint,
            settings.Revision,
            settings.Source,
            settings.ManagedByDeployment,
            settings.SourceKey,
            settings.CanAdoptLegacySessions);
}
