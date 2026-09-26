using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.API.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

public static class IntegrationCredentialEndpoints
{
    public const string CredentialManagementPolicy = "IntegrationCredentialManagementSession";
    public const string SelfRevocationPolicy = "IntegrationCredentialSelfRevocation";
    private const int DefaultLifetimeDays = 30;
    private const int MaximumLifetimeDays = 90;

    public static void MapIntegrationCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integration-credentials")
            .WithTags("Integration Credentials")
            .RequireAuthorization(CredentialManagementPolicy);

        group.MapGet("/", async (ClaimsPrincipal principal, IIntegrationCredentialOwnerResolver ownerResolver, RatelDeskIdentityDbContext identityDb, CancellationToken ct) =>
        {
            var owner = await ownerResolver.ResolveAsync(principal, ct);
            if (owner is null) return Results.Forbid();
            var credentials = await identityDb.IntegrationCredentials.AsNoTracking()
                .Where(credential => credential.OwnerUserId == owner.UserId)
                .OrderByDescending(credential => credential.CreatedAtUnixMilliseconds)
                .ThenByDescending(credential => credential.Id)
                .Select(credential => new IntegrationCredentialMetadata(
                    credential.Id, credential.Name, credential.Prefix, credential.Purpose,
                    credential.OrganizationId, credential.Permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    credential.ExpiresAtUtc, credential.CreatedAtUtc, credential.LastUsedAtUtc, credential.RevokedAtUtc)
                {
                    McpResourceUri = credential.McpResourceUri
                })
                .ToListAsync(ct);
            return Results.Ok(credentials);
        }).WithSummary("List integration credentials");

