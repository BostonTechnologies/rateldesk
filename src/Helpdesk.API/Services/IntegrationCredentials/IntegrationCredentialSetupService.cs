using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.McpPolicies;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services.IntegrationCredentials;

public sealed record IntegrationCredentialSetupRequest(Guid RequestId,
    IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest Credential,
    McpPolicyScopeDraft[] Policies, bool Approved);
public sealed record IntegrationCredentialSetupDelivery(Guid RequestId,
    IntegrationCredentialEndpoints.CreatedIntegrationCredential Credential, DateTimeOffset HandoffExpiresAtUtc);

/// <summary>Commits reviewed grants, policy and recoverable delivery in the shared database transaction.</summary>
public sealed class IntegrationCredentialSetupService(
    IIntegrationCredentialOwnerResolver owners, ICurrentUserAccessService access,
    HelpdeskDbContext db, RatelDeskIdentityDbContext identity,
    IDataProtectionProvider protection, TimeProvider clock)
{
    public static readonly TimeSpan HandoffLifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector protector = protection.CreateProtector("RatelDesk.IntegrationSetupDelivery.v1");
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<IntegrationCredentialSetupDelivery> CreateAsync(IntegrationCredentialSetupRequest? request,
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(principal, ct) ?? throw new CredentialGrantException(null);
        if (request is not { Approved: true, Credential: not null } || request.RequestId == Guid.Empty ||
            request.Credential.Purpose != "mcp")
            throw Failure(400, "setup_review_required", "Review and approve an HTTP MCP integration before creating it.", "review");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        var previous = await identity.IntegrationCredentialSetupAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
        if (previous is not null)
            return await ReplayAsync(previous, owner.UserId, hash, request, principal, ct);

        var issuer = new IntegrationCredentialIssuer(owners, access, db, identity, clock);
        var grant = await issuer.ValidateAsync(principal, request.Credential, ct);
        var now = Now;
        var (credential, delivery) = issuer.Create(grant, now);
        var policyError = McpPolicyDefinitionService.ValidateScopes(credential, request.Policies, now.UtcDateTime);
        if (policyError is not null) throw Failure(400, "setup_policy_invalid", policyError, "policies");
        var (policy, audit) = McpPolicyDefinitionService.Create(credential, request.Policies, owner.UserId, now.UtcDateTime);
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(
            now.Add(HandoffLifetime).ToUnixTimeMilliseconds(), credential.ExpiresAtUtc.ToUnixTimeMilliseconds()));
        var attempt = new IntegrationCredentialSetupAttempt
        {
            RequestId = request.RequestId, OwnerUserId = owner.UserId, CredentialId = credential.Id,
            RequestHash = hash, State = "Delivered", Revision = 1,
            DeliveryExpiresAtUnixMilliseconds = deadline.ToUnixTimeMilliseconds(),
            ProtectedDelivery = protector.Protect(JsonSerializer.Serialize(delivery))
        };

        // Infrastructure registration binds both models to exactly the same database.
        // Never redirect a separately configured context merely to obtain a transaction.
        if (identity.Database.ProviderName != db.Database.ProviderName ||
            identity.Database.GetConnectionString() != db.Database.GetConnectionString())
            throw Failure(503, "setup_database_binding", "Integration setup requires the configured shared RatelDesk database.", "persistence");

        await using var transaction = await identity.Database.BeginTransactionAsync(ct);
        identity.IntegrationCredentials.Add(credential);
        identity.IntegrationCredentialSetupAttempts.Add(attempt);
        await identity.SaveChangesAsync(ct);
        // The identity transaction owns these parameterized writes, as it already does
        // for reviewed credential-access audits. Neither context commits a partial setup.
        var policyExpiry = DateTime.SpecifyKind(policy.ExpiresUtc, DateTimeKind.Unspecified);
        var occurred = DateTime.SpecifyKind(audit.OccurredUtc, DateTimeKind.Unspecified);
        await identity.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "McpAccessPolicy" ("CredentialId", "OrganizationId", "PermissionsJson", "ExpiresUtc", "Revision", "State", "DefinitionJson")
            VALUES ({policy.CredentialId}, {policy.OrganizationId}, {policy.PermissionsJson}, {policyExpiry}, {policy.Revision}, {policy.State}, {policy.DefinitionJson})
            """, ct);
        await identity.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "McpAccessPolicyAudit" ("Id", "CredentialId", "Revision", "Actor", "OccurredUtc", "DefinitionJson")
            VALUES ({audit.Id}, {audit.CredentialId}, {audit.Revision}, {audit.Actor}, {occurred}, {audit.DefinitionJson})
            """, ct);
        var activity = new ActivityLog
        {
            UserId = owner.UserId, RelatedEntityId = credential.Id.ToString("N"),
            Message = $"Integration setup created after owner review. Prefix={credential.Prefix}; Purpose={credential.Purpose}; OrganizationId={credential.OrganizationId}; PolicyRevision=1."
        };
        await identity.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ActivityLogs" ("Id", "RelatedEntityId", "Timestamp", "UserId", "Message")
            VALUES ({activity.Id}, {activity.RelatedEntityId}, {occurred}, {activity.UserId}, {activity.Message})
            """, ct);
        await transaction.CommitAsync(ct);
        return new(request.RequestId, delivery, deadline);
    }

    private async Task<IntegrationCredentialSetupDelivery> ReplayAsync(IntegrationCredentialSetupAttempt attempt,
        string owner, string hash, IntegrationCredentialSetupRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (attempt.OwnerUserId != owner) throw Failure(404, "setup_not_found", "This setup is not available for your account.", "delivery");
        if (attempt.RequestHash != hash) throw Failure(409, "setup_request_conflict", "This setup reference already belongs to another approved draft. Resume the original draft.", "review");
        if (attempt.State == "Acknowledged") throw Failure(409, "setup_acknowledged", "The credential delivery has already been acknowledged.", "delivery");
        if (attempt.State == "Expired" || attempt.DeliveryExpiresAtUnixMilliseconds <= Now.ToUnixTimeMilliseconds())
        {
            var state = await ExpireAsync(attempt, ct);
            if (state == "Acknowledged") throw Failure(409, "setup_acknowledged", "The credential delivery has already been acknowledged.", "delivery");
            if (state != "Expired") throw Failure(409, "setup_in_progress", "The setup changed. Retry this same approved request.", "delivery");
            throw Failure(410, "setup_delivery_expired", "The unacknowledged delivery expired. Review the integration list before starting another setup.", "delivery");
        }
        if (attempt.State != "Delivered" || string.IsNullOrEmpty(attempt.ProtectedDelivery))
            throw Failure(409, "setup_in_progress", "The approved setup is not ready for delivery. Retry this same request.", "delivery");
        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attempt.CredentialId, ct);
        if (credential is null || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= Now)
            throw Failure(410, "setup_revoked", "The integration was revoked, deleted or expired before delivery.", "delivery");
        var issuer = new IntegrationCredentialIssuer(owners, access, db, identity, clock);
        var current = await issuer.ValidateAsync(principal, request.Credential with { ExpiresAtUtc = credential.ExpiresAtUtc, LifetimeDays = null }, ct);
        if (current.OwnerUserId != credential.OwnerUserId || current.Name != credential.Name || current.Purpose != credential.Purpose ||
            current.OrganizationId != credential.OrganizationId || current.McpResourceUri != credential.McpResourceUri ||
            !current.Permissions.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(credential.Permissions.Split(' ').Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw Failure(409, "setup_credential_changed", "The integration changed after review. Inspect its current access before continuing.", "delivery");
        var policy = await db.Set<McpAccessPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId == credential.Id, ct);
        if (policy is not { State: "Active", Revision: 1 } || policy.ExpiresUtc <= Now.UtcDateTime)
            throw Failure(409, "setup_policy_changed", "The integration policy changed after creation. Inspect its current policy before continuing.", "delivery");
        IntegrationCredentialEndpoints.CreatedIntegrationCredential? delivery;
        try { delivery = JsonSerializer.Deserialize<IntegrationCredentialEndpoints.CreatedIntegrationCredential>(protector.Unprotect(attempt.ProtectedDelivery)); }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { throw Failure(503, "setup_delivery_unavailable", "The protected delivery cannot currently be opened. Restore the API data-protection key ring and retry this setup.", "delivery"); }
        if (delivery is null || delivery.Id != credential.Id)
            throw Failure(503, "setup_delivery_unavailable", "The protected delivery is not available. Retry this setup after checking the API key ring.", "delivery");
        var deadline = Now.ToUnixTimeMilliseconds();
        if (!await identity.IntegrationCredentialSetupAttempts.AsNoTracking().AnyAsync(x =>
                x.RequestId == attempt.RequestId && x.Revision == attempt.Revision && x.State == "Delivered" &&
                x.DeliveryExpiresAtUnixMilliseconds > deadline, ct))
            throw Failure(409, "setup_in_progress", "The delivery changed or reached its deadline. Retry this same approved request to read its current state.", "delivery");
        return new(attempt.RequestId, delivery, DateTimeOffset.FromUnixTimeMilliseconds(attempt.DeliveryExpiresAtUnixMilliseconds));
    }

    public async Task AcknowledgeAsync(Guid requestId, ClaimsPrincipal principal, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(principal, ct) ?? throw new CredentialGrantException(null);
        var attempt = await identity.IntegrationCredentialSetupAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == requestId && x.OwnerUserId == owner.UserId, ct)
            ?? throw Failure(404, "setup_not_found", "This setup is not available for your account.", "acknowledgement");
        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attempt.CredentialId, ct);
        if (credential is null || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= Now)
            throw Failure(410, "setup_revoked", "This integration is no longer active.", "acknowledgement");
        if (attempt.State == "Acknowledged") return;
        if (attempt.State == "Expired" || attempt.DeliveryExpiresAtUnixMilliseconds <= Now.ToUnixTimeMilliseconds())
        {
            var state = await ExpireAsync(attempt, ct);
            if (state == "Acknowledged") return;
            if (state != "Expired") throw Failure(409, "setup_in_progress", "The setup changed. Retry acknowledgement.", "acknowledgement");
            throw Failure(410, "setup_delivery_expired", "The delivery expired before acknowledgement. The integration has been revoked.", "acknowledgement");
        }
        var deadline = Now.ToUnixTimeMilliseconds();
        var changed = await identity.IntegrationCredentialSetupAttempts
            .Where(x => x.RequestId == requestId && x.OwnerUserId == owner.UserId && x.Revision == attempt.Revision && x.State == "Delivered" &&
                x.DeliveryExpiresAtUnixMilliseconds > deadline && identity.Users.Any(user => user.Id == x.OwnerUserId && user.IsEnabled))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, "Acknowledged")
                .SetProperty(x => x.ProtectedDelivery, (string?)null).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        if (changed != 1)
        {
            var currentState = await identity.IntegrationCredentialSetupAttempts.AsNoTracking()
                .Where(x => x.RequestId == requestId && x.OwnerUserId == owner.UserId).Select(x => x.State).SingleOrDefaultAsync(ct);
            if (currentState != "Acknowledged")
                throw Failure(409, "setup_in_progress", "The setup changed while acknowledging. Retry acknowledgement.", "acknowledgement");
        }
    }

    private async Task<string?> ExpireAsync(IntegrationCredentialSetupAttempt attempt, CancellationToken ct)
    {
        await using var transaction = await identity.Database.BeginTransactionAsync(ct);
        var changed = await identity.IntegrationCredentialSetupAttempts
            .Where(x => x.RequestId == attempt.RequestId && x.Revision == attempt.Revision && x.State == "Delivered")
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, "Expired")
                .SetProperty(x => x.ProtectedDelivery, (string?)null).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        if (changed == 1)
            await identity.IntegrationCredentials.Where(x => x.Id == attempt.CredentialId && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAtUtc, Now), ct);
        await transaction.CommitAsync(ct);
        return changed == 1 ? "Expired" : await identity.IntegrationCredentialSetupAttempts.AsNoTracking()
            .Where(x => x.RequestId == attempt.RequestId).Select(x => x.State).SingleOrDefaultAsync(ct);
    }

    private static IntegrationSetupException Failure(int status, string code, string message, string stage) => new(status, code, message, stage);
}

public sealed class IntegrationSetupException(int statusCode, string code, string message, string stage) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string Stage { get; } = stage;
}
