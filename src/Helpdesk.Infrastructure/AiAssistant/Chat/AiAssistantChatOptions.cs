using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Application.AiAssistant.Chat;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

public sealed class AiAssistantChatOptions
{
    public bool Enabled { get; set; }
    public string Instance { get; set; } = "dev";
    public string Endpoint { get; set; } = string.Empty;
    public string DeviceToken { get; set; } = string.Empty;
    public bool AllowPrivateHttp { get; set; }
    public int IdleMinutes { get; set; } = 15;
    public int ConnectionCapacity { get; set; } = 25;
    public TimeSpan TurnInactivityTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ActivityHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public void Apply(AiAssistantChatOptions source)
    {
        Enabled = source.Enabled;
        Instance = source.Instance;
        Endpoint = source.Endpoint;
        DeviceToken = source.DeviceToken;
        AllowPrivateHttp = source.AllowPrivateHttp;
        IdleMinutes = source.IdleMinutes;
        ConnectionCapacity = source.ConnectionCapacity;
        TurnInactivityTimeout = source.TurnInactivityTimeout;
        ActivityHeartbeatInterval = source.ActivityHeartbeatInterval;
    }

    public bool IsValid()
    {
        if (!Enabled) return true;
        if (Instance != "dev" || string.IsNullOrWhiteSpace(DeviceToken) || IdleMinutes <= 0 || ConnectionCapacity <= 0 || TurnInactivityTimeout <= TimeSpan.Zero || ActivityHeartbeatInterval <= TimeSpan.Zero || ActivityHeartbeatInterval >= TurnInactivityTimeout || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/hub/session" || !IntegrationEndpointPolicy.IsAllowed(uri, AllowPrivateHttp)) return false;
        if (uri.Scheme == "https") return true;
        return AllowPrivateHttp && uri.Scheme == "http" &&
            System.Net.IPAddress.TryParse(uri.Host, out var ip) &&
            IntegrationEndpointPolicy.IsPrivateNetworkAddress(ip);
    }
}

public interface IAiAssistantChatRuntimeState
{
    AiAssistantChatRuntimeSnapshot Current { get; }
    bool CanApply(AiAssistantChatRuntimeSnapshot snapshot);
    bool TryPublish(AiAssistantChatRuntimeSnapshot snapshot);
    void Publish(AiAssistantChatRuntimeSnapshot snapshot);
}

public sealed class AiAssistantChatRuntimeState(IOptions<AiAssistantChatOptions> options) : IAiAssistantChatRuntimeState
{
    private readonly object gate = new();
    private AiAssistantChatRuntimeSnapshot current = new(
        options.Value.Enabled,
        options.Value.Instance,
        options.Value.Endpoint,
        options.Value.DeviceToken,
        options.Value.AllowPrivateHttp,
        options.Value.IdleMinutes,
        options.Value.ConnectionCapacity,
        options.Value.TurnInactivityTimeout,
        options.Value.ActivityHeartbeatInterval,
        IntegrationProviderSecretBinding.Fingerprint("Netclaw", options.Value.Instance, options.Value.Endpoint),
        Source: "deployment",
        ManagedByDeployment: true,
        SourceKey: "deployment",
        CanAdoptLegacySessions: !string.IsNullOrWhiteSpace(options.Value.Endpoint));

    public AiAssistantChatRuntimeSnapshot Current => Volatile.Read(ref current);

    public bool CanApply(AiAssistantChatRuntimeSnapshot snapshot)
    {
        lock (gate) return !IsStale(snapshot, current);
    }

    public bool TryPublish(AiAssistantChatRuntimeSnapshot snapshot)
    {
        lock (gate)
        {
            if (IsStale(snapshot, current)) return false;
            Volatile.Write(ref current, snapshot);
            return true;
        }
    }

    public void Publish(AiAssistantChatRuntimeSnapshot snapshot)
        => TryPublish(snapshot);

    private static bool IsStale(AiAssistantChatRuntimeSnapshot incoming, AiAssistantChatRuntimeSnapshot existing)
    {
        if (!string.Equals(incoming.RuntimeSourceKey, existing.RuntimeSourceKey, StringComparison.Ordinal) ||
            !string.Equals(incoming.Source, existing.Source, StringComparison.Ordinal))
            return false;

        if (incoming.Revision < existing.Revision) return true;
        return incoming.Revision == existing.Revision &&
            !string.Equals(incoming.ProfileFingerprint, existing.ProfileFingerprint, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(existing.ProfileFingerprint);
    }
}
