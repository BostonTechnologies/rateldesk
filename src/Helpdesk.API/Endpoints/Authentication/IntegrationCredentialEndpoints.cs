using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

public static class IntegrationCredentialEndpoints
{
    public const string CredentialManagementPolicy = "IntegrationCredentialManagementSession";
    public const string SelfRevocationPolicy = "IntegrationCredentialSelfRevocation";

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
            var issuer = new IntegrationCredentialIssuer(ownerResolver, accessService, db, identityDb);
            ValidatedCredentialGrant grant;
            try { grant = await issuer.ValidateAsync(principal, request, ct); }
            catch (CredentialGrantException error)
            {
                return error.Errors is null ? Results.Forbid() : Results.ValidationProblem(error.Errors);
            }
            var (credential, delivery) = issuer.Create(grant, DateTimeOffset.UtcNow);
            issuer.Track(credential);
            await identityDb.SaveChangesAsync(ct);
            db.ActivityLogs.Add(new Helpdesk.Shared.Models.ActivityLog
            {
                UserId = grant.OwnerUserId,
                RelatedEntityId = credential.Id.ToString("N"),
                Message = $"Integration credential created. Prefix={credential.Prefix}; Purpose={credential.Purpose}; OrganizationId={credential.OrganizationId}; Permissions={credential.Permissions}; ExpiresAtUtc={credential.ExpiresAtUtc:O}."
            });
            await db.SaveChangesAsync(ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/v1/integration-credentials/{credential.Id:N}", delivery);
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
        public override string ToString() => $"CreatedIntegrationCredential {{ Id = {Id}, Prefix = {Prefix}, Secret = [redacted], Purpose = {Purpose} }}";
    }
}
