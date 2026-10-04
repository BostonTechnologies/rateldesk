using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.ServiceIdentity;

public sealed record ServiceClientCreateRequest(string Name, string OrganizationId, string PeerInstanceId,
    string PeerTenantId, string[] Scopes, string[] CustomerIds, Guid? SourceInstanceId = null,
    Guid? SourceNamespaceId = null, string? LinkId = null, string? AttemptId = null, string? GrantHash = null,
    string? DescriptorHash = null, string? DirectionId = null, long LinkRevision = 1,
    string ResourceConstraintsJson = "{}", string? ClientId = null);
public sealed record CreatedServiceClient(ServicePrincipalRegistration Principal, string ClientSecret, long CredentialRevision)
{
    public DateTimeOffset CredentialExpiresAtUtc { get; init; }
}
public sealed record ServiceClientMetadata(Guid Id, string Name, string ClientId, string OrganizationId,
    string PeerInstanceId, string PeerTenantId, string[] Scopes, string[] CustomerIds, string Status, string Source,
    bool ReadOnly, long Revision, long CredentialRevision, Guid? SourceInstanceId, Guid? SourceNamespaceId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc)
{
    public string? LinkId { get; init; }
    public string? DirectionId { get; init; }
}
public sealed record AuthenticatedServiceClient(ServicePrincipalRegistration Principal, ServicePrincipalSecret Credential);

public interface IServicePrincipalRegistry
{
    Task<CreatedServiceClient> CreateAsync(ServiceClientCreateRequest request, string actorId, bool pending = false, CancellationToken ct = default);
    Task<CreatedServiceClient> CreatePendingAsync(ServiceClientCreateRequest request, string actorId, CancellationToken ct = default);
    Task<ServicePrincipalRegistration?> ResolvePrincipalAsync(ClaimsPrincipal principal, string? requiredScope = null, CancellationToken ct = default);
    Task<AuthenticatedServiceClient?> AuthenticateClientAsync(string clientId, string secret, CancellationToken ct = default);
    Task<bool> CanIssueScopesAsync(AuthenticatedServiceClient client, string[] scopes, CancellationToken ct = default);
    Task ActivateAsync(Guid id, CancellationToken ct = default);
    Task SetStatusAsync(Guid id, string status, CancellationToken ct = default);
    Task RevokeAsync(Guid id, CancellationToken ct = default);
    Task<CreatedServiceClient> CreateSuccessorAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task ActivateSuccessorAsync(Guid id, long revision, CancellationToken ct = default);
    Task RetirePredecessorAsync(Guid id, long revision, DateTimeOffset retireAt, CancellationToken ct = default);
    Task<CreatedServiceClient> RotateAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceClientMetadata>> ListAsync(CancellationToken ct = default);
    string[] PermittedScopes(ServicePrincipalRegistration principal, ServicePrincipalSecret credential);
}

