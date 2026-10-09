using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Pairing;

public sealed class InstallationPairingCode
{
    public int Id { get; set; } = 1;
    public string Salt { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string? ConsumedOperationId { get; set; }
    public string? ConsumedPeerId { get; set; }
    public int FailedAttempts { get; set; }
    public long Revision { get; set; }
}
public sealed class PairingRedemption
{
    public string Id { get; set; } = "";
    public string PairId { get; set; } = "";
    public long PairGeneration { get; set; }
    public string OwnerId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string? ProtectedResponse { get; set; }
    public DateTimeOffset RetryUntilUtc { get; set; }
}
public sealed class SystemPair
{
    public string Id { get; set; } = "";
    public string PeerInstallationId { get; set; } = "";
    public string PeerJson { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Salt { get; set; } = "";
    public string InboundSecretHash { get; set; } = "";
    public string ProtectedInboundSecret { get; set; } = "";
    public string? ProtectedOutboundSecret { get; set; }
    public string State { get; set; } = "connecting";
    public long Generation { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public string? ConnectOperationId { get; set; }
    public string? ProtectedExchangeRequest { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed class SystemConnection
{
    public Guid Id { get; set; }
    public string PairId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string MappingJson { get; set; } = "";
    public string State { get; set; } = "draft";
    public long Revision { get; set; } = 1;
    public long PairGeneration { get; set; }
    public Guid? InboundPrincipalId { get; set; }
    public string? ProtectedInboundCredential { get; set; }
    public string? ProtectedOutboundCredential { get; set; }
    public string? SaveOperationId { get; set; }
    public string? SaveRequestHash { get; set; }
    public string? ProtectedSaveResponse { get; set; }
    public DateTimeOffset? LastTestedAtUtc { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public string? LastTestMessage { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed class PairingCleanup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProtectedRequest { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
}
public static class PairingPersistence
{
    public static void ConfigurePairingModel(this ModelBuilder model)
    {
        model.Entity<InstallationPairingCode>(e => { e.ToTable("InstallationPairingCodes"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.Revision).IsConcurrencyToken(); });
        model.Entity<PairingRedemption>(e => { e.ToTable("PairingRedemptions"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(64); e.HasIndex(x => x.RetryUntilUtc); e.HasIndex(x => x.PairId); });
        model.Entity<SystemPair>(e => { e.ToTable("SystemPairs"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(64); e.HasIndex(x => x.PeerInstallationId).IsUnique(); e.Property(x => x.Revision).IsConcurrencyToken(); });
        model.Entity<SystemConnection>(e => { e.ToTable("SystemConnections"); e.HasKey(x => x.Id); e.HasIndex(x => x.PairId); e.Property(x => x.Revision).IsConcurrencyToken(); });
        model.Entity<PairingCleanup>(e => { e.ToTable("PairingCleanups"); e.HasKey(x => x.Id); e.HasIndex(x => x.ExpiresAtUtc); });
    }
}
