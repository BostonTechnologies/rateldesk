using System.Text.Json.Serialization;

namespace Helpdesk.Shared.DTOs.TicketQueue;

public sealed record TicketQueueOptionDto(string? Id, string Name);

public enum QueueAssigneeAction
{
    Keep = 0,
    Clear = 1,
    Replace = 2
}

public sealed record TicketQueueSelectionDto(string Id, long? ExpectedVersion);

public sealed record AssignTicketQueueDto(
    IReadOnlyList<TicketQueueSelectionDto> Tickets,
    [property: JsonRequired] string? SupportGroupId,
    QueueAssigneeAction AssigneeAction,
    string? AssigneeId,
    string? Reason);