        group.MapPost("/", async (
            [FromBody] CreateIntegrationCredentialRequest? request,
            ClaimsPrincipal principal,
            HttpContext context,
            IIntegrationCredentialOwnerResolver ownerResolver,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            RatelDeskIdentityDbContext identityDb,
            CancellationToken ct) =>
        {
            var owner = await ownerResolver.ResolveAsync(principal, ct);
            if (owner is null) return Results.Forbid();
            if (request is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["A credential request is required."] });
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A credential name up to 128 characters is required."] });
            var purpose = request.Purpose?.Trim().ToLowerInvariant();
            if (purpose is not ("api" or "mcp")) return Results.ValidationProblem(new Dictionary<string, string[]> { ["purpose"] = ["Purpose must be api or mcp."] });
            var mcpResourceUri = purpose == "mcp"
                ? CanonicalMcpResourceUri(request.McpResourceUri)
                : null;
            if (purpose == "mcp" && mcpResourceUri is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mcpResourceUri"] = ["MCP credentials require an absolute HTTPS resource URI ending in /mcp."] });
            if (purpose == "api" && !string.IsNullOrWhiteSpace(request.McpResourceUri))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mcpResourceUri"] = ["MCP resource URIs can only be configured for MCP credentials."] });

            var access = await accessService.ResolveAsync(principal, ct);
            if (request.Permissions is null || request.Permissions.Count == 0 ||
                request.Permissions.Any(string.IsNullOrWhiteSpace))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["permissions"] = ["At least one non-empty permission is required."] });
            }
            var requestedPermissions = request.Permissions
                .Select(permission => permission.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (requestedPermissions.Any(permission => !HelpdeskPermissions.AssignablePermissions.Contains(permission, StringComparer.OrdinalIgnoreCase)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["permissions"] = ["Permissions must be known assignable permissions."] });
            }
            var organizationId = request.OrganizationId?.Trim();
            var organization = string.IsNullOrWhiteSpace(organizationId)
                ? null
                : await db.Organizations.AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == organizationId, ct);
            if (organization is null || organization.State != Helpdesk.Shared.Models.EntityState.Enabled)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["organizationId"] = ["An enabled organization is required."] });
            }
            if (!access.IsHelpdeskAdmin && requestedPermissions.Any(permission => !access.HasPermission(permission, organizationId)))
                return Results.Forbid();

            if (request.LifetimeDays is < 1 or > MaximumLifetimeDays)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["lifetimeDays"] = [$"Lifetime must be between 1 and {MaximumLifetimeDays} days."] });
            var lifetimeDays = request.LifetimeDays ?? DefaultLifetimeDays;
            var id = Guid.NewGuid();
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var prefix = $"rdk_{id:N}"[..16];
            var createdAtUtc = DateTimeOffset.UtcNow;
            var credential = new IntegrationCredential
            {
                Id = id,
                OwnerUserId = owner.UserId,
                Name = request.Name.Trim(),
                Prefix = prefix,
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
                Purpose = purpose,
                McpResourceUri = mcpResourceUri,
                OrganizationId = organizationId,
                Permissions = string.Join(' ', requestedPermissions.Order(StringComparer.OrdinalIgnoreCase)),
                CreatedAtUtc = createdAtUtc,
                CreatedAtUnixMilliseconds = createdAtUtc.ToUnixTimeMilliseconds(),
                ExpiresAtUtc = createdAtUtc.AddDays(lifetimeDays)
            };
            identityDb.IntegrationCredentials.Add(credential);
            await identityDb.SaveChangesAsync(ct);
            db.ActivityLogs.Add(new Helpdesk.Shared.Models.ActivityLog
            {
                UserId = owner.UserId,
                RelatedEntityId = credential.Id.ToString("N"),
                Message = $"Integration credential created. Prefix={credential.Prefix}; Purpose={credential.Purpose}; OrganizationId={credential.OrganizationId}; Permissions={credential.Permissions}; ExpiresAtUtc={credential.ExpiresAtUtc:O}."
            });
            await db.SaveChangesAsync(ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/v1/integration-credentials/{credential.Id:N}", new CreatedIntegrationCredential(
                credential.Id, credential.Prefix, $"rdk_{credential.Id:N}_{secret}", credential.Purpose,
                credential.OrganizationId, requestedPermissions, credential.ExpiresAtUtc)
            {
                McpResourceUri = credential.McpResourceUri
            });
        }).WithSummary("Create an API integration credential");

        group.MapDelete("/{credentialId:guid}", async (Guid credentialId, ClaimsPrincipal principal, IIntegrationCredentialOwnerResolver ownerResolver, RatelDeskIdentityDbContext identityDb, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var owner = await ownerResolver.ResolveAsync(principal, ct);
            if (owner is null) return Results.Forbid();
            var credential = await identityDb.IntegrationCredentials.SingleOrDefaultAsync(candidate => candidate.Id == credentialId && candidate.OwnerUserId == owner.UserId, ct);
            if (credential is null) return Results.NotFound();
            if (credential.RevokedAtUtc is null)
            {
                credential.RevokedAtUtc = DateTimeOffset.UtcNow;
                await identityDb.SaveChangesAsync(ct);
                db.ActivityLogs.Add(new Helpdesk.Shared.Models.ActivityLog
                {
                    UserId = owner.UserId,
                    RelatedEntityId = credential.Id.ToString("N"),
                    Message = $"Integration credential revoked. Prefix={credential.Prefix}; Purpose={credential.Purpose}; OrganizationId={credential.OrganizationId}."
                });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).WithSummary("Revoke an integration credential");

        app.MapPost("/api/v1/integration-credentials/self/revoke", async (
            ClaimsPrincipal principal,
            RatelDeskIdentityDbContext identityDb,
            HelpdeskDbContext db,
            CancellationToken ct) =>
        {
            if (!Guid.TryParseExact(principal.FindFirstValue("integration_credential_id"), "N", out var credentialId))
                return Results.Forbid();

            var credential = await identityDb.IntegrationCredentials.SingleOrDefaultAsync(candidate => candidate.Id == credentialId, ct);
            if (credential is null) return Results.NotFound();
            if (credential.RevokedAtUtc is null)
            {
                credential.RevokedAtUtc = DateTimeOffset.UtcNow;
                await identityDb.SaveChangesAsync(ct);
                db.ActivityLogs.Add(new Helpdesk.Shared.Models.ActivityLog
                {
                    UserId = credential.OwnerUserId,
                    RelatedEntityId = credential.Id.ToString("N"),
                    Message = $"Presenting integration credential revoked. Prefix={credential.Prefix}; Purpose={credential.Purpose}; OrganizationId={credential.OrganizationId}."
                });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireAuthorization(SelfRevocationPolicy).WithTags("Integration Credentials").WithSummary("Revoke the presenting integration credential");
    }

    private static string? CanonicalMcpResourceUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/mcp", StringComparison.Ordinal))
        {
            return null;
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }

    public sealed record CreateIntegrationCredentialRequest(string Name, string Purpose, string OrganizationId, IReadOnlyList<string> Permissions, int? LifetimeDays)
    {
        public string? McpResourceUri { get; init; }
    }

    public sealed record IntegrationCredentialMetadata(Guid Id, string Name, string Prefix, string Purpose, string? OrganizationId, IReadOnlyList<string> Permissions, DateTimeOffset ExpiresAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastUsedAtUtc, DateTimeOffset? RevokedAtUtc)
    {
        public string? McpResourceUri { get; init; }
    }

    public sealed record CreatedIntegrationCredential(Guid Id, string Prefix, string Secret, string Purpose, string? OrganizationId, IReadOnlyList<string> Permissions, DateTimeOffset ExpiresAtUtc)
    {
        public string? McpResourceUri { get; init; }
    }
}
