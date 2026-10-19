using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Services;

namespace Helpdesk.Application.RequestTasks;

/// <summary>Authenticated review retains the stored designated approver identity and organization authority.</summary>
public interface IAuthenticatedRequestTaskApprovalService
{
    Task<PublicRequestTaskApprovalDto?> GetForActorAsync(string taskId, string actorId, CurrentUserAccessProfile access, CancellationToken ct);
    Task<RequestTaskApprovalReviewResult> ReviewForActorAsync(string taskId, string actorId, CurrentUserAccessProfile access, bool approve, string? reason, CancellationToken ct);
}
