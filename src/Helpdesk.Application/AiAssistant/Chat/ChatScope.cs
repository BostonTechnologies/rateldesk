using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Application.AiAssistant.Chat;

// Deliberate organization admission; a general conversation never resolves a ticket.
public sealed record ChatScope(ChatConversationScope Kind, string? OrganizationId, string? TicketType, string? TicketId)
{
    public static ChatScope Organization(string organizationId) => new(ChatConversationScope.Organization, organizationId, null, null);
    public static ChatScope Ticket(string type, string id) => new(ChatConversationScope.Ticket, null, type, id);
}
