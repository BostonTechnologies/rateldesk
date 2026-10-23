using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.RequestTasks;

public sealed partial class RequestTaskApprovalService
{
    public async Task<PublicRequestTaskApprovalDto?> GetForActorAsync(string taskId, string actorId, CurrentUserAccessProfile access, CancellationToken ct)
    {
        var loaded = await LoadActorApprovalAsync(taskId, actorId, access, ct);
        if (loaded is null) return null;
        var (request, task, approval) = loaded.Value;
        return new PublicRequestTaskApprovalDto
        {
            RequestId = request.Id, RequestTaskId = task.Id, TrackingId = request.TrackingId,
            RequestTitle = request.Title, RequestDescription = request.Description, TaskName = task.Name,
            ApproverName = approval.ApproverName, ApproverEmail = approval.ApproverEmail,
            ApprovalStatus = approval.Status, DueAt = task.DueAt, ReviewedAtUtc = approval.ReviewedAtUtc,
            ViewedAtUtc = approval.ViewedAtUtc,
            CanReview = task.Status == RequestTaskStatus.PendingApproval && approval.Status == RequestTaskApprovalStatus.Pending &&
                        (task.DueAt is null || task.DueAt > DateTimeOffset.UtcNow),
            PayloadFields = BuildPayloadFields(request.PayloadJson)
        };
    }

    public async Task<RequestTaskApprovalReviewResult> ReviewForActorAsync(string taskId, string actorId, CurrentUserAccessProfile access, bool approve, string? reason, CancellationToken ct)
    {
        return await ReviewDecisionAsync(taskId, () => LoadActorApprovalAsync(taskId, actorId, access, ct),
            approve, reason, actorId, ct);
    }

    private async Task<(Request Request, RequestTask Task, RequestTaskApproval Approval)?> LoadActorApprovalAsync(string taskId, string actorId, CurrentUserAccessProfile access, CancellationToken ct)
    {
        if (!access.IsAuthenticated || string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(access.Email)) return null;
        var task = await db.RequestTasks.IgnoreQueryFilters().SingleOrDefaultAsync(candidate => candidate.Id == taskId && candidate.Type == RequestTaskType.Approval, ct);
        if (task is null) return null;
        var request = await db.Requests.IgnoreQueryFilters().SingleOrDefaultAsync(candidate => candidate.Id == task.RequestId, ct);
        if (request is null || (!access.CanManageRequest(request.OrganizationId) &&
            !access.HasPermission(HelpdeskPermissions.RequestUser, request.OrganizationId) &&
            !access.HasPermission(HelpdeskPermissions.SelfServiceUser, request.OrganizationId))) return null;

        var email = access.Email.Trim().ToLowerInvariant();
        var approvals = await db.RequestTaskApprovals.IgnoreQueryFilters()
            .Where(candidate => candidate.RequestTaskId == task.Id && candidate.RequestId == request.Id && candidate.ApproverEmail.ToLower() == email)
            .ToListAsync(ct);
        var matches = approvals.Where(candidate =>
            string.Equals(candidate.ApproverSource, "User", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(candidate.ApproverId, actorId, StringComparison.Ordinal)
                : string.Equals(candidate.ApproverSource, "Customer", StringComparison.OrdinalIgnoreCase) &&
                  !string.IsNullOrWhiteSpace(access.CustomerId) && string.Equals(candidate.ApproverId, access.CustomerId, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? (request, task, matches[0]) : null;
    }
}
