using Helpdesk.Shared.Models;
using Helpdesk.Shared.DTOs.Request;

namespace Helpdesk.Application.RequestTasks;

public interface IRequestTaskApprovalService
{
    Task<RequestTask> StartApprovalAsync(RequestTask task, CancellationToken ct);
    Task<PublicRequestTaskApprovalDto?> GetPublicApprovalAsync(string trackingId, string taskId, string email, string token, CancellationToken ct);
    Task<RequestTaskApprovalReviewResult> ApproveAsync(string trackingId, string taskId, string email, string token, CancellationToken ct);
    Task<RequestTaskApprovalReviewResult> RejectAsync(string trackingId, string taskId, string email, string token, string? reason, CancellationToken ct);
    Task<int> RetryUnsentApprovalEmailsAsync(CancellationToken ct);
    Task<int> ExpireOverdueApprovalsAsync(CancellationToken ct);
}

public sealed class RequestTaskApprovalReviewResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public RequestTaskApprovalStatus? ApprovalStatus { get; init; }

    public static RequestTaskApprovalReviewResult Failed(string error) => Failed(error, null);

    public static RequestTaskApprovalReviewResult Failed(string error, string? errorCode) => new()
    {
        Success = false,
        Error = error,
        ErrorCode = errorCode
    };

    public static RequestTaskApprovalReviewResult Succeeded(
        string requestId,
        string taskId,
        RequestTaskApprovalStatus approvalStatus) => new()
        {
            Success = true,
            RequestId = requestId,
            TaskId = taskId,
            ApprovalStatus = approvalStatus
        };
}

public interface IRequestTaskApprovalTimeoutProcessor
{
    Task<int> ProcessAsync(CancellationToken ct);
}
