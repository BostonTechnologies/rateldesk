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
    void Publish(AiAssistantChatRuntimeSnapshot snapshot);
}

public sealed class AiAssistantChatRuntimeState(IOptions<AiAssistantChatOptions> options) : IAiAssistantChatRuntimeState
{
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
        string.Empty);

    public AiAssistantChatRuntimeSnapshot Current => Volatile.Read(ref current);

    public void Publish(AiAssistantChatRuntimeSnapshot snapshot)
        => Volatile.Write(ref current, snapshot);
}
