using System.Security.Claims;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.Services;

namespace Helpdesk.API.Endpoints.RequestTasks;

public static class AuthenticatedRequestApprovalEndpoints
{
    public static void MapAuthenticatedRequestApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/request-tasks/{taskId}/approval")
            .WithTags("Request Approvals")
            .RequireAuthorization();
        group.MapGet("/", async (string taskId, HttpContext context, ICurrentUserAccessService accessService,
            IAuthenticatedRequestTaskApprovalService approvals, CancellationToken ct) =>
        {
            var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(actor)) return Results.Forbid();
            var access = await accessService.ResolveAsync(context.User, ct);
            var result = await approvals.GetForActorAsync(taskId, actor, access, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        group.MapPost("/approve", (string taskId, HttpContext context,
            ICurrentUserAccessService accessService, IAuthenticatedRequestTaskApprovalService approvals,
            IWorkflowEngine workflows, CancellationToken ct) =>
            ReviewAsync(taskId, true, null, context, accessService, approvals, workflows, ct));
        group.MapPost("/reject", (string taskId, AuthenticatedRequestRejection request, HttpContext context,
            ICurrentUserAccessService accessService, IAuthenticatedRequestTaskApprovalService approvals,
            IWorkflowEngine workflows, CancellationToken ct) =>
            ReviewAsync(taskId, false, request.Reason, context, accessService, approvals, workflows, ct));
    }

    private static async Task<IResult> ReviewAsync(string taskId, bool approve, string? reason, HttpContext context,
        ICurrentUserAccessService accessService, IAuthenticatedRequestTaskApprovalService approvals,
        IWorkflowEngine workflows, CancellationToken ct)
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actor)) return Results.Forbid();
        if (!approve && (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["A rejection reason of 1–1000 characters is required."] });
        var access = await accessService.ResolveAsync(context.User, ct);
        var result = await approvals.ReviewForActorAsync(taskId, actor, access, approve, reason, ct);
        if (!result.Success) return result.ErrorCode == "approval_not_found" ? Results.NotFound()
            : Results.Conflict(new { code = result.ErrorCode ?? "approval_not_pending", message = result.Error });
        // A committed approval may outlive a failed workflow continuation. The engine
        // resumes idempotently on a repeated accepted decision without repeating the
        // approval transition, notification or audit record.
        if (approve) await workflows.RunAsync(result.RequestId, WorkflowRunReason.ApprovalReceived, ct);
        return Results.Ok(result);
    }

    public sealed record AuthenticatedRequestRejection(string Reason);
}
