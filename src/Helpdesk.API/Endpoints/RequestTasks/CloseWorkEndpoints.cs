using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Services;

namespace Helpdesk.API.Endpoints.RequestTasks;

internal static class CloseWorkEndpoints
{
    internal static void MapCloseWork(this RouteGroupBuilder group, bool closeParent)
    {
        group.MapPost("/bulk/close", async ([FromBody] CloseRequestWorkDto body, [FromServices] CloseRequestWork command,
            ClaimsPrincipal user, [FromServices] ITenantContext tenant, [FromServices] ICurrentUserAccessService accessService,
            [FromServices] ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length > AdministrativeClosure.MaximumReasonLength ||
                body.Selection is not { Count: > 0 and <= 50 } || body.Selection.Any(x => x == null ||
                    string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 200 || x.StateVersion < 0) ||
                body.Selection.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != body.Selection.Count)
                return Results.BadRequest("Select 1 to 50 distinct records and enter a closure reason of up to 1000 characters.");
            var actorId = tenant.UserId;
            if (string.IsNullOrWhiteSpace(actorId))
                return Results.Forbid();
            var results = new List<CloseWorkResult>();
            foreach (var selected in body.Selection)
            {
                var access = await accessService.ResolveAsync(user, ct);
                try
                {
                    results.Add(await command.ExecuteAsync(selected, closeParent, body.Reason.Trim(), actorId, access, ct));
                }
                catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
                {
                    loggerFactory.CreateLogger("CloseRequestWork").LogError(ex, "Could not confirm closure of {Id}.", selected.Id);
                    results.Add(new(selected.Id, "not_confirmed"));
                }
            }
            return Results.Ok(results);
        });
    }
}
