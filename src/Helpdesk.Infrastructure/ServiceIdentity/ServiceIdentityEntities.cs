using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.ServiceIdentity;

public sealed class ServicePrincipalRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ClientId { get; set; } = string.Empty;
    public string NormalizedClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string PeerInstanceId { get; set; } = string.Empty;
    public string PeerTenantId { get; set; } = string.Empty;
    public string AllowedScopesJson { get; set; } = "[]";
    public string CustomerIdsJson { get; set; } = "[]";
    public string ResourceConstraintsJson { get; set; } = "{}";
    public Guid? SourceInstanceId { get; set; }
    public Guid? SourceNamespaceId { get; set; }
    public string? LinkId { get; set; }
    public string? AttemptId { get; set; }
    public string? GrantHash { get; set; }
    public string? DescriptorHash { get; set; }
    public string? DirectionId { get; set; }
    public long LinkRevision { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public long Version { get; set; } = 1;
    public long CurrentCredentialRevision { get; set; } = 1;
    public string Status { get; set; } = "pending";
    public string Source { get; set; } = "database";
    public string? DeploymentFingerprint { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset? TerminalControlUntilUtc { get; set; }
}

/// <summary>Inbound storage contains salted verifiers only; protocol escrow is separate and finite.</summary>
public sealed class ServicePrincipalSecret
{
    public Guid ServicePrincipalId { get; set; }
    public long CredentialRevision { get; set; }
    public string SecretHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RetireAtUtc { get; set; }
}

public sealed class ServiceSigningKey
{
    public string Kid { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string ProtectedPrivateKey { get; set; } = string.Empty;
    public string PublicModulus { get; set; } = string.Empty;
    public string PublicExponent { get; set; } = string.Empty;
    public int? ActiveSlot { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ValidateUntilUtc { get; set; }
}

public static class ServiceIdentityModelConfiguration
{
    public static void ConfigureServiceIdentityModel(this ModelBuilder model)
    {
        model.Entity<ServicePrincipalRegistration>(e =>
        {
            e.ToTable("ServicePrincipalRegistrations", t => t.HasCheckConstraint("CK_ServicePrincipal_Status", "\"Status\" IN ('pending','prepared','verified','in_doubt','active','revoked','expired','failed')"));
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.NormalizedClientId).IsUnique();
            e.HasIndex(x => new { x.LinkId, x.DirectionId }).IsUnique().HasFilter("\"LinkId\" IS NOT NULL");
            foreach (var p in new[] { nameof(ServicePrincipalRegistration.ClientId), nameof(ServicePrincipalRegistration.NormalizedClientId), nameof(ServicePrincipalRegistration.Name), nameof(ServicePrincipalRegistration.PeerInstanceId), nameof(ServicePrincipalRegistration.PeerTenantId), nameof(ServicePrincipalRegistration.CreatedBy), nameof(ServicePrincipalRegistration.ApprovedBy) }) e.Property(p).HasMaxLength(256);
            e.Property(x => x.OrganizationId).HasMaxLength(64);
            e.Property(x => x.LinkId).HasMaxLength(256);
            e.Property(x => x.AttemptId).HasMaxLength(256);
            e.Property(x => x.GrantHash).HasMaxLength(64);
            e.Property(x => x.DescriptorHash).HasMaxLength(64);
            e.Property(x => x.DirectionId).HasMaxLength(64);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Helpdesk.Shared.Models.Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ServicePrincipalSecret>(e =>
        {
            e.ToTable("ServicePrincipalSecrets", t => t.HasCheckConstraint("CK_ServiceSecret_Status", "\"Status\" IN ('pending','active','retiring','revoked')"));
            e.HasKey(x => new { x.ServicePrincipalId, x.CredentialRevision });
            e.HasIndex(x => x.ServicePrincipalId).IsUnique().HasFilter("\"Status\" = 'pending'");
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.Salt).HasMaxLength(64);
            e.HasOne<ServicePrincipalRegistration>().WithMany().HasForeignKey(x => x.ServicePrincipalId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ServiceSigningKey>(e =>
        {
            e.ToTable("ServiceSigningKeys");
            e.HasKey(x => x.Kid);
            e.Property(x => x.Kid).HasMaxLength(64);
            e.Property(x => x.Issuer).HasMaxLength(2048);
            e.HasIndex(x => x.ActiveSlot).IsUnique().HasFilter("\"ActiveSlot\" IS NOT NULL");
        });
    }
}
