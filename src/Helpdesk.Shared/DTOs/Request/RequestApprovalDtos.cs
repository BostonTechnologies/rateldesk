using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Request;

public sealed class PublicRequestTaskApprovalDto
{
    public string RequestId { get; set; } = string.Empty;
    public string RequestTaskId { get; set; } = string.Empty;
    public string TrackingId { get; set; } = string.Empty;
    public string RequestTitle { get; set; } = string.Empty;
    public string RequestDescription { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string ApproverName { get; set; } = string.Empty;
    public string ApproverEmail { get; set; } = string.Empty;
    public RequestTaskApprovalStatus ApprovalStatus { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? ViewedAtUtc { get; set; }
    public bool CanReview { get; set; }
    public List<RequestApprovalPayloadFieldDto> PayloadFields { get; set; } = new();
}

public sealed class RequestApprovalPayloadFieldDto
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed record PublicRequestApprovalReviewRequest(string Email, string Token, string? Reason = null);

public sealed record PublicRequestReviewDto(string TrackingId, string RequestTitle, string RequestDescription, string TaskName,
    RequestTaskApprovalStatus ApprovalStatus, DateTimeOffset? DueAt, DateTimeOffset? ReviewedAtUtc, bool CanReview,
    List<RequestApprovalPayloadFieldDto> PayloadFields);
