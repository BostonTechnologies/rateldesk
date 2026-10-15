using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;

namespace Helpdesk.Shared.DTOs.Email;

public sealed record PendingEmailQuery(
    string? Search = null,
    EmailDeliveryStatus? Status = null,
    string? OrganizationId = null,
    string? Sender = null,
    string? Recipient = null,
    string? Reason = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string Sort = "created",
    bool Descending = true,
    int Page = 0,
    int PageSize = 25);

public sealed class PendingEmailRow : TicketTimelineEventDto
{
    public string TrackingId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string TicketType { get; set; } = "incidents";
    public string? OrganizationId { get; set; }
    public string? OrganizationName { get; set; }
    public string? Sender { get; set; }
    public string QueueState { get; set; } = string.Empty;
    public bool CanCancel { get; set; }
    public bool CanRemove { get; set; }
}

public sealed record PendingEmailPage(IReadOnlyList<PendingEmailRow> Items, int Total, int Page, int PageSize);
public sealed record PendingEmailActionRequest(Guid[] Ids, string Action, string Reason, bool Confirmed);
public sealed record PendingEmailActionResult(Guid Id, string Outcome, string Message);
public sealed record PendingEmailActionResponse(IReadOnlyList<PendingEmailActionResult> Results);
public sealed record PendingEmailOrganization(string Id, string Label);
