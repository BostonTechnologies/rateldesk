using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.Pairing;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Pairing;

public interface IPairingBusinessAuthority
{
    Task RequireCurrentAsync(OrchestrationResolvedSettings expected, CancellationToken ct);
}

public sealed class PairingConnectionResolver(HelpdeskDbContext db, IntegrationProviderSecretProtector secrets, PairingAuthority authority, ITenantContext tenant) : IPairingBusinessAuthority
{
    public async Task<OrchestrationResolvedSettings> ResolveAsync(string? organizationId = null, Guid? connectionId = null, CancellationToken ct = default)
    {
        organizationId ??= tenant.TenantId;
        var rows = await db.Set<SystemConnection>().AsNoTracking().Where(x => x.State == "connected" && (connectionId == null || x.Id == connectionId)).ToListAsync(ct);
        var selected = rows.Where(x => { var mapping = SystemPairingService.Read<PairingMapping>(x.MappingJson); return mapping.RunAutomation && (organizationId is null || mapping.RatelDeskOrganizationId == organizationId); }).ToArray();
        if (selected.Length == 0) return new() { Source = "pairing", Enabled = false };
        if (selected.Length != 1) throw new PairingFailure("connection_selection_required", "Select the named system connection on the automation binding for this organization.", 409);
        var row = selected[0]; var mapping = SystemPairingService.Read<PairingMapping>(row.MappingJson);
        if (!await authority.BusinessUsableAsync(row.Id, "rateldesk.orchestration.callback", ct) || row.ProtectedOutboundCredential is null) return new() { Source = "pairing", Enabled = false };
        var pair = await db.Set<SystemPair>().AsNoTracking().SingleAsync(x => x.Id == row.PairId, ct);
        var peer = SystemPairingService.Read<PairingMetadata>(pair.PeerJson);
        var credential = SystemPairingService.Read<PairingBusinessCredential>(secrets.Unprotect(row.ProtectedOutboundCredential));
        return new() { ProviderKey = "NetRatel", Source = "pairing", Enabled = true, BaseUrl = peer.ApiOrigin, Authority = credential.Issuer,
            Audience = credential.Audience, TokenEndpoint = peer.ApiOrigin + "/connect/token", Scope = string.Join(' ', credential.Scopes.Order(StringComparer.Ordinal)),
            ClientId = credential.ClientId, ClientSecret = credential.ClientSecret, HasClientSecret = true, AllowPrivateHttp = true,
            RemoteSystemName = peer.Name, HealthPath = "/internal/health", IngestPath = "/internal/ingest", CatalogPath = "/internal/catalog",
            Revision = checked((int)row.Revision), SourceKey = row.Id.ToString("D"), ProfileFingerprint = SystemPairingService.Hash(row.MappingJson + ":" + row.Revision),
            Pairing = new(mapping.RatelDeskOrganizationId, mapping.NetRatelTenantId, peer.InstallationId, row.Id.ToString("D"), row.Revision),
            LastTestedAtUtc = row.LastTestedAtUtc, LastTestSucceeded = row.LastTestSucceeded, UpdatedAtUtc = row.UpdatedAtUtc };
    }
    public async Task RequireCurrentAsync(OrchestrationResolvedSettings expected, CancellationToken ct)
    {
        if (expected.Pairing is null || !Guid.TryParse(expected.Pairing.MappingId, out var id)) throw new PairingFailure("connection_required", "A saved automation system connection is required.", 403);
        var current = await ResolveAsync(expected.Pairing.LocalTenantId, id, ct);
        if (!current.Enabled || current.Revision != expected.Revision || current.ProfileFingerprint != expected.ProfileFingerprint || current.Pairing != expected.Pairing)
            throw new PairingFailure("connection_revoked", "The named automation connection changed or was deleted. Resolve its current configuration before dispatching.", 403);
    }
}
