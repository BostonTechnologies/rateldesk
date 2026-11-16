using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Incident;

public class CreateTicketRelationDto
{
    public string TargetTicketId { get; set; } = string.Empty;

    public TicketRelationType RelationType { get; set; }

    public bool CloseSourceTicket { get; set; }

    public string? Note { get; set; }
}

public class TicketRelationDto
{
    public string SourceType { get; set; } = "incidents";
    public string TargetType { get; set; } = "incidents";
    public string? SourceLifecycleState { get; set; }
    public string? TargetLifecycleState { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public Guid Id { get; set; }

    public string SourceTicketId { get; set; } = string.Empty;

    public string SourceTrackingId { get; set; } = string.Empty;

    public string SourceSubject { get; set; } = string.Empty;

    public TicketState SourceState { get; set; }

    public string TargetTicketId { get; set; } = string.Empty;

    public string TargetTrackingId { get; set; } = string.Empty;

    public string TargetSubject { get; set; } = string.Empty;

    public TicketState TargetState { get; set; }

    public TicketRelationType RelationType { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public string CreatedByUserName { get; set; } = string.Empty;

    public string? Note { get; set; }

    public bool IsOutgoing { get; set; }
}

public sealed record LinkTicketRequest(string TargetTicketId, string? Note = null);
public sealed record TicketRelationPage(IReadOnlyList<TicketRelationDto> Items, int TotalCount, int Page, int PageSize);
public sealed record TicketLinkChoice(string Id, string Type, string Reference, string Title, string State, string Url);
public sealed record TicketLinkChoicePage(IReadOnlyList<TicketLinkChoice> Items, int TotalCount, int Page, int PageSize);

public class TicketRelationCreateResultDto
{
    public TicketRelationDto Relation { get; set; } = new();

    public IncidentDto SourceIncident { get; set; } = new();

    public IncidentDto TargetIncident { get; set; } = new();

    public List<string> AddedParentListeners { get; set; } = new();
}
