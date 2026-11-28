using System.Security.Claims;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Authentication;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Ticketing;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Integrations;

public static class IncidentReceiverSourceEndpoints
{
    public sealed record RegisterSource(Guid SourceInstanceId, string OrganizationId, string CustomerId, Guid CredentialId);
    public sealed record ChangeGrant(long ExpectedRevision, bool IsEnabled);
    public sealed record ChangeQueueRoutes(long ExpectedRevision, IReadOnlyList<string> SupportGroupIds);

    public static void MapIncidentReceiverSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations/netratel/sources").WithTags("NetRatel Source Administration")
            .RequireAuthorization(ServiceIdentityServiceCollectionExtensions.ManagementPolicy);
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
            if (await (from existing in db.IncidentReceiverSources
                       join binding in db.IncidentReceiverPrincipalBindings on existing.SourceNamespaceId equals binding.SourceNamespaceId
                       where existing.SourceInstanceId == request.SourceInstanceId && binding.PrincipalKind == "api_credential"
                       select existing).AnyAsync(ct)) return IncidentReceiverContract.Problem(409, "source-already-registered");
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
        }).Accepts<RegisterSource>("application/json");
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
        }).Accepts<ChangeGrant>("application/json");
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
        }).Accepts<ChangeGrant>("application/json");
        group.MapGet("/", async (HelpdeskDbContext db, CancellationToken ct) => Results.Ok(
            await db.IncidentReceiverSources.AsNoTracking().OrderBy(x => x.SourceInstanceId).Take(256).ToListAsync(ct)));
        group.MapGet("/{namespaceId:guid}/queues", async (Guid namespaceId, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var source = await db.IncidentReceiverSources.AsNoTracking().SingleOrDefaultAsync(row => row.SourceNamespaceId == namespaceId, ct);
            return source == null ? Results.NotFound() : Results.Ok(new
            {
                source.Revision,
                SupportGroupIds = await db.IncidentReceiverQueueRoutes.Where(route => route.SourceNamespaceId == namespaceId)
                    .Select(route => route.SupportGroupId).OrderBy(id => id).ToArrayAsync(ct)
            });
        }).WithName("GetIncidentReceiverQueueRoutes");
        group.MapPut("/{namespaceId:guid}/queues", async (
            Guid namespaceId, ChangeQueueRoutes request, ClaimsPrincipal principal, HelpdeskDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (request.SupportGroupIds == null || request.SupportGroupIds.Count > 100 ||
                request.SupportGroupIds.Any(id => !IncidentQueueContract.ValidGroupId(id)) ||
                request.SupportGroupIds.Count != request.SupportGroupIds.Distinct().Count())
                return IncidentReceiverContract.Problem(400, "invalid-queue-routes");
            var source = await db.IncidentReceiverSources.SingleOrDefaultAsync(row => row.SourceNamespaceId == namespaceId, ct);
            if (source == null) return Results.NotFound();
            if (source.Revision != request.ExpectedRevision)
                return IncidentReceiverContract.Problem(409, "source-revision-conflict");
            var actor = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(actor) || principal.HasClaim("auth_mode", "integration")) return Results.Forbid();
            var wanted = request.SupportGroupIds.ToArray();
            if (await TicketQueueAssignment.Destinations(db, source.OrganizationId).CountAsync(queue => wanted.Contains(queue.Id), ct) != wanted.Length)
                return IncidentReceiverContract.Problem(422, "invalid-queue-routes");

            var current = await db.IncidentReceiverQueueRoutes.Where(route => route.SourceNamespaceId == namespaceId).ToArrayAsync(ct);
            db.IncidentReceiverQueueRoutes.RemoveRange(current.Where(route => !wanted.Contains(route.SupportGroupId)));
            db.IncidentReceiverQueueRoutes.AddRange(wanted.Except(current.Select(route => route.SupportGroupId)).Select(id => new IncidentReceiverQueueRoute
            {
                SourceNamespaceId = namespaceId,
                SupportGroupId = id
            }));
            source.Revision++;
            source.UpdatedBy = actor;
            source.UpdatedAtUtc = time.GetUtcNow();
            Audit(db, source, actor, "queue-routes-changed", Guid.Empty, source.IsEnabled);
            db.ActivityLogs.Add(new ActivityLog
            {
                RelatedEntityId = namespaceId.ToString("D"), UserId = actor,
                Message = $"Updated NetRatel incident queue allowlist to [{string.Join(", ", wanted)}]."
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return IncidentReceiverContract.Problem(409, "source-revision-conflict"); }
            return Results.Ok(new { source.Revision, SupportGroupIds = wanted });
        }).WithName("SetIncidentReceiverQueueRoutes");
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
