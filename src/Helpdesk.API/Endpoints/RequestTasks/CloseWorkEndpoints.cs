using System.Security.Claims;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Services;

namespace Helpdesk.API.Endpoints.RequestTasks;

internal static class CloseWorkEndpoints
{
    internal static void MapCloseWork(this RouteGroupBuilder group, bool closeParent)
    {
        group.MapPost("/bulk/close", async (CloseRequestWorkDto body, CloseRequestWork command,
            ClaimsPrincipal user, ITenantContext tenant, ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length > AdministrativeClosure.MaximumReasonLength ||
                body.Selection is not { Count: > 0 and <= 50 } || body.Selection.Any(x => x == null ||
                    string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 200 || x.StateVersion < 0) ||
                body.Selection.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != body.Selection.Count)
                return Results.BadRequest("Select 1 to 50 distinct records and enter a closure reason of up to 1000 characters.");
            var actorId = tenant.UserId;
            if (string.IsNullOrWhiteSpace(actorId))
                return Results.Forbid();
            var access = await accessService.ResolveAsync(user, ct);
            var results = new List<CloseWorkResult>();
            foreach (var selected in body.Selection)
                results.Add(await command.ExecuteAsync(selected, closeParent, body.Reason.Trim(), actorId, access, ct));
            return Results.Ok(results);
        });
    }
}
