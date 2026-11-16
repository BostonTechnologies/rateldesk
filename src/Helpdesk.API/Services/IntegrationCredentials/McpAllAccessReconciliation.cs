using System.Data;
using System.Text.Json;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Helpdesk.API.Services.IntegrationCredentials;

/// <summary>Preserves reviewed full-catalog intent across upgrades and leaves custom policies unchanged.</summary>
public sealed class McpAllAccessReconciliation(HelpdeskDbContext db, RatelDeskIdentityDbContext identity,
    TimeProvider clock)
{
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!IntegrationDatabaseBinding.IsShared(db, identity))
            throw new InvalidOperationException("MCP catalog reconciliation requires the configured shared application database.");
        await using var transaction = await identity.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (identity.Database.IsNpgsql())
            await identity.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"rateldesk:all-access-catalog"}, 0))", ct);
        db.Database.SetDbConnection(identity.Database.GetDbConnection(), contextOwnsConnection: false);
        await using var enlistment = await db.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct);
        var now = clock.GetUtcNow();
        var policies = await db.Set<McpAccessPolicy>().AsTracking().Where(p => p.State == "Active" && p.ExpiresUtc > now.UtcDateTime).ToListAsync(ct);
        foreach (var policy in policies)
        {
            var definition = McpPolicyDefinitionDocumentReader.Read(policy.DefinitionJson);
            if (definition?.Scopes.Single() is not { Effect: "Allow" } scope || scope.ExpiresUtc <= now.UtcDateTime) continue;
            var credential = await identity.IntegrationCredentials.AsTracking().SingleOrDefaultAsync(c => c.Id == policy.CredentialId, ct);
            if (credential is null || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= now ||
                credential.McpResourceUri != definition.Resource || credential.ExpiresAtUtc.UtcDateTime < scope.ExpiresUtc ||
                credential.Purpose is not ("mcp" or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose) ||
                (credential.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose
                    ? scope.Scope != "server" || scope.OrganizationId is not null
                    : scope.Scope != "organization" || scope.OrganizationId != credential.OrganizationId)) continue;
            var legacy = !scope.AllAccess && await IsLegacyPresetAsync(policy, scope, credential, ct);
            if (!scope.AllAccess && !legacy) continue;
            var granted = McpPolicyDefinitionService.GrantedPermissions(credential).ToHashSet(StringComparer.Ordinal);
            var policyPermissions = scope.Permissions.ToHashSet(StringComparer.Ordinal);
            var fingerprint = McpPolicyDefinitionService.CatalogFingerprint(granted);
            if (legacy) scope = scope with { AllAccess = true, PermissionCatalogSnapshot = fingerprint };
            var changed = legacy;
            var snapshot = scope.PermissionCatalogSnapshot;
            if (snapshot is not null && (snapshot != fingerprint || snapshot != McpPolicyDefinitionService.CatalogFingerprint(policyPermissions)))
            { snapshot = null; changed = true; }
            if (snapshot is not null)
            {
                foreach (var permission in McpPolicyDefinitionService.FullAccessPermissions(credential).Except(granted).ToArray())
                {
                    // Configured full-catalog intent grows here; effective access still
                    // intersects it with current owner/account authority on every request.
                    granted.Add(permission); policyPermissions.Add(permission); changed = true;
                }
                snapshot = McpPolicyDefinitionService.CatalogFingerprint(granted);
            }
            var operations = McpPolicyDefinitionService.Operations.Select(op => op.Key).Order(StringComparer.Ordinal).ToArray();
            changed |= !scope.Operations.ToHashSet(StringComparer.Ordinal).SetEquals(operations);
            if (!changed) continue;
            var previousPermissions = credential.Permissions;
            credential.Permissions = string.Join(' ', granted.Order(StringComparer.Ordinal));
            scope = scope with { Operations = operations, PermissionCatalogSnapshot = snapshot,
                Permissions = policyPermissions.Order(StringComparer.Ordinal).ToArray() };
            // Keep the explicit snapshot when the owner has not deliberately narrowed access.
            policy.DefinitionJson = JsonSerializer.Serialize(definition with { Scopes = [scope] });
            policy.PermissionsJson = JsonSerializer.Serialize(scope.Permissions);
            policy.Revision++;
            db.Set<McpAccessPolicyAudit>().Add(McpPolicyDefinitionService.Audit(policy, "system:all-access-catalog", now.UtcDateTime));
            db.ActivityLogs.Add(new ActivityLog { UserId = credential.OwnerUserId, RelatedEntityId = credential.Id.ToString("N"),
                Message = $"Full-access MCP catalog reconciled at policy revision {policy.Revision}. PreviousPermissions={previousPermissions}; Permissions={credential.Permissions}." });
        }
        await identity.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<bool> IsLegacyPresetAsync(McpAccessPolicy policy, McpPolicyScopeDraft scope, IntegrationCredential credential, CancellationToken ct)
    {
        if (policy.Revision != 1 || scope.Exceptions.Length != 0 || !scope.Name.EndsWith(" · All access", StringComparison.Ordinal) ||
            McpPolicyDefinitionService.CatalogFingerprint(scope.Operations) != "E0F019CC1038A79E959248906ADD61779D8E68133D8912525DD65EC1117D1DED" ||
            !scope.Permissions.ToHashSet(StringComparer.Ordinal).SetEquals(McpPolicyDefinitionService.GrantedPermissions(credential))) return false;
        if (scope.Scope == "server" ? !scope.Permissions.SequenceEqual([HelpdeskPermissions.HelpdeskAdmin]) :
            McpPolicyDefinitionService.CatalogFingerprint(scope.Permissions) != "F439CA4A03D3F72F705948B3D288B9EF1FE9F9208159131059125E7D6986CE3C") return false;
        var audit = await db.Set<McpAccessPolicyAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.CredentialId == credential.Id && a.Revision == 1, ct);
        if (audit?.Actor != credential.OwnerUserId) return false;
        McpPolicyDto? original;
        try { original = JsonSerializer.Deserialize<McpPolicyDto>(audit.DefinitionJson); }
        catch (JsonException) { return false; }
        if (original?.Scopes.Length != 1 || original.Resource != credential.McpResourceUri ||
            JsonSerializer.Serialize(original.Scopes[0]) != JsonSerializer.Serialize(scope)) return false;
        return scope.Scope == "server" || await identity.IntegrationCredentialSetupAttempts.AnyAsync(a =>
            a.CredentialId == credential.Id && a.OwnerUserId == credential.OwnerUserId && a.State == "Acknowledged", ct);
    }
}
