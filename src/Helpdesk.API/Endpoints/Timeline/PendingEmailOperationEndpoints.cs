using System.Security.Claims;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Email;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Timeline;

internal static class PendingEmailOperationEndpoints
{
    public static void MapPendingEmailOperations(this RouteGroupBuilder group, string suffix)
    {
        group.MapPost("/pending/operations", async (PendingEmailSelectionRequest request, HttpContext context,
            HelpdeskDbContext db, PendingEmailActions actions, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await Service(db, actions).ResolveAsync(request, actor, Profile(context), ct)); }
            catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
        }).WithName($"ResolvePendingEmailOperation{suffix}");

        group.MapGet("/pending/operations/{id:guid}", async (Guid id, HttpContext context,
            HelpdeskDbContext db, PendingEmailActions actions, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return Results.Unauthorized();
            var result = await Service(db, actions).GetAsync(id, actor, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName($"GetPendingEmailOperation{suffix}");

        group.MapGet("/pending/operations/{id:guid}/results", async (Guid id, int? page, HttpContext context,
            HelpdeskDbContext db, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return Results.Unauthorized();
            if (page is < 0 or > 1000000) return Results.BadRequest();
            if (!await db.Set<PendingEmailOperationRecord>().AnyAsync(x => x.Id == id && x.ActorId == actor, ct))
                return Results.NotFound();
            return Results.Ok(await db.Set<PendingEmailOperationTarget>().AsNoTracking()
                .Where(x => x.OperationId == id && x.Outcome != null).OrderBy(x => x.Sequence)
                .Skip((page ?? 0) * 100).Take(100)
                .Select(x => new PendingEmailActionResult(x.DeliveryId, x.Outcome!, x.Message ?? "")).ToListAsync(ct));
        }).WithName($"GetPendingEmailOperationResults{suffix}");

        group.MapPost("/pending/operations/{id:guid}/confirm", async (Guid id, PendingEmailOperationConfirmation request,
            HttpContext context, HelpdeskDbContext db, PendingEmailActions actions, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return Results.Unauthorized();
            try { return await Service(db, actions).ConfirmAsync(id, actor, request, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
        }).WithName($"ConfirmPendingEmailOperation{suffix}");

        group.MapPost("/pending/operations/{id:guid}/process", async (Guid id, HttpContext context,
            HelpdeskDbContext db, PendingEmailActions actions, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var result = await Service(db, actions).ProcessAsync(id, actor, Profile(context), ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
        }).WithName($"ProcessPendingEmailOperation{suffix}");
    }

    private static string? Actor(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
    private static CurrentUserAccessProfile Profile(HttpContext context) => CurrentUserAccessProfile.FromClaims(context.User);
    private static PendingEmailOperationService Service(HelpdeskDbContext db, PendingEmailActions actions) => new(db, actions, TimeProvider.System);
}
