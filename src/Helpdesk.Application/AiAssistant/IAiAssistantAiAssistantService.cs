using Helpdesk.Shared.DTOs;

namespace Helpdesk.Application.AiAssistant;

/// <summary>A dispatch key is already bound to another operation, or a concurrent decision requires a fresh read.</summary>
public sealed class AiInvestigationDispatchConflictException(string message) : Exception(message);

public interface IAiAssistantAiAssistantService
{
    Task<IReadOnlyList<AiAssistantWebhookConfigurationDto>> ListConfigurationsAsync(string organizationId, CancellationToken ct);
    Task<AiAssistantWebhookConfigurationDto> UpsertConfigurationAsync(Guid? id, UpsertAiAssistantWebhookConfigurationDto request, string actor, CancellationToken ct);
    Task SetConfigurationStateAsync(Guid id, string organizationId, bool enabled, bool archived, string actor, CancellationToken ct);
    Task<IReadOnlyList<AiAssistantWebhookConfigurationDto>> GetEligibleAsync(string ticketId, string ticketType, CancellationToken ct);
    Task<AiInvestigationInvocationDto> DispatchAsync(string ticketId, string ticketType, DispatchAiInvestigationDto request, string actor, CancellationToken ct);
    Task<IReadOnlyList<AiInvestigationWorklogEntryDto>> GetWorklogAsync(string ticketId, string ticketType, CancellationToken ct);
    Task<bool> AppendMcpWorklogAsync(Guid invocationId, AppendAiInvestigationWorklogDto update, CancellationToken ct);
}
