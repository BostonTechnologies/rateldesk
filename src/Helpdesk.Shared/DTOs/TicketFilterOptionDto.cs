namespace Helpdesk.Shared.DTOs;

/// <summary>An authorized, stable filter identity; labels are presentation only.</summary>
public sealed record TicketFilterOptionDto(string Id, string Label);
public sealed record TicketFilterOptionsDto(List<TicketFilterOptionDto> Items, bool HasMore);
