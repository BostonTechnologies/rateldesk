using Helpdesk.API.Ops;
using Helpdesk.API.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.API.Endpoints.Ops;

public static class AiAgentOpsEndpoints
{
    public static IEndpointRouteBuilder MapAiAgentOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/ai-agent/logs", (
            [FromServices] AiAgentOpsLogBuffer logs,
            [FromQuery] long? since,
            [FromQuery] string? level,
            [FromQuery] string? contains,
            [FromQuery] string? correlationId,
            [FromQuery] int? limit) =>
            Results.Ok(logs.Query(since, level, contains, correlationId, limit)))
            .RequireAuthorization(AiAgentOrOwnerMcpAuthenticationHandler.PolicyName)
            .WithTags("AI Agent Ops")
            .WithSummary("Read recent redacted Helpdesk API logs for AI-agent diagnostics.")
            .WithDescription("Returns a bounded, redacted, structured recent-log window. Raw container logs and secrets are never returned.");

        return app;
    }
}
