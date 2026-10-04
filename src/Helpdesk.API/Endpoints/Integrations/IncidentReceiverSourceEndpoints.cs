using System.Security.Claims;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Integrations;

public static class IncidentReceiverSourceEndpoints
{
    public sealed record RegisterSource(Guid SourceInstanceId, string OrganizationId, string CustomerId, Guid CredentialId);
    public sealed record ChangeGrant(long ExpectedRevision, bool IsEnabled);

    public static void MapIncidentReceiverSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations/netratel/sources").WithTags("NetRatel Source Administration")
            .RequireAuthorization(IntegrationCredentialEndpoints.CredentialManagementPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var access = await context.HttpContext.RequestServices.GetRequiredService<ICurrentUserAccessService>()
                .ResolveAsync(context.HttpContext.User, context.HttpContext.RequestAborted);
            return access.IsHelpdeskAdmin ? await next(context) : Results.Forbid();
        });
        group.MapPost("/", async (HttpContext http, HelpdeskDbContext db, RatelDeskIdentityDbContext identity, TimeProvider time, CancellationToken ct) =>
        {
            var request = await IncidentReceiverEndpoints.ReadBoundedAsync<RegisterSource>(http.Request, ct);
            if (request is null || request.SourceInstanceId == Guid.Empty || request.CredentialId == Guid.Empty ||
                !IncidentReceiverContract.ValidTarget(new() { OrganizationId = request.OrganizationId, CustomerId = request.CustomerId }))
                return IncidentReceiverContract.Problem(400, "invalid-source-registration");
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.CustomerId && x.IsEnabled, ct);
            if (customer is null || customer.OrganizationId != request.OrganizationId ||
                !await db.Organizations.AnyAsync(x => x.Id == request.OrganizationId && x.IsEnabled, ct) ||
                !await ValidCredentialAsync(identity, request.CredentialId, request.OrganizationId, time, ct))
                return IncidentReceiverContract.Problem(400, "invalid-source-registration");
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(actor)) return Results.Forbid();
            var source = new IncidentReceiverSource
            {
                SourceNamespaceId = Guid.NewGuid(), SourceInstanceId = request.SourceInstanceId,
                OrganizationId = customer.OrganizationId, CustomerId = customer.Id,
                CreatedBy = actor, UpdatedBy = actor, CreatedAtUtc = time.GetUtcNow(), UpdatedAtUtc = time.GetUtcNow()
            };
            db.IncidentReceiverSources.Add(source);
            db.IncidentReceiverPrincipalBindings.Add(new() { SourceNamespaceId = source.SourceNamespaceId,
                PrincipalKind = "api_credential", PrincipalId = request.CredentialId.ToString("N") });
            Audit(db, source, actor, "registered", request.CredentialId, true);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (error.InnerException is Npgsql.PostgresException { SqlState: "23505" } or
                Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 })
            { return IncidentReceiverContract.Problem(409, "source-already-registered"); }
            return Results.Created($"/api/v1/integrations/netratel/sources/{source.SourceNamespaceId:D}", source);
        });
        group.MapPut("/{namespaceId:guid}/principal-bindings/{credentialId:guid}", async (
            Guid namespaceId, Guid credentialId, HttpContext http, HelpdeskDbContext db,
            RatelDeskIdentityDbContext identity, TimeProvider time, CancellationToken ct) =>
        {
            var request = await IncidentReceiverEndpoints.ReadBoundedAsync<ChangeGrant>(http.Request, ct);
            if (request is null) return IncidentReceiverContract.Problem(400, "invalid-source-registration");
            var source = await db.IncidentReceiverSources.SingleOrDefaultAsync(x => x.SourceNamespaceId == namespaceId, ct);
            if (source is null) return Results.NotFound();
            if (source.Revision != request.ExpectedRevision) return IncidentReceiverContract.Problem(409, "source-revision-conflict");
            if (request.IsEnabled && !await ValidCredentialAsync(identity, credentialId, source.OrganizationId, time, ct))
                return IncidentReceiverContract.Problem(400, "invalid-source-registration");
            var binding = await db.IncidentReceiverPrincipalBindings.SingleOrDefaultAsync(x => x.SourceNamespaceId == namespaceId &&
                x.PrincipalKind == "api_credential" && x.PrincipalId == credentialId.ToString("N"), ct);
            if (binding is null)
            {
                binding = new() { SourceNamespaceId = namespaceId, PrincipalKind = "api_credential", PrincipalId = credentialId.ToString("N") };
                db.IncidentReceiverPrincipalBindings.Add(binding);
            }
            binding.IsEnabled = request.IsEnabled;
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(actor)) return Results.Forbid();
            source.Revision++;
            source.UpdatedBy = actor;
            source.UpdatedAtUtc = time.GetUtcNow();
            Audit(db, source, actor, "principal-binding-changed", credentialId, binding.IsEnabled);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return IncidentReceiverContract.Problem(409, "source-revision-conflict"); }
            return Results.Ok(source);
        });
        group.MapPatch("/{namespaceId:guid}", async (Guid namespaceId, HttpContext http, HelpdeskDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var request = await IncidentReceiverEndpoints.ReadBoundedAsync<ChangeGrant>(http.Request, ct);
            if (request is null) return IncidentReceiverContract.Problem(400, "invalid-source-registration");
            var source = await db.IncidentReceiverSources.SingleOrDefaultAsync(x => x.SourceNamespaceId == namespaceId, ct);
            if (source is null) return Results.NotFound();
            if (source.Revision != request.ExpectedRevision) return IncidentReceiverContract.Problem(409, "source-revision-conflict");
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(actor)) return Results.Forbid();
            source.IsEnabled = request.IsEnabled;
            source.Revision++;
            source.UpdatedBy = actor;
            source.UpdatedAtUtc = time.GetUtcNow();
            Audit(db, source, actor, "source-state-changed", Guid.Empty, source.IsEnabled);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return IncidentReceiverContract.Problem(409, "source-revision-conflict"); }
            return Results.Ok(source);
        });
        group.MapGet("/", async (HelpdeskDbContext db, CancellationToken ct) => Results.Ok(
            await db.IncidentReceiverSources.AsNoTracking().OrderBy(x => x.SourceInstanceId).Take(256).ToListAsync(ct)));
    }

    private static async Task<bool> ValidCredentialAsync(RatelDeskIdentityDbContext identity, Guid id, string organizationId,
        TimeProvider time, CancellationToken ct)
    {
        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return credential is not null && credential.Purpose == "api" && credential.OrganizationId == organizationId &&
            credential.RevokedAtUtc is null && credential.ExpiresAtUtc > time.GetUtcNow() &&
            await identity.Users.AnyAsync(x => x.Id == credential.OwnerUserId && x.IsEnabled, ct);
    }

    private static void Audit(HelpdeskDbContext db, IncidentReceiverSource source, string actor, string action, Guid credentialId, bool enabled) =>
        db.IncidentReceiverSourceAudits.Add(new()
        {
            SourceNamespaceId = source.SourceNamespaceId, Revision = source.Revision, ActorId = actor,
            AtUtc = source.UpdatedAtUtc, Action = action, OrganizationId = source.OrganizationId, CustomerId = source.CustomerId,
            PrincipalKind = credentialId == Guid.Empty ? string.Empty : "api_credential",
            PrincipalId = credentialId == Guid.Empty ? string.Empty : credentialId.ToString("N"), IsEnabled = enabled
        });
}
