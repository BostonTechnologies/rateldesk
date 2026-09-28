using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.Connectivity;
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
        if (Instance != "dev" || string.IsNullOrWhiteSpace(DeviceToken) || IdleMinutes <= 0 || ConnectionCapacity <= 0 ||
            !AreSessionLimitsStorageCompatible(TurnInactivityTimeout, ActivityHeartbeatInterval))
            return false;

        try
        {
            return NetclawEndpointNormalizer.Normalize(Endpoint, nameof(Endpoint), AllowPrivateHttp) is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool AreSessionLimitsStorageCompatible(TimeSpan turnInactivityTimeout, TimeSpan activityHeartbeatInterval)
        => IsPositiveWholeSecondLimit(turnInactivityTimeout) &&
           IsPositiveWholeSecondLimit(activityHeartbeatInterval) &&
           activityHeartbeatInterval < turnInactivityTimeout;

    private static bool IsPositiveWholeSecondLimit(TimeSpan value)
        => value > TimeSpan.Zero &&
           value.Ticks % TimeSpan.TicksPerSecond == 0 &&
           value.Ticks / TimeSpan.TicksPerSecond <= int.MaxValue;
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
    private AiAssistantChatRuntimeSnapshot current = CreateInitialSnapshot(options.Value);

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

    private static AiAssistantChatRuntimeSnapshot CreateInitialSnapshot(AiAssistantChatOptions options)
    {
        string endpoint;
        try
        {
            endpoint = NetclawEndpointNormalizer.Normalize(options.Endpoint, nameof(options.Endpoint), options.AllowPrivateHttp) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            endpoint = string.Empty;
        }

        var valid = options.IsValid();
        return new AiAssistantChatRuntimeSnapshot(
            options.Enabled && valid,
            options.Instance,
            endpoint,
            options.DeviceToken,
            options.AllowPrivateHttp,
            options.IdleMinutes,
            options.ConnectionCapacity,
            options.TurnInactivityTimeout,
            options.ActivityHeartbeatInterval,
            IntegrationProviderSecretBinding.Fingerprint("Netclaw", options.Instance, endpoint),
            Source: "deployment",
            ManagedByDeployment: true,
            SourceKey: "deployment",
            CanAdoptLegacySessions: false);
    }
}
