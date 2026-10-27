namespace Helpdesk.Shared.DTOs.AiOps;

public sealed record AiPromptDto(Guid Id, string OrganizationId, string Name, string Description, string Content, int Revision, bool IsDefault, bool Archived);
public sealed record SaveAiPromptDto(string OrganizationId, string Name, string Description, string Content, int ExpectedRevision, bool IsDefault, bool Archived);
public sealed record CreateAiInvestigationDto(Guid Id, string OrganizationId, string Title, string[] TicketIds, Guid[] PromptIds);
public sealed record AiContextTicketDto(string Id, string Type, string TrackingId, string Title);
public sealed record AiInvestigationDto(Guid Id, string OrganizationId, string Title, string TicketId, string TicketType, string ContextSnapshot, DateTime CreatedUtc, Helpdesk.Shared.AiAssistant.Chat.ChatState State = Helpdesk.Shared.AiAssistant.Chat.ChatState.Idle);
