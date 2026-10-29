using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

public static class IntegrationCredentialEndpoints
{
    public const string CredentialManagementPolicy = "IntegrationCredentialManagementSession";
    public const string OwnerManagementPolicy = "IntegrationCredentialOwnerManagement";
    public const string SelfRevocationPolicy = "IntegrationCredentialSelfRevocation";

    public static void MapIntegrationCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integration-credentials")
            .WithTags("Integration Credentials")
            .RequireAuthorization(OwnerManagementPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapGet("/", async (ClaimsPrincipal principal, IIntegrationCredentialOwnerResolver ownerResolver, RatelDeskIdentityDbContext identityDb, CancellationToken ct) =>
        {
            var owner = await AdministrationOwnerResolver.ResolveAsync(principal, ownerResolver, identityDb, ct);
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
                    McpResourceUri = credential.McpResourceUri,
                    GrantKind = credential.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose
                        ? IntegrationCredentialGrantKinds.OwnerAdministration : credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose ? "setup-verification" : "ordinary"
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
        }).RequireAuthorization(CredentialManagementPolicy).WithSummary("Create an API integration credential");

        group.MapDelete("/{credentialId:guid}", async (Guid credentialId, ClaimsPrincipal principal, IIntegrationCredentialOwnerResolver ownerResolver, RatelDeskIdentityDbContext identityDb, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var owner = await AdministrationOwnerResolver.ResolveAsync(principal, ownerResolver, identityDb, ct);
            if (owner is null) return Results.Forbid();
            var credential = await identityDb.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == credentialId && candidate.OwnerUserId == owner.UserId, ct);
            if (credential is null) return Results.NotFound();
            var now = DateTimeOffset.UtcNow;
            var revoked = await identityDb.IntegrationCredentials
                .Where(candidate => candidate.Id == credentialId && candidate.OwnerUserId == owner.UserId && candidate.RevokedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.RevokedAtUtc, now), ct);
            if (revoked > 0)
            {
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

        group.MapDelete("/{credentialId:guid}/record", async (Guid credentialId, ClaimsPrincipal principal,
            IIntegrationCredentialOwnerResolver ownerResolver, RatelDeskIdentityDbContext identityDb,
            CancellationToken ct) =>
        {
            var owner = await AdministrationOwnerResolver.ResolveAsync(principal, ownerResolver, identityDb, ct);
            if (owner is null) return Results.Forbid();
            return await new IntegrationCredentialLifecycle(identityDb).DeleteOwnedAsync(owner.UserId, credentialId, ct)
                ? Results.NoContent() : Results.NotFound();
        }).WithSummary("Revoke and permanently delete an integration credential");

        var access = group.MapGroup("/{credentialId:guid}/access")
            .RequireAuthorization(CredentialManagementPolicy)
            .RequireRateLimiting("IntegrationPairing")
            .AddEndpointFilter(async (context, next) =>
            {
                try { return await next(context); }
                catch (CredentialGrantException error)
                { return error.Errors is null ? Results.Forbid() : Results.ValidationProblem(error.Errors); }
                catch (CredentialAccessException error)
                { return Results.Json(new { message = error.Message }, statusCode: error.StatusCode); }
            });

        access.MapGet("", async (Guid credentialId, ClaimsPrincipal principal,
            IIntegrationCredentialOwnerResolver ownerResolver, ICurrentUserAccessService accessService,
            HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb, IDataProtectionProvider protection,
            TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await new IntegrationCredentialAccessService(ownerResolver, accessService, db, identityDb, protection, clock)
                .GetAsync(credentialId, principal, ct)))
            .WithSummary("Inspect configured credential access and current admission");

        access.MapPost("/review", async (Guid credentialId, [FromBody] IntegrationCredentialAccessDraft? request,
            ClaimsPrincipal principal, IIntegrationCredentialOwnerResolver ownerResolver, ICurrentUserAccessService accessService,
            HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb, IDataProtectionProvider protection,
            TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await new IntegrationCredentialAccessService(ownerResolver, accessService, db, identityDb, protection, clock)
                .ReviewAsync(credentialId, request, principal, ct)))
            .WithSummary("Review an exact permission change for an existing credential");

        access.MapPost("/apply", async (Guid credentialId, [FromBody] ApplyIntegrationCredentialAccess? request,
            ClaimsPrincipal principal, IIntegrationCredentialOwnerResolver ownerResolver, ICurrentUserAccessService accessService,
            HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb, IDataProtectionProvider protection,
            TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await new IntegrationCredentialAccessService(ownerResolver, accessService, db, identityDb, protection, clock)
                .ApplyAsync(credentialId, request, principal, ct)))
            .WithSummary("Apply the owner's reviewed credential permission change");

        app.MapPost("/api/v1/integration-credentials/self/revoke", async (
            ClaimsPrincipal principal,
            RatelDeskIdentityDbContext identityDb,
            HelpdeskDbContext db,
            CancellationToken ct) =>
        {
            if (!Guid.TryParseExact(principal.FindFirstValue("integration_credential_id"), "N", out var credentialId))
                return Results.Forbid();

            var credential = await identityDb.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == credentialId, ct);
            if (credential is null) return Results.NotFound();
            var now = DateTimeOffset.UtcNow;
            var revoked = await identityDb.IntegrationCredentials
                .Where(candidate => candidate.Id == credentialId && candidate.RevokedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.RevokedAtUtc, now), ct);
            if (revoked > 0)
            {
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
        public string GrantKind { get; init; } = IntegrationCredentialGrantKinds.Ordinary;
        public bool OwnerAdministrationConsent { get; init; }
    }

    public sealed record IntegrationCredentialMetadata(Guid Id, string Name, string Prefix, string Purpose, string? OrganizationId, IReadOnlyList<string> Permissions, DateTimeOffset ExpiresAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastUsedAtUtc, DateTimeOffset? RevokedAtUtc)
    {
        public string? McpResourceUri { get; init; }
        public string GrantKind { get; init; } = IntegrationCredentialGrantKinds.Ordinary;
    }

    public sealed record CreatedIntegrationCredential(Guid Id, string Prefix, string Secret, string Purpose, string? OrganizationId, IReadOnlyList<string> Permissions, DateTimeOffset ExpiresAtUtc)
    {
        public string? McpResourceUri { get; init; }
        public string GrantKind { get; init; } = "ordinary";
        public override string ToString() => $"CreatedIntegrationCredential {{ Id = {Id}, Prefix = {Prefix}, Secret = [redacted], Purpose = {Purpose} }}";
    }
}
