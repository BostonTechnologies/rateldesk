namespace Helpdesk.Infrastructure.Persistence.Connectivity;

public class M2MConnectivitySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderKey { get; set; } = "Orchestrator";
    public bool Enabled { get; set; } = false; // default disabled
    public string? RemoteBaseUrl { get; set; }
    public string? RemoteAudience { get; set; }
    public string? RemoteSystemName { get; set; }
    public string? RemoteTokenEndpoint { get; set; }
    public string? RemoteAuthority { get; set; }
    public string? RemoteScope { get; set; }
    public string? ClientId { get; set; }
    public string? ProtectedClientSecret { get; set; }
    public string? SecretBindingFingerprint { get; set; }
    public int? SecretBindingRevision { get; set; }
    public string? ProfileFingerprint { get; set; }
    public string? PeerInstanceId { get; set; }
    public string? PeerTenantId { get; set; }
    public string? LocalTenantId { get; set; }
    public string? LinkId { get; set; }
    public long LinkRevision { get; set; }
    public long CredentialRevision { get; set; }
    public string? GrantHash { get; set; }
    public string? SourceInstanceId { get; set; }
    public string? SourceNamespaceId { get; set; }
    public string? DirectionId { get; set; }
    public bool ManagedSenderEnabled { get; set; } = true;
    public bool AllowPrivateHttp { get; set; }
    public string HealthPath { get; set; } = "/internal/health";
    public string IngestPath { get; set; } = "/internal/ingest";
    public string CatalogPath { get; set; } = "/internal/catalog";
    public int Revision { get; set; } = 1;
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
    public DateTimeOffset? LastTestedAtUtc { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
