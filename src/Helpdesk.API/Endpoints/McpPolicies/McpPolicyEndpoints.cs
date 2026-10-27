using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.McpPolicies;

public static class McpPolicyEndpoints
{
    private sealed record PreviewReceipt(string Actor, DateTime ExpiresUtc, McpPolicyDraft Draft);
    public static void MapMcpPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/mcp-policies").RequireAuthorization("HelpdeskAdmin").WithTags("MCP Policies");
        group.AddEndpointFilter(async (ctx, next) =>
        {
            // Policy administration belongs to an interactive administrator, never
            // the credential being constrained or an owner-delegated MCP caller.
            if (ctx.HttpContext.User.HasClaim("auth_mode", "integration")) return Results.Forbid();
            try { return await next(ctx); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "Policy revision changed. Preview again." }); }
            catch (DbUpdateException) { return Results.Conflict(new { message = "Policy changed concurrently. Read the current policy before retrying." }); }
        });
        group.MapGet("", async (HelpdeskDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Set<McpAccessPolicy>().AsNoTracking().ToListAsync(ct)).Select(ToDto)));
        group.MapGet("/credentials", async (RatelDeskIdentityDbContext identity, HttpContext ctx, CancellationToken ct) =>
        {
            var actor = Actor(ctx);
            var now = DateTimeOffset.UtcNow;
            var candidates = await identity.IntegrationCredentials.AsNoTracking()
                .Where(x => x.OwnerUserId != actor && x.RevokedAtUtc == null && (x.Purpose == "mcp" || x.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose))
                .Take(500).Select(x => new McpPolicyCredential(x.Id, x.Name, x.OwnerUserId, x.OrganizationId, x.ExpiresAtUtc)).ToListAsync(ct);
            return Results.Ok(candidates.Where(x => x.ExpiresAtUtc > now));
        });
        group.MapGet("/{id:guid}/audit", async (Guid id, HelpdeskDbContext db, CancellationToken ct) => Results.Ok(await db.Set<McpAccessPolicyAudit>().AsNoTracking().Where(x => x.CredentialId == id).OrderByDescending(x => x.Revision).Take(100).Select(x => new McpPolicyAuditDto(x.Id, x.CredentialId, x.Revision, x.Actor, DateTime.SpecifyKind(x.OccurredUtc, DateTimeKind.Utc), x.DefinitionJson)).ToListAsync(ct)));
        group.MapPost("/preview", async (McpPolicyDraft draft, HttpContext ctx, HelpdeskDbContext db, RatelDeskIdentityDbContext identity, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var error = await Validate(draft, Actor(ctx), db, identity, ct);
            if (error is not null) return error;
            // PostgreSQL stores microseconds; pin the reviewed value at that precision
            // so an accepted confirmation replays identically on both providers.
            draft = draft with { ExpiresUtc = new DateTime(draft.ExpiresUtc.Ticks / 10 * 10, DateTimeKind.Utc) };
            var receipt = new PreviewReceipt(Actor(ctx), DateTime.UtcNow.AddMinutes(5), draft);
            var token = protection.CreateProtector("RatelDesk.McpPolicyPreview.v1").Protect(JsonSerializer.Serialize(receipt));
            return Results.Ok(new McpPolicyPreview(token, draft, draft.State == "Active" ? "Constrain this credential to the listed organization and permissions until expiry. Existing account authority remains the upper bound." : "Deny all access through this credential. Disabling, expiry and revocation do not restore its earlier permissions."));
        });
        group.MapPost("/confirm", async (ConfirmMcpPolicy request, HttpContext ctx, HelpdeskDbContext db, RatelDeskIdentityDbContext identity, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            PreviewReceipt? receipt;
            try { receipt = JsonSerializer.Deserialize<PreviewReceipt>(protection.CreateProtector("RatelDesk.McpPolicyPreview.v1").Unprotect(request.Confirmation)); }
            catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { return Results.BadRequest(); }
            if (receipt is null || receipt.Actor != Actor(ctx) || receipt.ExpiresUtc <= DateTime.UtcNow) return Results.BadRequest();
            var draft = receipt.Draft;
            var previous = await db.Set<McpAccessPolicy>().SingleOrDefaultAsync(x => x.CredentialId == draft.CredentialId, ct);
            // Exact accepted replay reads the durable receipt without a second write.
            if (previous?.Revision == draft.ExpectedRevision + 1 && JsonSerializer.Serialize(ToDto(previous)) == JsonSerializer.Serialize(new McpPolicyDto(draft.CredentialId, draft.OrganizationId, draft.Permissions, draft.ExpiresUtc, previous.Revision, draft.State))) return Results.Ok(ToDto(previous));
            var error = await Validate(draft, receipt.Actor, db, identity, ct);
            if (error is not null) return error;
            var row = previous ?? new McpAccessPolicy { CredentialId = draft.CredentialId };
            if (previous is null) db.Add(row);
            row.OrganizationId = draft.OrganizationId; row.PermissionsJson = JsonSerializer.Serialize(draft.Permissions);
            row.State = draft.State; row.ExpiresUtc = draft.ExpiresUtc; row.Revision++;
            db.Add(new McpAccessPolicyAudit { CredentialId = row.CredentialId, Revision = row.Revision, Actor = receipt.Actor, DefinitionJson = JsonSerializer.Serialize(ToDto(row)) });
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(row));
        });
    }
    private static string Actor(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? "";
    private static McpPolicyDto ToDto(McpAccessPolicy x) => new(x.CredentialId, x.OrganizationId, JsonSerializer.Deserialize<string[]>(x.PermissionsJson) ?? [], DateTime.SpecifyKind(x.ExpiresUtc, DateTimeKind.Utc), x.Revision, x.State);
    private static async Task<IResult?> Validate(McpPolicyDraft draft, string actor, HelpdeskDbContext db, RatelDeskIdentityDbContext identity, CancellationToken ct)
    {
        if (draft.Permissions is not { Length: > 0 and <= 100 } || draft.Permissions.Any(x => !HelpdeskPermissions.AssignablePermissions.Contains(x, StringComparer.Ordinal)) || draft.State is not ("Active" or "Disabled" or "Revoked") || draft.ExpiresUtc.Kind != DateTimeKind.Utc || draft.ExpiresUtc <= DateTime.UtcNow || draft.ExpiresUtc > DateTime.UtcNow.AddDays(90)) return Results.BadRequest();
        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == draft.CredentialId, ct);
        if (credential is null || credential.OwnerUserId == actor || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= DateTimeOffset.UtcNow || draft.ExpiresUtc > credential.ExpiresAtUtc.UtcDateTime || credential.Purpose is not ("mcp" or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose)) return Results.Forbid();
        if (!await db.Organizations.AnyAsync(x => x.Id == draft.OrganizationId && x.IsEnabled, ct)) return Results.NotFound();
        if (credential.Purpose == "mcp" && credential.OrganizationId != draft.OrganizationId) return Results.Forbid();
        var previous = await db.Set<McpAccessPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId == draft.CredentialId, ct);
        if ((previous?.Revision ?? 0) != draft.ExpectedRevision || previous?.State == "Revoked") return Results.Conflict();
        return null;
    }
}
