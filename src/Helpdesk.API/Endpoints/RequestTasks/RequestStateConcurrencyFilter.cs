using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.RequestTasks;

public sealed class RequestStateConcurrencyFilter(ILogger<RequestStateConcurrencyFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.HttpContext.RequestServices.GetRequiredService<HelpdeskDbContext>().ChangeTracker.Clear();
            logger.LogInformation("A request operation conflicted with a committed state change.");
            return Results.Conflict(new
            {
                code = "request_state_changed",
                message = "Another operation changed this request or task. Refresh its current state and retry the action."
            });
        }
    }
}
