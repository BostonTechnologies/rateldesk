using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Infrastructure.ServiceIdentity;

public sealed record ServiceClientCreateRequest(string Name, string OrganizationId, string PeerInstanceId,
    string PeerTenantId, string[] Scopes, string[] CustomerIds, Guid? SourceInstanceId = null,
    Guid? SourceNamespaceId = null, Guid? MappingId = null);
public sealed record CreatedServiceClient(ServicePrincipalRegistration Principal, string ClientSecret, long CredentialRevision)
{ public DateTimeOffset CredentialExpiresAtUtc { get; init; } }
public sealed record AuthenticatedServiceClient(ServicePrincipalRegistration Principal, ServicePrincipalSecret Credential);
public interface IServicePrincipalRegistry
{
    Task<CreatedServiceClient> CreateAsync(ServiceClientCreateRequest request, string actorId, bool pending = false, CancellationToken ct = default);
    Task<ServicePrincipalRegistration?> ResolvePrincipalAsync(ClaimsPrincipal principal, string? requiredScope = null, CancellationToken ct = default);
    Task<AuthenticatedServiceClient?> AuthenticateClientAsync(string clientId, string secret, CancellationToken ct = default);
    Task<bool> CanIssueScopesAsync(AuthenticatedServiceClient client, string[] scopes, CancellationToken ct = default);
    Task RevokeAsync(Guid id, CancellationToken ct = default);
    string[] PermittedScopes(ServicePrincipalRegistration principal, ServicePrincipalSecret credential);
}
/// <summary>Every authenticated request checks the current named mapping and retained account authority across replicas.</summary>
public sealed class ServicePrincipalRegistry(HelpdeskDbContext db, IOptionsMonitor<ServiceIdentityOptions> options,
    TimeProvider time, PairingAuthority authority) : IServicePrincipalRegistry
{
    public static bool IsValidClientId(string value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    public static string[] ReadArray(string value) => JsonSerializer.Deserialize<string[]>(value) ?? [];
    public static bool IsMachinePrincipal(ClaimsPrincipal principal) => principal.HasClaim("auth_mode", "service") && principal.HasClaim("token_use", ServiceIdentityClaims.Purpose);
    public async Task<CreatedServiceClient> CreateAsync(ServiceClientCreateRequest request, string actorId, bool pending = false, CancellationToken ct = default)
    {
        ValidateRequest(request);
        if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentException("A current mapping owner is required.");
        var mapping = await db.Set<SystemConnection>().SingleOrDefaultAsync(x => x.Id == request.MappingId && x.State != "deleted", ct)
            ?? throw new ArgumentException("A validated saved mapping is required for business credentials.");
        var now = time.GetUtcNow();
        var row = new ServicePrincipalRegistration { ClientId = "rdpair_" + Guid.NewGuid().ToString("N"), Name = request.Name,
            OrganizationId = request.OrganizationId, PeerInstanceId = request.PeerInstanceId, PeerTenantId = request.PeerTenantId,
            AllowedScopesJson = JsonSerializer.Serialize(request.Scopes.Order(StringComparer.Ordinal)), CustomerIdsJson = JsonSerializer.Serialize(request.CustomerIds),
            SourceInstanceId = request.SourceInstanceId, SourceNamespaceId = request.SourceNamespaceId, MappingId = request.MappingId, MappingRevision = mapping.Revision,
            Status = "active", CreatedBy = actorId, ApprovedBy = actorId, CreatedAtUtc = now, UpdatedAtUtc = now };
        row.NormalizedClientId = row.ClientId.ToUpperInvariant();
        var value = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)); var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var credential = new ServicePrincipalSecret { ServicePrincipalId = row.Id, CredentialRevision = 1, Salt = salt, SecretHash = Hash(salt, value), Status = "active", CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(options.CurrentValue.CredentialMaximumAgeDays) };
        db.Add(row); db.Add(credential); await BindSourceAsync(row, actorId, ct); await db.SaveChangesAsync(ct);
        return new(row, value, 1) { CredentialExpiresAtUtc = credential.ExpiresAtUtc };
    }
    public async Task<AuthenticatedServiceClient?> AuthenticateClientAsync(string clientId, string secret, CancellationToken ct = default)
    {
        if (!IsValidClientId(clientId) || secret is null || secret.Length is < 32 or > 1024) return null;
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedClientId == clientId.ToUpperInvariant(), ct);
        if (row is null || !await UsableAsync(row, ct)) return null;
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x => x.ServicePrincipalId == row.Id && x.CredentialRevision == row.CurrentCredentialRevision, ct);
        if (credential is null || !CredentialEnabled(credential) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(credential.SecretHash), Convert.FromHexString(Hash(credential.Salt, secret)))) return null;
        return new(row, credential);
    }
    public async Task<ServicePrincipalRegistration?> ResolvePrincipalAsync(ClaimsPrincipal principal, string? requiredScope = null, CancellationToken ct = default)
    {
        if (!IsMachinePrincipal(principal) || !Guid.TryParseExact(principal.FindFirstValue(ServiceIdentityClaims.PrincipalId), "N", out var id) ||
            !long.TryParse(principal.FindFirstValue(ServiceIdentityClaims.CredentialRevision), out var revision)) return null;
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == revision, ct);
        if (row is null || credential is null || !CredentialEnabled(credential) || !await UsableAsync(row, ct)) return null;
        if (principal.FindFirstValue("sub") != "service:" + row.Id.ToString("N") || principal.FindFirstValue("client_id") != row.ClientId ||
            principal.FindFirstValue(ServiceIdentityClaims.GrantRevision) != row.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            principal.FindFirstValue(ServiceIdentityClaims.OrganizationId) != row.OrganizationId || principal.FindFirstValue(ServiceIdentityClaims.PeerInstanceId) != row.PeerInstanceId ||
            principal.FindFirstValue(ServiceIdentityClaims.PeerTenantId) != row.PeerTenantId || principal.FindFirstValue(ServiceIdentityClaims.MappingId) != row.MappingId?.ToString("D") ||
            principal.FindFirstValue(ServiceIdentityClaims.MappingRevision) != row.MappingRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)) return null;
        var scopes = principal.FindAll("scope").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (scopes.Length == 0 || scopes.Any(x => !PermittedScopes(row, credential).Contains(x, StringComparer.Ordinal)) || requiredScope is not null && !scopes.Contains(requiredScope, StringComparer.Ordinal)) return null;
        return row;
    }
    public string[] PermittedScopes(ServicePrincipalRegistration principal, ServicePrincipalSecret credential) =>
        principal.Status == "active" && CredentialEnabled(credential) && principal.MappingId is not null ? ReadArray(principal.AllowedScopesJson) : [];
    public async Task<bool> CanIssueScopesAsync(AuthenticatedServiceClient client, string[] scopes, CancellationToken ct = default) =>
        scopes.Length > 0 && scopes.Distinct(StringComparer.Ordinal).Count() == scopes.Length && scopes.All(x => PermittedScopes(client.Principal, client.Credential).Contains(x, StringComparer.Ordinal)) && await UsableAsync(client.Principal, ct);
    private async Task<bool> UsableAsync(ServicePrincipalRegistration row, CancellationToken ct)
    {
        if (row.Status != "active" || row.MappingId is not { } mappingId) return false;
        var mapping = await db.Set<SystemConnection>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == mappingId && x.InboundPrincipalId == row.Id && x.Revision == row.MappingRevision && x.State == "connected", ct);
        if (mapping is null) return false;
        foreach (var scope in ReadArray(row.AllowedScopesJson)) if (!await authority.BusinessUsableAsync(mappingId, scope, ct)) return false;
        if (row.SourceNamespaceId is not { } sourceId) return row.SourceInstanceId is null;
        return await db.IncidentReceiverSources.AsNoTracking().AnyAsync(x => x.SourceNamespaceId == sourceId && x.SourceInstanceId == row.SourceInstanceId && x.OrganizationId == row.OrganizationId && x.IsEnabled, ct) &&
            await db.IncidentReceiverPrincipalBindings.AsNoTracking().AnyAsync(x => x.SourceNamespaceId == sourceId && x.PrincipalKind == "service_principal" && x.PrincipalId == row.Id.ToString("N") && x.IsEnabled, ct);
    }
    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Set<ServicePrincipalRegistration>().SingleOrDefaultAsync(x => x.Id == id, ct); if (row is null) return;
        row.Status = "revoked"; row.Revision++; row.Version++; row.RevokedAtUtc ??= time.GetUtcNow();
        foreach (var secret in await db.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == id).ToListAsync(ct)) secret.Status = "revoked";
        foreach (var binding in await db.IncidentReceiverPrincipalBindings.Where(x => x.PrincipalKind == "service_principal" && x.PrincipalId == id.ToString("N")).ToListAsync(ct)) binding.IsEnabled = false;
        await db.SaveChangesAsync(ct);
    }
    private async Task BindSourceAsync(ServicePrincipalRegistration row, string actor, CancellationToken ct)
    {
        if (row.SourceInstanceId is not { } producer || row.SourceNamespaceId is not { } ns) return;
        var customer = ReadArray(row.CustomerIdsJson).Single();
        var source = await db.IncidentReceiverSources.SingleOrDefaultAsync(x => x.SourceNamespaceId == ns, ct);
        if (source is null) { source = new() { SourceNamespaceId = ns, SourceInstanceId = producer, OrganizationId = row.OrganizationId, CustomerId = customer, CreatedBy = actor, UpdatedBy = actor, CreatedAtUtc = time.GetUtcNow(), UpdatedAtUtc = time.GetUtcNow() }; db.Add(source); }
        if (source.SourceInstanceId != producer) throw new ServiceClientConflictException("The connection source namespace belongs to another producer.");
        if (source.OrganizationId != row.OrganizationId || source.CustomerId != customer)
        {
            if (await db.IncidentCreateReceipts.AnyAsync(x => x.SourceNamespaceId == ns, ct)) throw new PairingFailure("mapping_history_bound", "This connection already has committed incident receipts for its selected organization and customer. Keep those selections or create a new named connection for another target.", 409);
            source.Revision++; source.OrganizationId = row.OrganizationId; source.CustomerId = customer; source.UpdatedBy = actor; source.UpdatedAtUtc = time.GetUtcNow();
        }
        source.IsEnabled = true;
        db.Add(new IncidentReceiverPrincipalBinding { SourceNamespaceId = ns, PrincipalKind = "service_principal", PrincipalId = row.Id.ToString("N"), IsEnabled = true });
        db.Add(new IncidentReceiverSourceAudit { SourceNamespaceId = ns, Revision = source.Revision, ActorId = actor, AtUtc = time.GetUtcNow(), Action = "pairing-bound", OrganizationId = row.OrganizationId, CustomerId = customer, PrincipalKind = "service_principal", PrincipalId = row.Id.ToString("N"), IsEnabled = true });
    }
    private bool CredentialEnabled(ServicePrincipalSecret row) => row.Status == "active" && row.ExpiresAtUtc > time.GetUtcNow();
    private static string Hash(string salt, string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + value)));
    internal static void ValidateRequest(ServiceClientCreateRequest request)
    {
        if (request.MappingId is null || request.MappingId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || string.IsNullOrWhiteSpace(request.OrganizationId) ||
            !Guid.TryParse(request.PeerInstanceId, out _) || !int.TryParse(request.PeerTenantId, out var tenant) || tenant <= 0 || request.Scopes is null || request.Scopes.Length == 0 ||
            request.Scopes.Distinct(StringComparer.Ordinal).Count() != request.Scopes.Length || request.Scopes.Any(x => !ServiceIdentityScopes.Business.Contains(x, StringComparer.Ordinal))) throw new ArgumentException("A real scoped mapping and supported business capabilities are required.");
        var incident = request.Scopes.Any(x => x != ServiceIdentityScopes.Callback);
        if (request.CustomerIds is null || incident && (request.CustomerIds.Length != 1 || request.SourceInstanceId is null || request.SourceNamespaceId != request.MappingId) ||
            !incident && (request.CustomerIds.Length != 0 || request.SourceInstanceId is not null || request.SourceNamespaceId is not null)) throw new ArgumentException("Incident access requires the selected customer and stable producer/mapping namespace.");
    }
}
public sealed class ServiceClientConflictException(string message) : InvalidOperationException(message);
