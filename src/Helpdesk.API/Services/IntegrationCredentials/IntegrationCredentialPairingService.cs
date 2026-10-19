using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services.IntegrationCredentials;

public sealed class IntegrationCredentialPairingService(
    RatelDeskIdentityDbContext identity, HelpdeskDbContext db, IntegrationCredentialIssuer issuer,
    IIntegrationCredentialOwnerResolver owners, ICurrentUserAccessService accessService,
    IDataProtectionProvider protection, IConfiguration configuration, TimeProvider clock)
{
    public const int PollIntervalSeconds = 5;
    // RemoteIpAddress is the immediate peer. A Web/YARP peer legitimately shares this bucket.
    public const int MaximumStartsPerTenMinutes = 120;
    private readonly IDataProtector protector = protection.CreateProtector("RatelDesk.IntegrationCredentialPairing.v1");
    private DateTimeOffset Now => clock.GetUtcNow();
    public string? PublicOrigin
    {
        get
        {
            var value = configuration["IntegrationCredentials:Pairing:PublicOrigin"];
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
                uri.AbsolutePath == "/" ? uri.GetLeftPart(UriPartial.Authority) : null;
        }
    }

    public IntegrationPairingCapabilities Capabilities() => new("RatelDesk", 1, PublicOrigin is not null,
        PublicOrigin, PublicOrigin is null ? null : $"{PublicOrigin}/account/integration-credentials/approve");

    public async Task<IntegrationPairingStarted> StartAsync(IntegrationPairingStartRequest request, string remote, CancellationToken ct)
    {
        if (PublicOrigin is null) throw new PairingUnavailableException();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || request.Name.Any(char.IsControl))
            throw Invalid("name", "Enter a connection name up to 128 characters without control characters.");
        var purpose = request.Purpose?.Trim().ToLowerInvariant();
        if (purpose is not ("api" or "mcp")) throw Invalid("purpose", "Purpose must be api or mcp.");
        if (request.GrantKind is not ("ordinary" or "setup-verification" or IntegrationCredentialGrantKinds.OwnerAdministration) ||
            (request.GrantKind != "ordinary" && purpose != "mcp"))
            throw Invalid("grantKind", "Setup verification and owner administration require this instance's MCP resource.");
        var resource = IntegrationCredentialIssuer.CanonicalMcpResourceUri(request.McpResourceUri);
        // Single-origin pairing binds to this deployment's trusted resource, never a caller-supplied recipient.
        if (purpose == "mcp" && resource != $"{PublicOrigin}/mcp")
            throw Invalid("mcpResourceUri", "Use this instance's canonical public HTTPS /mcp resource.");
        if (purpose == "api" && !string.IsNullOrWhiteSpace(request.McpResourceUri))
            throw Invalid("mcpResourceUri", "API credentials do not have an MCP resource.");
        if (!await TakeAttemptAsync("start:" + remote, MaximumStartsPerTenMinutes, ct)) throw new PairingRateLimitException();
        await CleanupAsync(ct);
        var device = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var human = new string(Enumerable.Range(0, 8).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        var expiry = Now.AddMinutes(10);
        identity.IntegrationCredentialPairings.Add(new()
        {
            Id = Guid.NewGuid(), DeviceHash = Hash(device), HumanHash = Hash(human), Name = request.Name.Trim(),
            Purpose = request.GrantKind == "setup-verification" ? IntegrationCredentialIssuer.SetupVerificationPurpose :
                request.GrantKind == IntegrationCredentialGrantKinds.OwnerAdministration ? IntegrationCredentialGrantKinds.OwnerAdministrationPurpose : purpose,
            McpResourceUri = purpose == "mcp" ? resource : null, ExpiresAtUnixMilliseconds = expiry.ToUnixTimeMilliseconds()
        });
        await identity.SaveChangesAsync(ct);
        return new(device, human.Insert(4, "-"), Capabilities().VerificationUri!, expiry, PollIntervalSeconds);
    }

    public async Task<IntegrationPairingReview?> ReviewAsync(string human, ClaimsPrincipal principal, string remote, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(principal, ct);
        if (owner is null) throw new CredentialGrantException(null);
        if (!await TakeAttemptAsync($"code:{owner.UserId}", 30, ct)) throw new PairingRateLimitException();
        var pair = await FindHumanAsync(human, ct);
        if (pair is null || pair.State != "pending" || pair.ExpiresAtUnixMilliseconds <= Now.ToUnixTimeMilliseconds()) return null;
        var access = await accessService.ResolveAsync(principal, ct);
        var ownerAdministrationAvailable = pair.Purpose is "mcp" or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose &&
            access.IsHelpdeskAdmin && await identity.Users.AsNoTracking().AnyAsync(account => account.Id == owner.UserId && account.IsEnabled && account.IsInstanceAdministrator, ct);
        if (pair.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose && !ownerAdministrationAvailable)
            throw new CredentialGrantException(null);
        var organizations = await db.Organizations.AsNoTracking().Where(o => o.IsEnabled).OrderBy(o => o.Name).ToListAsync(ct);
        var allowed = organizations.Select(o => new IntegrationPairingOrganization(o.Id, o.Name,
            pair.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose ? [] : HelpdeskPermissions.AssignablePermissions.Where(p => access.IsHelpdeskAdmin || access.HasPermission(p, o.Id)).ToArray()))
            .Where(o => pair.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose ? access.IsHelpdeskAdmin || access.AllowedOrganizationIds.Contains(o.Id) : o.Permissions.Count > 0).ToArray();
        return new(NormalizeHuman(human).Insert(4, "-"), pair.Name, pair.Purpose is IntegrationCredentialIssuer.SetupVerificationPurpose or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose ? "mcp" : pair.Purpose, pair.McpResourceUri,
            DateTimeOffset.FromUnixTimeMilliseconds(pair.ExpiresAtUnixMilliseconds), allowed)
            {
                GrantKind = pair.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose ? "setup-verification" :
                    pair.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose ? IntegrationCredentialGrantKinds.OwnerAdministration : "ordinary",
                OwnerAdministrationAvailable = ownerAdministrationAvailable
            };
    }

    public async Task<bool> ApproveAsync(IntegrationPairingApprovalRequest request, ClaimsPrincipal principal, string remote, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(principal, ct);
        if (owner is null) throw new CredentialGrantException(null);
        if (!request.Consent) throw Invalid("consent", "Explicit approval is required.");
        if (!await TakeAttemptAsync($"code:{owner.UserId}", 30, ct)) throw new PairingRateLimitException();
        var pair = await FindHumanAsync(request.HumanCode, ct);
        if (pair is null || pair.State != "pending" || pair.ExpiresAtUnixMilliseconds <= Now.ToUnixTimeMilliseconds()) return false;
        if (pair.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose &&
            (request.GrantKind != "ordinary" || request.OwnerAdministrationConsent))
            throw Invalid("grantKind", "A temporary setup verification cannot grant owner administration.");
        var grantRequest = new IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest(pair.Name,
            pair.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose ? "mcp" : pair.Purpose,
            request.OrganizationId, request.Permissions, request.LifetimeDays)
        {
            McpResourceUri = pair.McpResourceUri, GrantKind = request.GrantKind,
            OwnerAdministrationConsent = request.OwnerAdministrationConsent
        };
        await ValidateGrantAsync(pair, principal, grantRequest, ct);
        if (pair.Purpose != IntegrationCredentialIssuer.SetupVerificationPurpose)
            pair.Purpose = request.GrantKind == IntegrationCredentialGrantKinds.OwnerAdministration
                ? IntegrationCredentialGrantKinds.OwnerAdministrationPurpose : grantRequest.Purpose;
        pair.OwnerUserId = owner.UserId;
        pair.ProtectedApproval = protector.Protect(JsonSerializer.Serialize(new ApprovalSnapshot(grantRequest,
            principal.Claims.Select(c => new StoredClaim(c.Type, c.Value)).ToArray())));
        pair.State = "approved";
        pair.Revision++;
        try { await identity.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); return false; }
    }

    public async Task<bool> DenyAsync(string human, ClaimsPrincipal principal, string remote, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(principal, ct);
        if (owner is null) throw new CredentialGrantException(null);
        if (!await TakeAttemptAsync($"code:{owner.UserId}", 30, ct)) throw new PairingRateLimitException();
        var pair = await FindHumanAsync(human, ct);
        if (pair is null || pair.State != "pending" || pair.ExpiresAtUnixMilliseconds <= Now.ToUnixTimeMilliseconds()) return false;
        pair.State = "denied"; pair.Revision++;
        try { await identity.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); return false; }
    }

    public async Task<IntegrationPairingPoll> PollAsync(string device, CancellationToken ct)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            var pair = await FindDeviceAsync(device, ct);
            if (pair is null) return new("expired");
            var now = Now.ToUnixTimeMilliseconds();
            try { if (await ExpireAsync(pair, now, ct)) return new("expired"); }
            catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); continue; }
            if (pair.State is "consumed" or "denied" or "cancelled" or "expired") return new(pair.State);
            if (pair.LastPollAtUnixMilliseconds is { } last && now - last < PollIntervalSeconds * 1000)
                return new("slow_down");
            pair.LastPollAtUnixMilliseconds = now;
            pair.Revision++;
            IntegrationCredentialEndpoints.CreatedIntegrationCredential? delivery = null;
            if (pair.State is "approved" or "issued")
            {
                try
                {
                    var approval = JsonSerializer.Deserialize<ApprovalSnapshot>(protector.Unprotect(pair.ProtectedApproval!))!;
                    var principal = new ClaimsPrincipal(new ClaimsIdentity(approval.Claims.Select(c => new Claim(c.Type, c.Value)), "PairingApproval"));
                    var grant = await ValidateGrantAsync(pair, principal, approval.Request, ct);
                    if (grant.OwnerUserId != pair.OwnerUserId) throw new CredentialGrantException(null);
                    if (pair.State == "approved")
                    {
                        var issued = issuer.Create(grant, Now);
                        var protectedDelivery = protector.Protect(JsonSerializer.Serialize(issued.Delivery));
                        issuer.Track(issued.Record);
                        delivery = issued.Delivery;
                        pair.CredentialId = issued.Record.Id;
                        pair.ProtectedDelivery = protectedDelivery;
                        pair.DeliveryExpiresAtUnixMilliseconds = Math.Min(pair.ExpiresAtUnixMilliseconds, Now.AddMinutes(2).ToUnixTimeMilliseconds());
                        pair.State = "issued";
                    }
                    else
                    {
                        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == pair.CredentialId, ct);
                        if (credential is null || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= Now)
                            throw new CredentialGrantException(null);
                        delivery = JsonSerializer.Deserialize<IntegrationCredentialEndpoints.CreatedIntegrationCredential>(protector.Unprotect(pair.ProtectedDelivery!));
                    }
                }
                catch (Exception error) when (error is CredentialGrantException or CryptographicException or JsonException)
                {
                    await RevokeUnacknowledgedAsync(pair, ct);
                    delivery = null;
                    pair.State = "denied"; pair.ProtectedApproval = null; pair.ProtectedDelivery = null;
                }
            }
            try
            {
                // Optimistic row revision and credential INSERT are one EF transaction on both providers.
                await identity.SaveChangesAsync(ct);
                if (delivery is not null) await RecordIssuanceAuditAsync(pair, delivery, ct);
                return new(pair.State == "approved" ? "pending" : pair.State, PollIntervalSeconds, delivery,
                    pair.DeliveryExpiresAtUnixMilliseconds is { } deadline ? DateTimeOffset.FromUnixTimeMilliseconds(deadline) : null);
            }
            catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); }
        }
        return new("slow_down");
    }

    public async Task<bool> AckAsync(string device, Guid credentialId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var pair = await FindDeviceAsync(device, ct);
            if (pair is null || pair.CredentialId != credentialId) return false;
            // A tombstone never acknowledges access that has since been revoked or deleted.
            var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == credentialId, ct);
            if (credential is null || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= Now) return false;
            if (pair.State == "consumed") return true; // Durable retry after a lost acknowledgement response.
            try { if (await ExpireAsync(pair, Now.ToUnixTimeMilliseconds(), ct) || pair.State != "issued") return false; }
            catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); continue; }
            pair.State = "consumed"; pair.ProtectedDelivery = null; pair.ProtectedApproval = null; pair.Revision++;
            try { await identity.SaveChangesAsync(ct); return true; }
            catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); }
        }
        return false;
    }

    public async Task CancelAsync(string device, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var pair = await FindDeviceAsync(device, ct);
            if (pair is null || (pair.State == "consumed" && pair.Purpose != IntegrationCredentialIssuer.SetupVerificationPurpose) || pair.State is "cancelled" or "expired" or "denied") return;
            await RevokeUnacknowledgedAsync(pair, ct);
            pair.State = "cancelled"; pair.ProtectedApproval = null; pair.ProtectedDelivery = null; pair.Revision++;
            try { await identity.SaveChangesAsync(ct); return; }
            catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); }
        }
        throw new PairingRateLimitException();
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        var now = Now.ToUnixTimeMilliseconds();
        var expired = await identity.IntegrationCredentialPairings.Where(p =>
            (p.ExpiresAtUnixMilliseconds <= now || p.DeliveryExpiresAtUnixMilliseconds <= now) &&
            (p.State == "pending" || p.State == "approved" || p.State == "issued" || (p.State == "consumed" && p.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose))).Select(p => p.Id).Take(100).ToListAsync(ct);
        foreach (var id in expired)
        {
            var pair = await identity.IntegrationCredentialPairings.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (pair is null) continue;
            try { await ExpireAsync(pair, now, ct); }
            catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); }
        }
        // Retain consumed tombstones for one day so acknowledgement retries remain idempotent.
        await identity.IntegrationCredentialPairings.Where(p => p.ExpiresAtUnixMilliseconds < now - 86400000 &&
            (p.State == "expired" || p.State == "consumed" || p.State == "denied" || p.State == "cancelled")).ExecuteDeleteAsync(ct);
        await identity.IntegrationCredentialPairingRateLimits.Where(p => p.WindowEndsAtUnixMilliseconds < now).ExecuteDeleteAsync(ct);
    }

    private async Task RecordIssuanceAuditAsync(IntegrationCredentialPairing pair,
        IntegrationCredentialEndpoints.CreatedIntegrationCredential delivery, CancellationToken ct)
    {
        var auditId = pair.Id.ToString();
        if (await db.ActivityLogs.AnyAsync(a => a.Id == auditId, ct)) return;
        db.ActivityLogs.Add(new Helpdesk.Shared.Models.ActivityLog
        {
            Id = auditId, UserId = pair.OwnerUserId!, RelatedEntityId = delivery.Id.ToString("N"),
            Message = $"Browser-approved {(pair.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose ? "temporary setup verification" : "integration")} credential created. Prefix={delivery.Prefix}; Purpose={delivery.Purpose}; OrganizationId={delivery.OrganizationId}; Permissions={string.Join(' ', delivery.Permissions)}; ExpiresAtUtc={delivery.ExpiresAtUtc:O}."
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (IsUniqueViolation(error)) { db.ChangeTracker.Clear(); }
    }

    private static bool IsUniqueViolation(DbUpdateException error) =>
        error.InnerException is Npgsql.PostgresException { SqlState: "23505" } ||
        error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };

    private async Task<bool> ExpireAsync(IntegrationCredentialPairing pair, long now, CancellationToken ct)
    {
        if ((pair.State == "consumed" && pair.Purpose != IntegrationCredentialIssuer.SetupVerificationPurpose) || pair.State is "cancelled" or "denied" or "expired") return pair.State == "expired";
        if (pair.ExpiresAtUnixMilliseconds > now && (pair.DeliveryExpiresAtUnixMilliseconds is null || pair.DeliveryExpiresAtUnixMilliseconds > now)) return false;
        await RevokeUnacknowledgedAsync(pair, ct);
        pair.State = "expired"; pair.ProtectedDelivery = null; pair.ProtectedApproval = null; pair.Revision++;
        await identity.SaveChangesAsync(ct);
        return true;
    }

    private async Task RevokeUnacknowledgedAsync(IntegrationCredentialPairing pair, CancellationToken ct)
    {
        if ((pair.State == "consumed" && pair.Purpose != IntegrationCredentialIssuer.SetupVerificationPurpose) || pair.CredentialId is null) return;
        var credential = await identity.IntegrationCredentials.SingleOrDefaultAsync(c => c.Id == pair.CredentialId, ct);
        if (credential is not null && credential.RevokedAtUtc is null) credential.RevokedAtUtc = Now;
    }

    private Task<IntegrationCredentialPairing?> FindDeviceAsync(string device, CancellationToken ct) =>
        device?.Length != 64 ? Task.FromResult<IntegrationCredentialPairing?>(null) :
        identity.IntegrationCredentialPairings.SingleOrDefaultAsync(p => p.DeviceHash == Hash(device) && p.Purpose != "http-host", ct);
    private Task<IntegrationCredentialPairing?> FindHumanAsync(string human, CancellationToken ct) =>
        NormalizeHuman(human).Length != 8 ? Task.FromResult<IntegrationCredentialPairing?>(null) :
        identity.IntegrationCredentialPairings.SingleOrDefaultAsync(p => p.HumanHash == Hash(NormalizeHuman(human)) && p.Purpose != "http-host", ct);
    private static string NormalizeHuman(string? human) => (human ?? "").Trim().Replace("-", "").ToUpperInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal async Task<bool> TakeAttemptAsync(string subject, int limit, CancellationToken ct)
    {
        var window = Now.ToUnixTimeMilliseconds() / 600000;
        var key = Hash($"{subject}:{window}");
        for (var retry = 0; retry < 3; retry++)
        {
            var rate = await identity.IntegrationCredentialPairingRateLimits.SingleOrDefaultAsync(r => r.Key == key, ct);
            if (rate is null) identity.IntegrationCredentialPairingRateLimits.Add(rate = new() { Key = key, WindowEndsAtUnixMilliseconds = (window + 1) * 600000 });
            if (rate.Attempts >= limit) return false;
            rate.Attempts++; rate.Revision++;
            try { await identity.SaveChangesAsync(ct); return true; }
            catch (DbUpdateException error) when (error is DbUpdateConcurrencyException || IsUniqueViolation(error)) { identity.ChangeTracker.Clear(); }
        }
        return false;
    }

    private Task<ValidatedCredentialGrant> ValidateGrantAsync(IntegrationCredentialPairing pair, ClaimsPrincipal principal,
        IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest request, CancellationToken ct) =>
        pair.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose
            ? issuer.ValidateSetupVerificationAsync(principal, request, ct)
            : issuer.ValidateAsync(principal, request, ct);

    private static CredentialGrantException Invalid(string key, string message) => new(new() { [key] = [message] });
    private sealed record StoredClaim(string Type, string Value);
    private sealed record ApprovalSnapshot(IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest Request, StoredClaim[] Claims);
}

public sealed record IntegrationPairingPoll(string Status, int PollIntervalSeconds = IntegrationCredentialPairingService.PollIntervalSeconds,
    IntegrationCredentialEndpoints.CreatedIntegrationCredential? Credential = null, DateTimeOffset? DeliveryExpiresAtUtc = null);
public sealed class PairingRateLimitException : Exception;
public sealed class PairingUnavailableException : Exception;

public sealed class IntegrationCredentialPairingCleanup(IServiceScopeFactory scopes, ILogger<IntegrationCredentialPairingCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IntegrationCredentialPairingService>().CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Pairing cleanup deferred ({Category}).", error.GetType().Name); }
        }
    }
}
