using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Identity;

public sealed class RatelDeskIdentityDbContext(DbContextOptions<RatelDeskIdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole, string>(options)
{
    public DbSet<IntegrationCredential> IntegrationCredentials => Set<IntegrationCredential>();
    public DbSet<IntegrationCredentialSetupAttempt> IntegrationCredentialSetupAttempts => Set<IntegrationCredentialSetupAttempt>();

    public DbSet<IntegrationCredentialPairing> IntegrationCredentialPairings => Set<IntegrationCredentialPairing>();
    public DbSet<IntegrationCredentialPairingRateLimit> IntegrationCredentialPairingRateLimits => Set<IntegrationCredentialPairingRateLimit>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<IntegrationCredentialSetupAttempt>(entity =>
        {
            entity.HasKey(x => x.RequestId);
            entity.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => x.CredentialId).IsUnique();
            entity.HasIndex(x => new { x.State, x.DeliveryExpiresAtUnixMilliseconds }).HasDatabaseName("IX_IntegrationSetup_State_Deadline");
        });

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.DisplayName).HasMaxLength(256);
            entity.Property(user => user.IsEnabled).HasDefaultValue(true);
            entity.Property(user => user.IsInstanceAdministrator).HasDefaultValue(false);
            entity.Property(user => user.AuthorizationRevision).HasDefaultValue(0L);
            entity.HasIndex(user => user.IsEnabled);
        });

        builder.Entity<IntegrationCredentialPairing>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.DeviceHash).HasMaxLength(64).IsRequired();
            entity.Property(p => p.HumanHash).HasMaxLength(64).IsRequired();
            entity.Property(p => p.Name).HasMaxLength(128).IsRequired();
            entity.Property(p => p.Purpose).HasMaxLength(16).IsRequired();
            entity.Property(p => p.McpResourceUri).HasMaxLength(2048);
            entity.Property(p => p.State).HasMaxLength(32).IsRequired();
            entity.Property(p => p.OwnerUserId).HasMaxLength(450);
            entity.Property(p => p.Revision).IsConcurrencyToken();
            entity.HasIndex(p => p.DeviceHash).IsUnique();
            entity.HasIndex(p => p.HumanHash).IsUnique();
            entity.HasIndex(p => p.ExpiresAtUnixMilliseconds);
        });
        builder.Entity<IntegrationCredentialPairingRateLimit>(entity =>
        {
            entity.HasKey(p => p.Key);
            entity.Property(p => p.Key).HasMaxLength(64);
            entity.Property(p => p.Revision).IsConcurrencyToken();
            entity.HasIndex(p => p.WindowEndsAtUnixMilliseconds);
        });

        builder.Entity<IntegrationCredential>(entity =>
        {
            entity.ToTable("IntegrationCredentials");
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(credential => credential.Name).HasMaxLength(128).IsRequired();
            entity.Property(credential => credential.Prefix).HasMaxLength(32).IsRequired();
            entity.Property(credential => credential.SecretHash).HasMaxLength(128).IsRequired();
            entity.Property(credential => credential.Purpose).HasMaxLength(16).IsRequired();
            entity.Property(credential => credential.McpResourceUri).HasMaxLength(2048);
            entity.Property(credential => credential.OrganizationId).HasMaxLength(128);
            entity.Property(credential => credential.Permissions).HasMaxLength(4096).IsRequired();
            entity.HasIndex(credential => credential.OwnerUserId);
            entity.HasIndex(credential => new { credential.OwnerUserId, credential.CreatedAtUnixMilliseconds, credential.Id });
            entity.HasIndex(credential => new { credential.Purpose, credential.ExpiresAtUtc, credential.RevokedAtUtc });
        });
    }
}

