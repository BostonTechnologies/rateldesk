using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.RequestTasks;

public static class RequestTaskApprovalEndpoints
{
    public static void MapRequestTaskApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAuthenticatedRequestApprovalEndpoints();
        app.MapGet("/api/v1/request-task-approvers", SearchApprovers)
            .WithTags("Request Tasks")
            .RequireAuthorization("HelpdeskAdmin")
            .WithSummary("Search approval task approver candidates.");

        var publicGroup = app.MapGroup("/api/v1/request-approvals/public")
            .WithTags("Request Approvals")
            .AllowAnonymous();

        publicGroup.MapGet("/{trackingId}/{taskId}", GetPublicApproval);
        publicGroup.MapPost("/{trackingId}/{taskId}/approve", ApprovePublicApproval);
        publicGroup.MapPost("/{trackingId}/{taskId}/reject", RejectPublicApproval);
    }

    private static async Task<IResult> SearchApprovers(
        [FromServices] HelpdeskDbContext db,
        [FromQuery] string? organizationId,
        [FromQuery] string? q,
        [FromQuery] int? pageSize,
        CancellationToken token)
    {
        var take = Math.Clamp(pageSize ?? 50, 1, 100);
        var search = q?.Trim();
        var organizations = await db.Organizations.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, token);
        var usersQuery = db.Users.AsNoTracking();
        var customersQuery = db.Customers.AsNoTracking().Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled);
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            usersQuery = usersQuery.Where(x => x.OrganizationId == organizationId);
            customersQuery = customersQuery.Where(x => x.OrganizationId == organizationId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search}%";
            if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                usersQuery = usersQuery.Where(x => EF.Functions.ILike(x.Name, like) || EF.Functions.ILike(x.Email, like));
                customersQuery = customersQuery.Where(x => EF.Functions.ILike(x.Name, like) || EF.Functions.ILike(x.Email, like));
            }
            else
            {
                usersQuery = usersQuery.Where(x => EF.Functions.Like(x.Name, like) || EF.Functions.Like(x.Email, like));
                customersQuery = customersQuery.Where(x => EF.Functions.Like(x.Name, like) || EF.Functions.Like(x.Email, like));
            }
        }

        var users = await usersQuery
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Email)
            .Take(take)
            .Select(x => new RequestTaskApprovalApproverModel
            {
                Source = "User",
                Id = x.Id,
                Name = x.Name,
                Email = x.Email,
                OrganizationId = x.OrganizationId
            })
            .ToListAsync(token);
        var remaining = Math.Max(0, take - users.Count);
        var customers = remaining == 0
            ? new List<RequestTaskApprovalApproverModel>()
            : await customersQuery
                .Where(x => !string.IsNullOrWhiteSpace(x.Email))
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Email)
                .Take(remaining)
                .Select(x => new RequestTaskApprovalApproverModel
                {
                    Source = "Customer",
                    Id = x.Id,
                    Name = x.Name,
                    Email = x.Email,
                    OrganizationId = x.OrganizationId
                })
                .ToListAsync(token);

        var results = users.Concat(customers)
            .DistinctBy(x => $"{x.Source}:{x.Id}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
        foreach (var result in results)
        {
            if (!string.IsNullOrWhiteSpace(result.OrganizationId) && organizations.TryGetValue(result.OrganizationId, out var organizationName))
            {
                result.OrganizationName = organizationName;
            }
        }

        return Results.Ok(results);
    }

    private static async Task<IResult> GetPublicApproval(
        [FromRoute] string trackingId,
        [FromRoute] string taskId,
        [FromQuery] string email,
        [FromQuery] string token,
        [FromServices] IRequestTaskApprovalService approvalService,
        CancellationToken ct)
    {
        var approval = await approvalService.GetPublicApprovalAsync(trackingId, taskId, email, token, ct);
        return approval is null ? Results.Unauthorized() : Results.Ok(approval);
    }

    private static async Task<IResult> ApprovePublicApproval(
        [FromRoute] string trackingId,
        [FromRoute] string taskId,
        [FromBody] PublicRequestApprovalReviewRequest request,
        [FromServices] IRequestTaskApprovalService approvalService,
        [FromServices] IWorkflowEngine workflowEngine,
        CancellationToken ct)
    {
        var result = await approvalService.ApproveAsync(trackingId, taskId, request.Email, request.Token, ct);
        if (!result.Success)
        {
            return result.ErrorCode == "approval_changed"
                ? Results.Conflict(new { code = result.ErrorCode, message = result.Error }) : Results.BadRequest(result.Error);
        }

        await workflowEngine.RunAsync(result.RequestId, WorkflowRunReason.ApprovalReceived, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RejectPublicApproval(
        [FromRoute] string trackingId,
        [FromRoute] string taskId,
        [FromBody] PublicRequestApprovalReviewRequest request,
        [FromServices] IRequestTaskApprovalService approvalService,
        CancellationToken ct)
    {
        var result = await approvalService.RejectAsync(trackingId, taskId, request.Email, request.Token, request.Reason, ct);
        return result.Success ? Results.Ok(result) : result.ErrorCode == "approval_changed"
            ? Results.Conflict(new { code = result.ErrorCode, message = result.Error }) : Results.BadRequest(result.Error);
    }
}
