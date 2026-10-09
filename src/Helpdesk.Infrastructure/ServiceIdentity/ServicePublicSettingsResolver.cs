using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.ServiceIdentity;

public sealed class ServiceIdentityConfiguration
{
    public int Id { get; set; } = 1;
    public long Revision { get; set; }
    public bool Enabled { get; set; }
    public string WebBaseUrl { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "rateldesk.services";
    public string InstanceId { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed record ServicePublicSettingsEffective(ServiceIdentityOptions Identity, long Revision, string[] LockedFields);
public interface IServicePublicSettingsResolver
{
    Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default);
}
/// <summary>Installation identity and ordinary deployment origins automatically enable paired connections.</summary>
public sealed class ServicePublicSettingsResolver(HelpdeskDbContext db, IConfiguration configuration,
    IOptionsMonitor<ServiceIdentityOptions> options) : IServicePublicSettingsResolver
{
    public async Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default)
    {
        var stored = await db.Set<ServiceIdentityConfiguration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        var configured = options.CurrentValue;
        var branding = await db.Set<InstanceBranding>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        var installation = await db.InstanceInitializations.AsNoTracking().Where(x => x.Id == InstanceInitialization.SingletonId).Select(x => (Guid?)x.InstanceId).SingleOrDefaultAsync(ct);
        var web = configured.WebBaseUrl is { Length: > 0 } ? configured.WebBaseUrl : stored?.WebBaseUrl is { Length: > 0 } ? stored.WebBaseUrl : branding?.ApplicationUrl ?? configuration["Branding:ApplicationUrl"] ?? configuration["PublicWebAppUrl"] ?? "";
        var api = configured.ApiBaseUrl is { Length: > 0 } ? configured.ApiBaseUrl : configuration["StorageOptions:PublicApiBaseUrl"] is { Length: > 0 } publicApi ? publicApi : stored?.ApiBaseUrl is { Length: > 0 } ? stored.ApiBaseUrl : configuration["PublicApiBaseUrl"] ?? "";
        web = PairingTransport.Origin(web); api = PairingTransport.Origin(api);
        var publishedId = configured.InstanceId is { Length: > 0 } ? configured.InstanceId : stored?.InstanceId is { Length: > 0 } ? stored.InstanceId : installation?.ToString("D");
        if (!Guid.TryParse(publishedId, out var persistentId) || persistentId == Guid.Empty) throw new PairingFailure("installation_unavailable", "Restore the persistent installation GUID or complete normal installation bootstrap before pairing.", 503);
        var existingPairs = await db.Set<SystemPair>().AsNoTracking().Where(x => x.State != "deleted").Select(x => new { x.Id, x.PeerInstallationId }).ToListAsync(ct);
        if (existingPairs.Any(x => x.Id != SystemPairingService.PairId(persistentId.ToString("D"), x.PeerInstallationId))) throw new PairingFailure("installation_identity_changed", "The configured installation GUID differs from the saved system pairing identity. Restore the original persistent GUID.", 409);
        var signingIssuer = await db.Set<ServiceSigningKey>().AsNoTracking().Where(x => x.ActiveSlot == 1).Select(x => x.Issuer).SingleOrDefaultAsync(ct);
        var issuer = signingIssuer ?? (configured.Issuer.Length > 0 ? configured.Issuer : stored?.Issuer is { Length: > 0 } ? stored.Issuer : api + "/services");
        var identity = new ServiceIdentityOptions { Enabled = true, WebBaseUrl = web, ApiBaseUrl = api, InstanceId = persistentId.ToString("D"), Issuer = issuer,
            Audience = configuration["ServiceIdentity:Audience"] is { Length: > 0 } ? configured.Audience : stored?.Audience is { Length: > 0 } ? stored.Audience : configured.Audience, AllowPrivateHttp = true, AccessTokenLifetimeSeconds = configured.AccessTokenLifetimeSeconds,
            ClockSkewSeconds = configured.ClockSkewSeconds, CredentialMaximumAgeDays = configured.CredentialMaximumAgeDays };
        if (stored?.InstanceId != identity.InstanceId)
        {
            var persisted = await db.Set<ServiceIdentityConfiguration>().SingleOrDefaultAsync(x => x.Id == 1, ct);
            if (persisted is null) { persisted = new(); db.Add(persisted); }
            persisted.InstanceId = identity.InstanceId; persisted.WebBaseUrl = web; persisted.ApiBaseUrl = api; persisted.Issuer = issuer; persisted.Audience = identity.Audience;
            persisted.Enabled = true; persisted.Revision++; persisted.UpdatedBy = "pairing-installation"; persisted.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return new(identity, stored?.Revision ?? 0, []);
    }
}
