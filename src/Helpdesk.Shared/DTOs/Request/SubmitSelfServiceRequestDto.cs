namespace Helpdesk.Shared.DTOs.Request;

public sealed class SubmitSelfServiceRequestDto
{
    public Guid? SubmissionId { get; set; }
    public string RequestFormId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? RequesterEmail { get; set; }
    public string? RequesterName { get; set; }
    public string? RequestedForPersonId { get; set; }
    public SelfServiceRequestPersonSource? RequestedForPersonSource { get; set; }
}

public sealed class SelfServiceSubmissionContextDto
{
    public string? OrganizationId { get; set; }
    public string? OrganizationName { get; set; }
    public string? RequestedForName { get; set; }
    public bool CanSubmit { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}

public sealed class SelfServiceRequestSubmittedDto
{
    public string RequestId { get; set; } = string.Empty;
    public string TrackingId { get; set; } = string.Empty;
}

public enum SelfServiceRequestPersonSource
{
    User = 0,
    Customer = 1
}

public sealed class SelfServiceRequestUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public SelfServiceRequestPersonSource Source { get; set; }
    public string? OrganizationId { get; set; }
    public string? OrganizationName { get; set; }
    public string? Role { get; set; }
}
