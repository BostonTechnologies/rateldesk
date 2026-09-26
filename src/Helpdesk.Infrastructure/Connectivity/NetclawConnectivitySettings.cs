namespace Helpdesk.Infrastructure.Persistence.Connectivity;

public sealed class NetclawConnectivitySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; }
    public string Instance { get; set; } = "dev";
    public string? Endpoint { get; set; }
    public string? ProtectedDeviceToken { get; set; }
    public bool AllowPrivateHttp { get; set; }
    public int IdleMinutes { get; set; } = 15;
    public int ConnectionCapacity { get; set; } = 25;
    public int TurnInactivityTimeoutSeconds { get; set; } = 300;
    public int ActivityHeartbeatIntervalSeconds { get; set; } = 15;
    public int Revision { get; set; } = 1;
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
    public DateTimeOffset? LastTestedAtUtc { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
