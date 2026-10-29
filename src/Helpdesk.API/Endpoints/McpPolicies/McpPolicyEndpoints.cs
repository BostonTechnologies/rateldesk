using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.API.Endpoints.McpPolicies;

public static class McpPolicyEndpoints
{
    private sealed record PreviewReceipt(string Actor, DateTime ExpiresUtc, string Action, McpPolicyDraft Draft);
    private const string PreviewPurpose = "RatelDesk.McpPolicyPreview.v2";

    public static void MapMcpPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/mcp-policies").RequireAuthorization("HelpdeskAdmin").WithTags("MCP Policies");
        group.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers.CacheControl = "no-store";
            // Interactive administrators can manage their own approved integration.
            // An integration or owner-delegated machine can never edit its ceiling.
            var principal = ctx.HttpContext.User;
            if (string.IsNullOrWhiteSpace(Actor(ctx.HttpContext)) ||
                principal.FindAll("auth_mode").Any(claim => claim.Value is "integration" or "mcp" or "gateway" or "service" or "machine") ||
                principal.HasClaim("token_use", "rateldesk_service") || principal.HasClaim("token_use", "mcp_execution"))
                return Results.Forbid();
            try { return await next(ctx); }
            catch (DbUpdateConcurrencyException) { return Conflict("Policy revision changed. Review the current policy again."); }
            catch (DbUpdateException error) when (error.InnerException is
                PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } or
                SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
            { return Conflict("Policy changed concurrently. Read the current policy before retrying."); }
        });
        group.MapGet("", async (bool? includeRetired, HelpdeskDbContext db, RatelDeskIdentityDbContext identity, CancellationToken ct) =>
        {
            var rows = await db.Set<McpAccessPolicy>().AsNoTracking().ToListAsync(ct);
            var directory = await Directory(db, identity, false, ct);
            var credentials = directory.Credentials.ToDictionary(x => x.Id);
            var now = DateTime.UtcNow;
            return Results.Ok(rows.Where(row => includeRetired == true || row.State == "Active" && row.ExpiresUtc > now &&
                    credentials.TryGetValue(row.CredentialId, out var credential) && credential.IsActive)
                .Select(row => Describe(row, credentials)).OrderBy(row => row.CredentialName ?? row.CredentialId.ToString(), StringComparer.OrdinalIgnoreCase));
        });
        group.MapGet("/directory", async (HelpdeskDbContext db, RatelDeskIdentityDbContext identity, CancellationToken ct) =>
            Results.Ok(await Directory(db, identity, true, ct)));
        group.MapGet("/credentials", async (HelpdeskDbContext db, RatelDeskIdentityDbContext identity, CancellationToken ct) =>
            Results.Ok((await Directory(db, identity, true, ct)).Credentials));
        group.MapGet("/{id:guid}/audit", async (Guid id, HelpdeskDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Set<McpAccessPolicyAudit>().AsNoTracking().Where(x => x.CredentialId == id)
                .OrderByDescending(x => x.Revision).Take(100)
                .Select(x => new McpPolicyAuditDto(x.Id, x.CredentialId, x.Revision, x.Actor,
                    DateTime.SpecifyKind(x.OccurredUtc, DateTimeKind.Utc), x.DefinitionJson)).ToListAsync(ct)));
        group.MapPost("/preview", async (McpPolicyDraft draft, HttpContext ctx, HelpdeskDbContext db,
            RatelDeskIdentityDbContext identity, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var error = await Validate(draft, db, identity, ct);
            if (error is not null) return error;
            draft = Normalize(draft);
            return Results.Ok(Preview(protection, Actor(ctx), "Update", draft,
                draft.State == "Active"
                    ? "Apply this policy within the credential's existing grants and the owner's current authority. Nested denies override allowed operations."
                    : "Stop access through this credential. The policy and its history remain available."));
        });
        group.MapPost("/{id:guid}/delete/preview", async (Guid id, McpPolicyDeleteDraft request, HttpContext ctx,
            HelpdeskDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var row = await db.Set<McpAccessPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId == id, ct);
            if (row is null) return Results.NotFound();
            if (row.Revision != request.ExpectedRevision || row.State == "Deleted")
                return Conflict("This policy changed or was already deleted. Refresh the policy list.");
            var dto = McpPolicyDefinitionService.ToDto(row);
            var draft = new McpPolicyDraft(id, dto.OrganizationId, dto.Permissions, dto.ExpiresUtc, row.Revision, "Deleted") { Scopes = dto.Scopes };
            return Results.Ok(Preview(protection, Actor(ctx), "Delete", draft,
                "Delete this policy from normal management and stop all access through this credential, including nested exceptions. Audit history is retained. Deletion does not restore the credential's former permissions."));
        });
        group.MapPost("/confirm", async (ConfirmMcpPolicy request, HttpContext ctx, HelpdeskDbContext db,
            RatelDeskIdentityDbContext identity, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            PreviewReceipt? receipt;
            try { receipt = JsonSerializer.Deserialize<PreviewReceipt>(protection.CreateProtector(PreviewPurpose).Unprotect(request.Confirmation)); }
            catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { return Invalid("The policy review is invalid. Review again."); }
            if (receipt is null || receipt.Actor != Actor(ctx) || receipt.ExpiresUtc <= DateTime.UtcNow)
                return Invalid("The policy review expired or belongs to another administrator. Review again.");
            var draft = receipt.Draft;
            var previous = await db.Set<McpAccessPolicy>().SingleOrDefaultAsync(x => x.CredentialId == draft.CredentialId, ct);
            // A repeated accepted receipt reads the exact durable result without a second audit write.
            if (previous?.Revision == draft.ExpectedRevision + 1 && Matches(previous, draft))
                return Results.Ok(McpPolicyDefinitionService.ToDto(previous));
            if (receipt.Action == "Delete")
            {
                if (previous is null) return Results.NotFound();
                if (previous.Revision != draft.ExpectedRevision || previous.State == "Deleted")
                    return Conflict("Policy revision changed. Review deletion again.");
                previous.State = "Deleted";
                previous.Revision++;
                db.Add(McpPolicyDefinitionService.Audit(previous, receipt.Actor, DateTime.UtcNow));
                await db.SaveChangesAsync(ct);
                return Results.Ok(McpPolicyDefinitionService.ToDto(previous));
            }
            if (receipt.Action != "Update") return Invalid("The policy review action is invalid.");
            var error = await Validate(draft, db, identity, ct);
            if (error is not null) return error;
            var row = previous ?? new McpAccessPolicy { CredentialId = draft.CredentialId };
            if (previous is null) db.Add(row);
            if (previous is not null && draft.State is ("Disabled" or "Revoked") && Matches(previous, draft, compareState: false))
            {
                row.State = draft.State;
            }
            else if (draft.Scopes.Length > 0)
            {
                var credential = await identity.IntegrationCredentials.AsNoTracking().SingleAsync(x => x.Id == draft.CredentialId, ct);
                McpPolicyDefinitionService.Apply(row, credential, draft.Scopes, draft.State);
            }
            else
            {
                row.OrganizationId = draft.OrganizationId;
                row.PermissionsJson = JsonSerializer.Serialize(draft.Permissions);
                row.State = draft.State;
                row.ExpiresUtc = draft.ExpiresUtc;
            }
            row.Revision++;
            db.Add(McpPolicyDefinitionService.Audit(row, receipt.Actor, DateTime.UtcNow));
            await db.SaveChangesAsync(ct);
            return Results.Ok(McpPolicyDefinitionService.ToDto(row));
        });
    }

    private static string Actor(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? "";
    private static IResult Invalid(string message) => Results.BadRequest(new { message });
    private static IResult Conflict(string message) => Results.Conflict(new { message });

    private static McpPolicyPreview Preview(IDataProtectionProvider protection, string actor, string action, McpPolicyDraft draft, string effect)
    {
        var receipt = new PreviewReceipt(actor, DateTime.UtcNow.AddMinutes(5), action, draft);
        return new(protection.CreateProtector(PreviewPurpose).Protect(JsonSerializer.Serialize(receipt)), draft, effect);
    }

    private static McpPolicyDraft Normalize(McpPolicyDraft draft)
    {
        var scopes = McpPolicyDefinitionService.Normalize(draft.Scopes);
        return scopes.Length == 0
            ? draft with { ExpiresUtc = new DateTime(draft.ExpiresUtc.Ticks / 10 * 10, DateTimeKind.Utc) }
            : draft with { Scopes = scopes, OrganizationId = scopes[0].OrganizationId ?? "", Permissions = scopes[0].Permissions, ExpiresUtc = scopes[0].ExpiresUtc };
    }

    private static bool Matches(McpAccessPolicy row, McpPolicyDraft draft, bool compareState = true)
    {
        var expected = new McpPolicyDto(draft.CredentialId, draft.OrganizationId, draft.Permissions, draft.ExpiresUtc, row.Revision, compareState ? draft.State : row.State)
        {
            Scopes = draft.Scopes,
            Resource = McpPolicyDefinitionDocumentReader.Read(row.DefinitionJson)?.Resource
        };
        return JsonSerializer.Serialize(McpPolicyDefinitionService.ToDto(row)) == JsonSerializer.Serialize(expected);
    }

    private static async Task<IResult?> Validate(McpPolicyDraft draft, HelpdeskDbContext db,
        RatelDeskIdentityDbContext identity, CancellationToken ct)
    {
        if (draft.Scopes is null || draft.Scopes.Any(scope => scope is null || scope.Name is null || scope.Permissions is null ||
                scope.Operations is null || scope.Exceptions is null || scope.Exceptions.Any(exception => exception is null || exception.Name is null || exception.Operations is null)) ||
            draft.State is not ("Active" or "Disabled" or "Revoked"))
            return Invalid("Select an active, disabled or revoked policy state.");
        var previous = await db.Set<McpAccessPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId == draft.CredentialId, ct);
        if ((previous?.Revision ?? 0) != draft.ExpectedRevision || previous?.State is "Revoked" or "Deleted")
            return Conflict("This policy revision is stale or terminal. Refresh the policy list.");
        if (previous is not null && draft.State is ("Disabled" or "Revoked"))
        {
            // Stopping access must work after a child or the credential expires.
            // A disabled policy can also be edited while remaining disabled; those
            // changed bounds must pass the same checks as an active definition.
            if (Matches(previous, Normalize(draft), compareState: false)) return null;
            if (previous.State != "Disabled" || draft.State != "Disabled")
                return Invalid("Disable or revoke the saved policy without changing its definition. Review edits separately.");
        }
        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == draft.CredentialId, ct);
        if (credential is null || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            credential.Purpose is not ("mcp" or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose))
            return Invalid("Choose an active HTTP MCP credential.");
        if (draft.Scopes.Length > 0)
        {
            var error = McpPolicyDefinitionService.ValidateScopes(credential, draft.Scopes, DateTime.UtcNow);
            if (error is not null) return Invalid(error);
            if (draft.Scopes[0].Scope == "organization" && !await db.Organizations.AnyAsync(x => x.Id == draft.Scopes[0].OrganizationId && x.IsEnabled, ct))
                return Invalid("Choose an enabled organization within this credential's scope.");
        }
        else
        {
            if (previous is not null && !string.IsNullOrEmpty(previous.DefinitionJson))
                return Conflict("This policy includes operation rules. Use the complete policy editor to preserve them.");
            if (draft.Permissions is not { Length: > 0 and <= 100 } ||
                draft.Permissions.Any(x => !HelpdeskPermissions.AssignablePermissions.Contains(x, StringComparer.Ordinal)) ||
                draft.ExpiresUtc.Kind != DateTimeKind.Utc || draft.ExpiresUtc <= DateTime.UtcNow ||
                draft.ExpiresUtc > credential.ExpiresAtUtc.UtcDateTime)
                return Invalid("Choose valid permissions and a future UTC expiry no later than the credential expiry.");
            if (!await db.Organizations.AnyAsync(x => x.Id == draft.OrganizationId && x.IsEnabled, ct)) return Results.NotFound();
            if (credential.Purpose == "mcp" && credential.OrganizationId != draft.OrganizationId) return Results.Forbid();
        }
        return null;
    }

    private static async Task<McpPolicyDirectory> Directory(HelpdeskDbContext db, RatelDeskIdentityDbContext identity, bool activeOnly, CancellationToken ct)
    {
        var candidates = await identity.IntegrationCredentials.AsNoTracking()
            .Where(x => x.Purpose == "mcp" || x.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose).ToListAsync(ct);
        var ownerIds = candidates.Select(x => x.OwnerUserId).Distinct().ToArray();
        var owners = await identity.Users.AsNoTracking().Where(x => ownerIds.Contains(x.Id))
            .Select(x => new { x.Id, x.DisplayName, x.UserName, x.Email, x.IsEnabled, x.IsInstanceAdministrator }).ToDictionaryAsync(x => x.Id, ct);
        var organizations = await db.Organizations.AsNoTracking().Where(x => x.IsEnabled)
            .OrderBy(x => x.Name).Select(x => new McpPolicyOrganization(x.Id, x.Name)).ToArrayAsync(ct);
        var names = organizations.ToDictionary(x => x.Id, x => x.Name);
        var credentialIds = candidates.Select(x => x.Id).ToArray();
        var policies = await db.Set<McpAccessPolicy>().AsNoTracking().Where(x => credentialIds.Contains(x.CredentialId))
            .Select(x => new { x.CredentialId, x.State, x.Revision }).ToDictionaryAsync(x => x.CredentialId, ct);
        var now = DateTimeOffset.UtcNow;
        var activeIds = candidates.Where(x => x.RevokedAtUtc is null && x.ExpiresAtUtc > now &&
                owners.TryGetValue(x.OwnerUserId, out var owner) && owner.IsEnabled &&
                (x.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose
                    ? owner.IsInstanceAdministrator && x.Permissions == HelpdeskPermissions.HelpdeskAdmin
                    : x.OrganizationId is not null && names.ContainsKey(x.OrganizationId)))
            .Select(x => x.Id).ToHashSet();
        var credentials = candidates.Where(x => !activeOnly || activeIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new McpPolicyCredential(x.Id, x.Name, x.OwnerUserId, x.OrganizationId, x.ExpiresAtUtc)
            {
                OwnerName = owners.TryGetValue(x.OwnerUserId, out var owner)
                    ? !string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.DisplayName
                        : !string.IsNullOrWhiteSpace(owner.UserName) ? owner.UserName
                        : !string.IsNullOrWhiteSpace(owner.Email) ? owner.Email : owner.Id
                    : "Deleted account",
                OrganizationName = x.OrganizationId is not null && names.TryGetValue(x.OrganizationId, out var name) ? name : null,
                Resource = x.McpResourceUri,
                IsActive = activeIds.Contains(x.Id),
                PolicyState = policies.TryGetValue(x.Id, out var policyState) ? policyState.State : null,
                PolicyRevision = policies.TryGetValue(x.Id, out var policyRevision) ? policyRevision.Revision : null,
                GrantKind = x.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose ? IntegrationCredentialGrantKinds.OwnerAdministration : IntegrationCredentialGrantKinds.Ordinary,
                Permissions = x.Permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            }).ToArray();
        return new(credentials, organizations, McpPolicyDefinitionService.Operations);
    }

    private static McpPolicyDto Describe(McpAccessPolicy row, IReadOnlyDictionary<Guid, McpPolicyCredential> credentials)
    {
        var dto = McpPolicyDefinitionService.ToDto(row);
        return credentials.TryGetValue(row.CredentialId, out var credential)
            ? dto with { CredentialName = credential.Name, OwnerName = credential.OwnerName, OrganizationName = credential.OrganizationName, Resource = credential.Resource }
            : dto with { CredentialName = "Deleted credential", OwnerName = "Retained audit" };
    }
}
