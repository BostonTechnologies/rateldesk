using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services.IntegrationCredentials;

/// <summary>Reviewed changes to an existing ordinary credential, using the credential issuer's current authority.</summary>
public sealed class IntegrationCredentialAccessService(
    IIntegrationCredentialOwnerResolver owners,
    ICurrentUserAccessService accessService,
    HelpdeskDbContext db,
    RatelDeskIdentityDbContext identity,
    IDataProtectionProvider protection,
    TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("RatelDesk.IntegrationCredentialAccessReview.v1");
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<IntegrationCredentialAccessDetails> GetAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var credential = await OwnedAsync(id, principal, ct);
        return await DetailsAsync(credential, principal, ct);
    }

    public async Task<IntegrationCredentialAccessReview> ReviewAsync(Guid id, IntegrationCredentialAccessDraft? draft,
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var credential = await OwnedAsync(id, principal, ct);
        if (draft is null || string.IsNullOrWhiteSpace(draft.ExpectedVersion))
            throw Invalid("Load the current credential access before reviewing a change.");
        if (draft.ExpectedVersion != Version(credential))
            throw Conflict("Credential access changed. Refresh the current access and review your draft again.");

        var permissions = await ValidateAsync(credential, draft.Permissions, principal, ct);
        var current = Permissions(credential);
        var expiry = Now.AddMinutes(5);
        var receipt = new ReviewReceipt(credential.Id, credential.OwnerUserId, Version(credential),
            Version(credential, permissions), permissions, expiry);
        return new(protector.Protect(JsonSerializer.Serialize(receipt)), permissions,
            permissions.Except(current, StringComparer.OrdinalIgnoreCase).ToArray(),
            current.Except(permissions, StringComparer.OrdinalIgnoreCase).ToArray(), expiry);
    }

    public async Task<IntegrationCredentialAccessDetails> ApplyAsync(Guid id, ApplyIntegrationCredentialAccess? request,
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var credential = await OwnedAsync(id, principal, ct);
        if (request is not { Consent: true } || string.IsNullOrWhiteSpace(request.Confirmation) || request.Confirmation.Length > 16384)
            throw Invalid("Review the exact permission changes and explicitly confirm them before applying.");

        ReviewReceipt? receipt;
        try { receipt = JsonSerializer.Deserialize<ReviewReceipt>(protector.Unprotect(request.Confirmation)); }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        { throw Invalid("This review is invalid. Review your draft again."); }
        if (receipt is null || receipt.CredentialId != id || receipt.OwnerUserId != credential.OwnerUserId || receipt.ExpiresAtUtc <= Now)
            throw Invalid("This review has expired or belongs to another credential. Review your draft again.");

        // Recheck current account and organization authority even after a successful preview.
        var permissions = await ValidateAsync(credential, receipt.Permissions, principal, ct);
        var version = Version(credential);
        if (version == receipt.AppliedVersion)
            return await DetailsAsync(credential, principal, ct); // Confirmed replay after a lost response.
        if (version != receipt.ExpectedVersion)
            throw Conflict("Credential access changed after review. Refresh the current access and review your draft again.");

        var updatedPermissions = string.Join(' ', permissions);
        await using var transaction = await identity.Database.BeginTransactionAsync(ct);
        // Compare the complete credential binding and original grants. Last-use updates do not
        // invalidate a review; revocation, deletion and concurrent access changes do.
        var updated = await identity.IntegrationCredentials.Where(candidate =>
                candidate.Id == id && candidate.OwnerUserId == credential.OwnerUserId &&
                candidate.RevokedAtUtc == null && candidate.Permissions == credential.Permissions &&
                candidate.Purpose == credential.Purpose && candidate.OrganizationId == credential.OrganizationId &&
                candidate.McpResourceUri == credential.McpResourceUri && candidate.ExpiresAtUtc == credential.ExpiresAtUtc &&
                candidate.Name == credential.Name && candidate.Prefix == credential.Prefix &&
                candidate.CreatedAtUtc == credential.CreatedAtUtc && candidate.SecretHash == credential.SecretHash &&
                identity.Users.Any(account => account.Id == candidate.OwnerUserId && account.IsEnabled))
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Permissions, updatedPermissions), ct);
        if (updated != 1)
            throw Conflict("The credential changed or was revoked. Refresh the current access and review your draft again.");

        var audit = new ActivityLog
        {
            UserId = credential.OwnerUserId, RelatedEntityId = id.ToString("N"),
            Message = $"Integration credential access updated after owner review. Prefix={credential.Prefix}; Purpose={credential.Purpose}; OrganizationId={credential.OrganizationId}; PreviousPermissions={credential.Permissions}; Permissions={updatedPermissions}."
        };
        // Domain and identity contexts share the database. Audit and grants commit together on
        // the identity connection, matching the existing credential-deletion lifecycle.
        await identity.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ActivityLogs" ("Id", "RelatedEntityId", "Timestamp", "UserId", "Message")
            VALUES ({audit.Id}, {audit.RelatedEntityId}, {audit.Timestamp}, {audit.UserId}, {audit.Message})
            """, ct);
        await transaction.CommitAsync(ct);
        return await DetailsAsync(await OwnedAsync(id, principal, ct), principal, ct);
    }

    private async Task<IntegrationCredential> OwnedAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        // Explicitly require an interactive account. A delegated owner credential cannot edit
        // itself or siblings, even though it can list and revoke credentials elsewhere.
        var owner = await owners.ResolveAsync(principal, ct);
        if (owner is null) throw new CredentialGrantException(null);
        return await identity.IntegrationCredentials.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OwnerUserId == owner.UserId, ct)
            ?? throw new CredentialAccessException(StatusCodes.Status404NotFound, "The credential is no longer available.");
    }

    private async Task<string[]> ValidateAsync(IntegrationCredential credential, IReadOnlyList<string>? requested,
        ClaimsPrincipal principal, CancellationToken ct)
    {
        if (UnavailableReason(credential) is { } reason) throw Conflict(reason);
        if (requested is null || requested.Count is 0 or > 100 || requested.Any(permission => string.IsNullOrWhiteSpace(permission) || permission.Length > 128))
            throw Invalid("Select at least one supported permission for this organization.");
        var issuer = new IntegrationCredentialIssuer(owners, accessService, db, identity);
        var validated = await issuer.ValidateAsync(principal,
            new IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest(credential.Name, credential.Purpose,
                credential.OrganizationId!, requested, null) { McpResourceUri = credential.McpResourceUri }, ct);
        if (validated.OwnerUserId != credential.OwnerUserId) throw new CredentialGrantException(null);
        return validated.Permissions.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IntegrationCredentialAccessDetails> DetailsAsync(IntegrationCredential credential,
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == credential.OrganizationId, ct);
        var access = await accessService.ResolveAsync(principal, ct);
        var available = organization?.IsEnabled == true
            ? HelpdeskPermissions.AssignablePermissions.Where(p => access.HasPermission(p, credential.OrganizationId)).ToArray()
            : [];
        var unavailable = UnavailableReason(credential) ?? (organization?.IsEnabled != true
            ? "The credential's organization must be enabled before access can be edited."
            : available.Length == 0 ? "Your account does not currently have permissions to grant in this organization." : null);
        var configured = Permissions(credential);
        var policy = await db.Set<McpAccessPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId == credential.Id, ct);
        var policyState = policy?.State ?? "None";
        var policySummary = policy is null
            ? "No additional MCP policy is configured. Credential grants and the owner's current permissions determine access."
            : $"Policy revision {policy.Revision} is {policy.State.ToLowerInvariant()} and expires {policy.ExpiresUtc:O}. Permission edits do not change this policy.";
        if (policy is { State: "Active" } && policy.ExpiresUtc <= Now.UtcDateTime)
        {
            policyState = "Expired";
            policySummary = $"MCP policy revision {policy.Revision} has expired. Access remains denied until an authorized administrator updates the policy.";
        }
        var definition = policy is null ? null : McpPolicyDefinitionDocumentReader.Read(policy.DefinitionJson);
        var basePolicy = definition?.Scopes.Single();
        var allowedByPolicy = policy is null ? null : basePolicy?.Permissions ?? (JsonSerializer.Deserialize<string[]>(policy.PermissionsJson) ?? []);
        var ownerAdministration = credential.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose;
        if (ownerAdministration && policy is not null && string.IsNullOrEmpty(policy.DefinitionJson))
            policySummary += " Owner administration requires an instance-administrator context. This legacy organization policy removes that context, so MCP execution is denied.";
        var policyActive = policy is null || policy.State == "Active" && policy.ExpiresUtc > Now.UtcDateTime &&
            (string.IsNullOrEmpty(policy.DefinitionJson) || basePolicy is { Effect: "Allow" } &&
                basePolicy.ExpiresUtc > Now.UtcDateTime && definition!.Resource == credential.McpResourceUri);
        if (basePolicy is not null)
            policySummary += $" {basePolicy.Name}: {basePolicy.Operations.Length} selected operations and {basePolicy.Exceptions.Length} nested exceptions. Each request must also satisfy those operation rules.";
        var scopeMatches = policy is null || (ownerAdministration
            ? basePolicy is { Scope: "server", OrganizationId: null }
            : (basePolicy?.Scope is null or "organization") && (basePolicy?.OrganizationId ?? policy.OrganizationId) == credential.OrganizationId);
        var handoffUsable = await IntegrationCredentialSetupAuthorization.IsUsableAsync(identity, credential.Id, Now, ct);
        var active = credential.RevokedAtUtc is null && credential.ExpiresAtUtc > Now && handoffUsable && policyActive && scopeMatches &&
            (ownerAdministration ? access.IsHelpdeskAdmin && configured.SequenceEqual([HelpdeskPermissions.HelpdeskAdmin]) : organization?.IsEnabled == true);
        var effective = active ? configured.Where(permission => access.HasPermission(permission, credential.OrganizationId) &&
            (policy is null || allowedByPolicy!.Contains(permission, StringComparer.OrdinalIgnoreCase))).ToArray() : [];
        return new(credential.Id, credential.OrganizationId, organization?.Name, configured, available, effective,
            configured.Except(effective, StringComparer.OrdinalIgnoreCase).ToArray(), Version(credential), unavailable is null,
            unavailable, policyState, policySummary) { RevokedAtUtc = credential.RevokedAtUtc };
    }

    private string? UnavailableReason(IntegrationCredential credential) => credential switch
    {
        { RevokedAtUtc: not null } => "Revoked credentials cannot be edited.",
        _ when credential.ExpiresAtUtc <= Now => "Expired credentials cannot be edited.",
        { Purpose: not ("api" or "mcp") } => "Owner administration and setup verification grants use their dedicated consent flow and cannot be changed here.",
        _ => null
    };

    private static string[] Permissions(IntegrationCredential credential) => credential.Permissions
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string Version(IntegrationCredential credential, IReadOnlyList<string>? permissions = null) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            credential.Id, credential.OwnerUserId, credential.Name, credential.Prefix, credential.Purpose, credential.SecretHash,
            credential.OrganizationId, credential.McpResourceUri, credential.CreatedAtUtc, credential.ExpiresAtUtc,
            credential.RevokedAtUtc, Permissions = permissions is null ? credential.Permissions : string.Join(' ', permissions)
        }))));

    private sealed record ReviewReceipt(Guid CredentialId, string OwnerUserId, string ExpectedVersion,
        string AppliedVersion, string[] Permissions, DateTimeOffset ExpiresAtUtc);
    private static CredentialAccessException Invalid(string message) => new(StatusCodes.Status400BadRequest, message);
    private static CredentialAccessException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
}

public sealed class CredentialAccessException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