/// <summary>Every token grant and authenticated request reads durable authority, including on other replicas.</summary>
public sealed class ServicePrincipalRegistry(HelpdeskDbContext db, IOptionsMonitor<ServiceIdentityOptions> options,
    TimeProvider time) : IServicePrincipalRegistry
{
    public static bool IsValidClientId(string value) => Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    public static string[] ReadArray(string value) => JsonSerializer.Deserialize<string[]>(value) ?? [];
    public static bool IsMachinePrincipal(ClaimsPrincipal principal) => principal.HasClaim("auth_mode", "service") && principal.HasClaim("token_use", ServiceIdentityClaims.Purpose);

    public Task<CreatedServiceClient> CreatePendingAsync(ServiceClientCreateRequest request, string actorId, CancellationToken ct = default) => CreateAsync(request, actorId, true, ct);

    public async Task<CreatedServiceClient> CreateAsync(ServiceClientCreateRequest request, string actorId, bool pending = false, CancellationToken ct = default)
    {
        if (!options.CurrentValue.Enabled) throw new InvalidOperationException("The service issuer is not configured.");
        ValidateRequest(request);
        if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentException("An approving administrator is required.");
        var clientId = request.ClientId ?? $"rdsvc_{Guid.NewGuid():N}";
        if (!IsValidClientId(clientId)) throw new ArgumentException("Invalid client ID.");
        if (options.CurrentValue.Clients.Any(x => x.ClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase)) ||
            await db.Set<ServicePrincipalRegistration>().AnyAsync(x => x.NormalizedClientId == clientId.ToUpperInvariant(), ct))
            throw new ServiceClientConflictException("The client identity is already owned by a deployment or registration.");
        if (!await MappingEnabledAsync(request.OrganizationId, request.CustomerIds, ct)) throw new ArgumentException("Every selected organization/customer must be enabled and belong to the explicit organization.");
        var now = time.GetUtcNow();
        var registration = new ServicePrincipalRegistration
        {
            ClientId = clientId, NormalizedClientId = clientId.ToUpperInvariant(), Name = request.Name,
            OrganizationId = request.OrganizationId, PeerInstanceId = request.PeerInstanceId, PeerTenantId = request.PeerTenantId,
            AllowedScopesJson = JsonSerializer.Serialize(request.Scopes.Order(StringComparer.Ordinal)),
            CustomerIdsJson = JsonSerializer.Serialize(request.CustomerIds.Order(StringComparer.Ordinal)), ResourceConstraintsJson = JsonSerializer.Serialize(ReadLocalConstraints(request)),
            SourceInstanceId = request.SourceInstanceId, SourceNamespaceId = request.SourceNamespaceId, LinkId = request.LinkId,
            AttemptId = request.AttemptId, GrantHash = request.GrantHash, DescriptorHash = request.DescriptorHash,
            DirectionId = request.DirectionId, LinkRevision = request.LinkRevision, Status = pending ? "pending" : "active",
            CreatedBy = actorId, ApprovedBy = actorId, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var secret = NewSecret(registration.Id, 1, pending ? "pending" : "active");
        db.Set<ServicePrincipalRegistration>().Add(registration);
        db.Set<ServicePrincipalSecret>().Add(secret.Row);
        await BindSourceAsync(registration, !pending, actorId, ct);
        await db.SaveChangesAsync(ct);
        return new(registration, secret.Value, 1) { CredentialExpiresAtUtc = secret.Row.ExpiresAtUtc };
    }

    public async Task<AuthenticatedServiceClient?> AuthenticateClientAsync(string clientId, string secret, CancellationToken ct = default)
    {
        if (!options.CurrentValue.Enabled || !IsValidClientId(clientId) || secret.Length is < 32 or > 1024) return null;
        await EnsureDeploymentClientsAsync(ct);
        var registration = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedClientId == clientId.ToUpperInvariant(), ct);
        if (registration is null || !await RegistrationEnabledAsync(registration, ct) || !await LocalIdentityCurrentAsync(registration, ct)) return null;
        var credentials = await db.Set<ServicePrincipalSecret>().AsNoTracking().Where(x => x.ServicePrincipalId == registration.Id && x.Status != "revoked").ToListAsync(ct);
        ServicePrincipalSecret? selected = null;
        foreach (var credential in credentials)
        {
            var computed = Hash(credential.Salt, secret);
            var matches = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(credential.SecretHash), Convert.FromHexString(computed));
            if (matches && CredentialEnabled(credential)) selected = credential;
        }
        return selected is null ? null : new(registration, selected);
    }

    public async Task<ServicePrincipalRegistration?> ResolvePrincipalAsync(ClaimsPrincipal principal, string? requiredScope = null, CancellationToken ct = default)
    {
        if (!IsMachinePrincipal(principal) || !Guid.TryParseExact(principal.FindFirstValue(ServiceIdentityClaims.PrincipalId), "N", out var id) ||
            !long.TryParse(principal.FindFirstValue(ServiceIdentityClaims.CredentialRevision), out var credentialRevision)) return null;
        await EnsureDeploymentClientsAsync(ct);
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == credentialRevision, ct);
        if (row is null || credential is null || !CredentialEnabled(credential) || !await RegistrationEnabledAsync(row, ct) || !await LocalIdentityCurrentAsync(row, ct)) return null;
        if (principal.FindFirstValue("sub") != $"service:{row.Id:N}" || principal.FindFirstValue("client_id") != row.ClientId ||
            principal.FindFirstValue(ServiceIdentityClaims.GrantRevision) != row.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            principal.FindFirstValue(ServiceIdentityClaims.OrganizationId) != row.OrganizationId ||
            principal.FindFirstValue(ServiceIdentityClaims.PeerInstanceId) != row.PeerInstanceId || principal.FindFirstValue(ServiceIdentityClaims.PeerTenantId) != row.PeerTenantId ||
            !Bound(principal, ServiceIdentityClaims.LinkId, row.LinkId) || !Bound(principal, ServiceIdentityClaims.AttemptId, row.AttemptId) ||
            !Bound(principal, ServiceIdentityClaims.GrantHash, row.GrantHash) || !Bound(principal, ServiceIdentityClaims.DirectionId, row.DirectionId) ||
            principal.FindFirstValue(ServiceIdentityClaims.LinkRevision) != row.LinkRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)) return null;
        var tokenScopes = principal.FindAll("scope").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var allowed = PermittedScopes(row, credential);
        if (tokenScopes.Length == 0 || tokenScopes.Any(x => !allowed.Contains(x, StringComparer.Ordinal)) ||
            requiredScope is not null && !tokenScopes.Contains(requiredScope, StringComparer.Ordinal)) return null;
        if (!await LinkBusinessEnabledAsync(row, tokenScopes, ct)) return null;
        return row;
    }

    public string[] PermittedScopes(ServicePrincipalRegistration principal, ServicePrincipalSecret credential)
    {
        if (principal.Status == "revoked") return principal.TerminalControlUntilUtc > time.GetUtcNow() && principal.LinkId is not null ? [ServiceIdentityScopes.Control] : [];
        if (principal.LinkId is null) return principal.Status == "active" && credential.Status != "pending" ? ReadArray(principal.AllowedScopesJson) : [];
        if (principal.Status == "active" && credential.Status is "active" or "retiring") return [.. ReadArray(principal.AllowedScopesJson), ServiceIdentityScopes.Verify, ServiceIdentityScopes.Control];
        return principal.Status is "pending" or "prepared" or "verified" or "in_doubt" or "active" ? [ServiceIdentityScopes.Verify, ServiceIdentityScopes.Control] : [];
    }

    public async Task<bool> CanIssueScopesAsync(AuthenticatedServiceClient client, string[] scopes, CancellationToken ct = default)
    {
        if (!await LocalIdentityCurrentAsync(client.Principal, ct)) return false;
        if (!await LinkBusinessEnabledAsync(client.Principal, scopes, ct)) return false;
        // Control recovery is independent from a revoked incident binding; business issuance must observe it live.
        if (!scopes.Any(x => x is ServiceIdentityScopes.IncidentCreate or ServiceIdentityScopes.IncidentReceipts or ServiceIdentityScopes.IncidentTargets)) return true;
        var row = client.Principal;
        if (row.SourceNamespaceId is null || row.SourceInstanceId is null) return false;
        var customers = ReadArray(row.CustomerIdsJson);
        return await db.IncidentReceiverSources.AsNoTracking().AnyAsync(x => x.SourceNamespaceId == row.SourceNamespaceId && x.SourceInstanceId == row.SourceInstanceId &&
                   x.IsEnabled && x.OrganizationId == row.OrganizationId && customers.Contains(x.CustomerId), ct) &&
               await db.IncidentReceiverPrincipalBindings.AsNoTracking().AnyAsync(x => x.SourceNamespaceId == row.SourceNamespaceId && x.PrincipalKind == "service_principal" && x.PrincipalId == row.Id.ToString("N") && x.IsEnabled, ct);
    }

    private async Task<bool> LinkBusinessEnabledAsync(ServicePrincipalRegistration row, string[] scopes, CancellationToken ct)
    {
        if (row.LinkId is null || !scopes.Any(x => ServiceIdentityScopes.Business.Contains(x, StringComparer.Ordinal))) return true;
        return await db.Set<ServiceLinkAttempt>().AsNoTracking().AnyAsync(x => x.LinkId == row.LinkId && x.AttemptId == row.AttemptId &&
            x.LinkRevision == row.LinkRevision && x.GrantHash == row.GrantHash && x.InboundPrincipalId == row.Id &&
            x.LocalTenantId == row.OrganizationId && x.PeerInstanceId == row.PeerInstanceId && x.PeerTenantId == row.PeerTenantId &&
            x.Decision == "commit" && x.LocalInboundActive && (x.LifecycleState == "commit_decided" || x.LifecycleState == "active"), ct);
    }

    private async Task<bool> LocalIdentityCurrentAsync(ServicePrincipalRegistration row, CancellationToken ct)
    {
        var currentOptions = options.CurrentValue;
        if (!currentOptions.Enabled) return false;
        if (row.LinkId is null) return true;
        var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.LinkId == row.LinkId && x.AttemptId == row.AttemptId &&
            x.LinkRevision == row.LinkRevision && x.GrantHash == row.GrantHash && x.InboundPrincipalId == row.Id &&
            x.LocalTenantId == row.OrganizationId && x.PeerInstanceId == row.PeerInstanceId && x.PeerTenantId == row.PeerTenantId, ct);
        if (attempt?.GrantSummaryJson is null || attempt.DescriptorHash != row.DescriptorHash) return false;
        try
        {
            var summary = Helpdesk.Shared.ServiceLink.ServiceLinkCanonicalJson.Deserialize<Helpdesk.Shared.ServiceLink.ServiceLinkGrantSummary>(attempt.GrantSummaryJson);
            if (summary.Contract != Helpdesk.Shared.ServiceLink.ServiceLinkContract.Version || summary.AttemptId != attempt.AttemptId ||
                summary.LinkId != attempt.LinkId || summary.ProposedLinkRevision != attempt.LinkRevision || summary.DescriptorHash != attempt.DescriptorHash ||
                Helpdesk.Shared.ServiceLink.ServiceLinkCanonicalJson.HashObject(summary) != attempt.GrantHash) return false;
            return ServiceLinkAuthority.LocalIdentityMatches(summary, attempt.Role, currentOptions);
        }
        catch (JsonException) { return false; }
    }

    public Task ActivateAsync(Guid id, CancellationToken ct = default) => SetStatusAsync(id, "active", ct);

    public async Task SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        if (status is not ("pending" or "prepared" or "verified" or "in_doubt" or "active" or "revoked" or "expired" or "failed")) throw new ArgumentException("Unsupported registration state.");
        var row = await ManagedAsync(id, ct);
        if (row.Status == "revoked" && status != "revoked") throw new ServiceClientConflictException("Revoked registrations cannot be reopened.");
        if (status == "active" && !await MappingEnabledAsync(row.OrganizationId, ReadArray(row.CustomerIdsJson), ct)) throw new ServiceClientConflictException("The approved mapping is disabled.");
        row.Status = status;
        row.Version++;
        row.UpdatedAtUtc = time.GetUtcNow();
        if (status == "active")
        {
            var credential = await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == row.CurrentCredentialRevision, ct);
            credential.Status = "active";
        }
        if (status == "revoked")
        {
            row.RevokedAtUtc ??= time.GetUtcNow();
            row.TerminalControlUntilUtc ??= time.GetUtcNow().AddSeconds(options.CurrentValue.TerminalControlRecoverySeconds);
            if (row.LinkId is not null)
            {
                var link = await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.LinkId == row.LinkId && x.InboundPrincipalId == row.Id, ct);
                if (link is not null && link.LocalInboundActive)
                {
                    // Invocation depends on the callback grant accepted by this identity.
                    // A local directional revocation must stop it immediately as well.
                    link.LocalInboundActive = false;
                    link.LocalBusinessSenderEnabled = false;
                    link.LastErrorCode = "grant-unavailable";
                    link.Revision++;
                    link.UpdatedAtUnixSeconds = time.GetUtcNow().ToUnixTimeSeconds();
                }
            }
        }
        if (row.SourceNamespaceId.HasValue)
        {
            var binding = await db.IncidentReceiverPrincipalBindings.SingleOrDefaultAsync(x => x.SourceNamespaceId == row.SourceNamespaceId && x.PrincipalKind == "service_principal" && x.PrincipalId == id.ToString("N"), ct);
            if (binding is not null) binding.IsEnabled = status == "active";
        }
        await db.SaveChangesAsync(ct);
    }

    public Task RevokeAsync(Guid id, CancellationToken ct = default) => SetStatusAsync(id, "revoked", ct);

    public async Task<CreatedServiceClient> CreateSuccessorAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        var row = await ManagedAsync(id, ct);
        if (row.Status != "active" || row.CurrentCredentialRevision != expectedRevision) throw new ServiceClientConflictException("The credential revision or registration state changed.");
        if (await db.Set<ServicePrincipalSecret>().AnyAsync(x => x.ServicePrincipalId == id && x.CredentialRevision > expectedRevision && x.Status == "pending", ct)) throw new ServiceClientConflictException("A successor credential is already pending.");
        var next = (await db.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == id).MaxAsync(x => (long?)x.CredentialRevision, ct) ?? 0) + 1;
        var secret = NewSecret(id, next, "pending");
        db.Set<ServicePrincipalSecret>().Add(secret.Row);
        row.Version++;
        await db.SaveChangesAsync(ct);
        return new(row, secret.Value, next) { CredentialExpiresAtUtc = secret.Row.ExpiresAtUtc };
    }

    public async Task ActivateSuccessorAsync(Guid id, long revision, CancellationToken ct = default)
    {
        var row = await ManagedAsync(id, ct);
        var successor = await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == revision, ct);
        if (row.Status != "active" || successor.Status == "revoked" || revision < row.CurrentCredentialRevision) throw new ServiceClientConflictException("The successor cannot be activated.");
        successor.Status = "active";
        row.CurrentCredentialRevision = revision;
        row.Version++;
        row.UpdatedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task RetirePredecessorAsync(Guid id, long revision, DateTimeOffset retireAt, CancellationToken ct = default)
    {
        var row = await ManagedAsync(id, ct);
        if (revision >= row.CurrentCredentialRevision || retireAt > time.GetUtcNow().AddSeconds(options.CurrentValue.CredentialOverlapSeconds)) throw new ServiceClientConflictException("Invalid predecessor retirement.");
        var predecessor = await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == revision, ct);
        predecessor.Status = "retiring";
        predecessor.RetireAtUtc ??= retireAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task<CreatedServiceClient> RotateAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        var row = await ManagedAsync(id, ct);
        if (row.LinkId is not null) throw new ServiceClientConflictException("Reciprocal clients rotate through the coordinated link workflow.");
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var result = await CreateSuccessorAsync(id, expectedRevision, ct);
        await ActivateSuccessorAsync(id, result.CredentialRevision, ct);
        await RetirePredecessorAsync(id, expectedRevision, time.GetUtcNow().AddSeconds(options.CurrentValue.CredentialOverlapSeconds), ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<ServiceClientMetadata>> ListAsync(CancellationToken ct = default)
    {
        if (options.CurrentValue.Enabled) await EnsureDeploymentClientsAsync(ct);
        var rows = await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync(ct);
        var secrets = await db.Set<ServicePrincipalSecret>().AsNoTracking().ToListAsync(ct);
        return rows.OrderByDescending(x => x.CreatedAtUtc).Select(x => Metadata(x, secrets.Single(s => s.ServicePrincipalId == x.Id && s.CredentialRevision == x.CurrentCredentialRevision).ExpiresAtUtc)).ToArray();
    }

    public static ServiceClientMetadata Metadata(ServicePrincipalRegistration row, DateTimeOffset expiresAt) => new(row.Id, row.Name, row.ClientId, row.OrganizationId, row.PeerInstanceId, row.PeerTenantId, ReadArray(row.AllowedScopesJson), ReadArray(row.CustomerIdsJson), row.Status, row.Source, row.Source == "deployment", row.Revision, row.CurrentCredentialRevision, row.SourceInstanceId, row.SourceNamespaceId, row.CreatedAtUtc, expiresAt) { LinkId = row.LinkId, DirectionId = row.DirectionId };

    private bool CredentialEnabled(ServicePrincipalSecret row) => row.Status != "revoked" && row.ExpiresAtUtc > time.GetUtcNow() && (row.RetireAtUtc is null || row.RetireAtUtc > time.GetUtcNow());
    private async Task<bool> RegistrationEnabledAsync(ServicePrincipalRegistration row, CancellationToken ct) =>
        (row.Status is "pending" or "prepared" or "verified" or "in_doubt" or "active" || row.Status == "revoked" && row.TerminalControlUntilUtc > time.GetUtcNow()) &&
        await MappingEnabledAsync(row.OrganizationId, ReadArray(row.CustomerIdsJson), ct);
    private async Task<bool> MappingEnabledAsync(string organizationId, string[] customers, CancellationToken ct) =>
        await db.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId && x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct) &&
        await db.Customers.AsNoTracking().CountAsync(x => customers.Contains(x.Id) && x.OrganizationId == organizationId && x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct) == customers.Length;
    private static bool Bound(ClaimsPrincipal principal, string claim, string? value) => principal.FindFirstValue(claim) == value;

    private async Task<ServicePrincipalRegistration> ManagedAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Set<ServicePrincipalRegistration>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (row.Source == "deployment") throw new ServiceClientConflictException("Deployment-managed clients are read-only; update ServiceIdentity:Clients in deployment configuration.");
        return row;
    }

    private (ServicePrincipalSecret Row, string Value) NewSecret(Guid id, long revision, string status, string? value = null)
    {
        value ??= Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return (new() { ServicePrincipalId = id, CredentialRevision = revision, Salt = salt, SecretHash = Hash(salt, value), Status = status,
            CreatedAtUtc = time.GetUtcNow(), ExpiresAtUtc = time.GetUtcNow().AddDays(options.CurrentValue.CredentialMaximumAgeDays) }, value);
    }
    private static string Hash(string salt, string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{value}")));

    internal static void ValidateRequest(ServiceClientCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || string.IsNullOrWhiteSpace(request.OrganizationId) || request.OrganizationId.Length > 64 ||
            string.IsNullOrWhiteSpace(request.PeerInstanceId) || request.PeerInstanceId.Length > 256 || string.IsNullOrWhiteSpace(request.PeerTenantId) || request.PeerTenantId.Length > 256)
            throw new ArgumentException("A name, organization and explicit peer instance/tenant are required.");
        if (request.OrganizationId.Any(char.IsControl) || request.PeerInstanceId.Any(char.IsControl) || request.PeerTenantId.Any(char.IsControl)) throw new ArgumentException("Service identity fields cannot contain control characters.");
        if (request.Scopes is null || request.Scopes.Length == 0 || request.Scopes.Distinct(StringComparer.Ordinal).Count() != request.Scopes.Length || request.Scopes.Any(x => !ServiceIdentityScopes.Business.Contains(x, StringComparer.Ordinal))) throw new ArgumentException("Only exact supported narrow business scopes can be approved.");
        if (request.CustomerIds is null || request.CustomerIds.Length > 100 || request.CustomerIds.Any(string.IsNullOrWhiteSpace) || request.CustomerIds.Distinct(StringComparer.Ordinal).Count() != request.CustomerIds.Length) throw new ArgumentException("Customer grants must be an explicit unique bounded list.");
        if (request.Scopes.Any(x => x != ServiceIdentityScopes.Callback) && (request.CustomerIds.Length != 1 || request.SourceInstanceId is null)) throw new ArgumentException("Incident scopes require an exact producer source and one approved customer mapping.");
        if (request.SourceInstanceId is not null && request.CustomerIds.Length != 1) throw new ArgumentException("A receiver source requires exactly one approved customer mapping.");
        if (request.LinkId is not null && (request.AttemptId is null || request.GrantHash?.Length != 64 || request.DescriptorHash?.Length != 64 || request.DirectionId is not ("initiator_to_responder" or "responder_to_initiator") || request.LinkRevision < 1)) throw new ArgumentException("A reciprocal registration requires all approved ceremony bindings.");
        var typed = ReadLocalConstraints(request);
        foreach (var values in new[] { typed.CustomerIds, typed.RequestIds, typed.TaskIds, typed.ResourceIds, typed.RequestDefinitionIds })
            if (values is null || values.Length > 100 || values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Constraint lists must be explicit unique bounded sets.");
        if (typed.OrganizationId is not null && typed.OrganizationId != request.OrganizationId || typed.CustomerIds.Length > 0 &&
            !typed.CustomerIds.Order(StringComparer.Ordinal).SequenceEqual(request.CustomerIds.Order(StringComparer.Ordinal))) throw new ArgumentException("Resource constraints must equal the explicitly approved organization/customer mapping.");
        if (request.LinkId is null && request.Scopes.Contains(ServiceIdentityScopes.Callback, StringComparer.Ordinal) &&
            (typed.OrganizationId != request.OrganizationId || typed.RequestIds.Length == 0 || typed.TaskIds.Length == 0))
            throw new ArgumentException("Manual callbacks require resourceConstraintsJson with organization_id and explicit request_ids/task_ids for recorded executions.");
    }

    private static Helpdesk.Shared.ServiceLink.ServiceLinkResourceConstraints ReadLocalConstraints(ServiceClientCreateRequest request)
    {
        if (request.ResourceConstraintsJson is null || request.ResourceConstraintsJson.Length > 8192) throw new ArgumentException("Resource constraints are required and bounded.");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(request.ResourceConstraintsJson); }
        catch (JsonException) { throw new ArgumentException("Malformed service resource constraints."); }
        using var constraints = parsed;
        if (constraints.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Resource constraints must be an object.");
        if (constraints.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != constraints.RootElement.EnumerateObject().Count()) throw new ArgumentException("Duplicate resource constraint fields are invalid.");
        var root = constraints.RootElement;
        var known = new[] { "organization_id", "customer_ids", "request_ids", "task_ids", "tenant_id", "resource_ids", "request_definition_ids" };
        if (root.EnumerateObject().Any(x => !known.Contains(x.Name, StringComparer.Ordinal))) throw new ArgumentException("Unknown service resource constraint fields.");
        try
        {
            // The local manual form has explicit organization/customer fields. Normalize these into the FULL wire schema.
            // A reciprocal grant is already immutable and must contain every frozen field.
            if (request.LinkId is not null) return JsonSerializer.Deserialize<Helpdesk.Shared.ServiceLink.ServiceLinkResourceConstraints>(request.ResourceConstraintsJson) ?? throw new ArgumentException("Resource constraints are required.");
            return new()
            {
                OrganizationId = root.TryGetProperty("organization_id", out var organization) ? organization.GetString() : request.OrganizationId,
                CustomerIds = ReadList("customer_ids", request.CustomerIds), RequestIds = ReadList("request_ids", []), TaskIds = ReadList("task_ids", []),
                TenantId = root.TryGetProperty("tenant_id", out var tenant) ? tenant.GetString() : null,
                ResourceIds = ReadList("resource_ids", []), RequestDefinitionIds = ReadList("request_definition_ids", [])
            };
            string[] ReadList(string key, string[] fallback) => root.TryGetProperty(key, out var property)
                ? property.EnumerateArray().Select(x => x.GetString() ?? throw new ArgumentException("Null constraint identities are invalid.")).ToArray() : fallback;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new ArgumentException("Unknown or malformed service resource constraints."); }
    }

    private async Task BindSourceAsync(ServicePrincipalRegistration row, bool enabled, string actor, CancellationToken ct)
    {
        if (!row.SourceInstanceId.HasValue) return;
        var source = await db.IncidentReceiverSources.SingleOrDefaultAsync(x => x.SourceInstanceId == row.SourceInstanceId, ct);
        var customer = ReadArray(row.CustomerIdsJson).Single();
        if (source is null)
        {
            if (row.SourceNamespaceId.HasValue) throw new ArgumentException("A caller cannot select an authoritative source namespace.");
            source = new() { SourceNamespaceId = Guid.NewGuid(), SourceInstanceId = row.SourceInstanceId.Value, OrganizationId = row.OrganizationId,
                CustomerId = customer, CreatedBy = actor, UpdatedBy = actor, CreatedAtUtc = time.GetUtcNow(), UpdatedAtUtc = time.GetUtcNow() };
            db.IncidentReceiverSources.Add(source);
        }
        if (!source.IsEnabled || source.OrganizationId != row.OrganizationId || source.CustomerId != customer || row.SourceNamespaceId.HasValue && source.SourceNamespaceId != row.SourceNamespaceId) throw new ServiceClientConflictException("The existing source namespace belongs to a different or disabled approved mapping.");
        row.SourceNamespaceId = source.SourceNamespaceId;
        var binding = await db.IncidentReceiverPrincipalBindings.SingleOrDefaultAsync(x => x.SourceNamespaceId == source.SourceNamespaceId && x.PrincipalKind == "service_principal" && x.PrincipalId == row.Id.ToString("N"), ct);
        if (binding is null) db.IncidentReceiverPrincipalBindings.Add(new() { SourceNamespaceId = source.SourceNamespaceId, PrincipalKind = "service_principal", PrincipalId = row.Id.ToString("N"), IsEnabled = enabled });
        else binding.IsEnabled = enabled;
        db.IncidentReceiverSourceAudits.Add(new() { SourceNamespaceId = source.SourceNamespaceId, Revision = source.Revision, ActorId = actor,
            AtUtc = time.GetUtcNow(), Action = enabled ? "service-bound" : "service-pending", OrganizationId = row.OrganizationId, CustomerId = customer,
            PrincipalKind = "service_principal", PrincipalId = row.Id.ToString("N"), IsEnabled = enabled });
    }

    private async Task EnsureDeploymentClientsAsync(CancellationToken ct)
    {
        // Configuration is complete and authoritative. Never combine an environment ID with a database secret.
        var configured = options.CurrentValue.Clients;
        var authorityChanged = false;
        var deployedRows = await db.Set<ServicePrincipalRegistration>().Where(x => x.Source == "deployment").ToListAsync(ct);
        foreach (var removed in deployedRows.Where(x => !configured.Any(c => c.ClientId.ToUpperInvariant() == x.NormalizedClientId)))
        {
            if (removed.Status != "revoked") { removed.Status = "revoked"; removed.Revision++; removed.Version++; removed.RevokedAtUtc = time.GetUtcNow(); authorityChanged = true; }
        }
        foreach (var client in configured)
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(client)));
            var normalized = client.ClientId.ToUpperInvariant();
            var row = await db.Set<ServicePrincipalRegistration>().SingleOrDefaultAsync(x => x.NormalizedClientId == normalized, ct);
            if (row is not null && row.Source != "deployment") throw new ServiceClientConflictException("Deployment client ID collides with a managed registration. Rename one complete identity; ownership is never silently changed.");
            if (row?.DeploymentFingerprint == fingerprint) continue;
            authorityChanged = true;
            if (!await MappingEnabledAsync(client.OrganizationId, client.CustomerIds, ct)) throw new ServiceClientConflictException("Deployment client mapping is missing or disabled.");
            if (row is null)
            {
                row = new() { ClientId = client.ClientId, NormalizedClientId = normalized, Source = "deployment", CreatedBy = "deployment", ApprovedBy = "deployment", CreatedAtUtc = time.GetUtcNow() };
                db.Set<ServicePrincipalRegistration>().Add(row);
            }
            else
            {
                row.Revision++; row.Version++; row.CurrentCredentialRevision++;
                foreach (var old in await db.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == row.Id).ToListAsync(ct)) old.Status = "revoked";
            }
            var sameSourceMapping = row.SourceInstanceId == client.SourceInstanceId && row.OrganizationId == client.OrganizationId &&
                ReadArray(row.CustomerIdsJson).Order(StringComparer.Ordinal).SequenceEqual(client.CustomerIds.Order(StringComparer.Ordinal));
            var retainedNamespace = sameSourceMapping ? row.SourceNamespaceId : null;
            if (!sameSourceMapping && row.SourceNamespaceId is not null)
            {
                var oldBinding = await db.IncidentReceiverPrincipalBindings.SingleOrDefaultAsync(x => x.SourceNamespaceId == row.SourceNamespaceId && x.PrincipalKind == "service_principal" && x.PrincipalId == row.Id.ToString("N"), ct);
                if (oldBinding is not null) oldBinding.IsEnabled = false;
            }
            row.Name = client.Name; row.OrganizationId = client.OrganizationId; row.PeerInstanceId = client.PeerInstanceId; row.PeerTenantId = client.PeerTenantId;
            row.AllowedScopesJson = JsonSerializer.Serialize(client.Scopes); row.CustomerIdsJson = JsonSerializer.Serialize(client.CustomerIds);
            row.ResourceConstraintsJson = JsonSerializer.Serialize(ReadLocalConstraints(new(client.Name, client.OrganizationId, client.PeerInstanceId, client.PeerTenantId, client.Scopes, client.CustomerIds, ResourceConstraintsJson: client.ResourceConstraintsJson)));
            row.SourceInstanceId = client.SourceInstanceId; row.SourceNamespaceId = client.SourceNamespaceId ?? retainedNamespace; row.Status = client.Enabled ? "active" : "revoked";
            row.DeploymentFingerprint = fingerprint; row.UpdatedAtUtc = time.GetUtcNow();
            db.Set<ServicePrincipalSecret>().Add(NewSecret(row.Id, row.CurrentCredentialRevision, "active", client.ClientSecret).Row);
            await BindSourceAsync(row, client.Enabled, "deployment", ct);
        }
        if (authorityChanged) await db.SaveChangesAsync(ct);
    }
}

public sealed class ServiceClientConflictException(string message) : InvalidOperationException(message);
