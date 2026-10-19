using System.ComponentModel.DataAnnotations;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Models;

namespace Helpdesk.Mcp.Tools;

/// <summary>Fixed adapters for the existing human AI administration and ticket conversation APIs.</summary>
internal static class OperatorAiOperations
{
    private const string Tool = "helpdesk_ai_assistant";
    private const string Webhooks = "/api/v1/admin/ai-assistant-webhooks";
    private const string Ticket = "/api/v1/{ticketType}/{ticketId}/ai-assistant";
    private const string Chat = Ticket + "/chat";
    private const string TicketAuthority = "Current Incident, Request or Change management authority in the actual ticket organization, or HelpdeskAdmin";
    private const string ChatAuthority = TicketAuthority + "; native chat requires an enabled PostgreSQL deployment and exact ticket/conversation binding";

    public static IReadOnlyList<OperatorOperation> All { get; } =
    [
        new(Tool, "webhook_configurations", "GET", Webhooks + "/", QueryType: typeof(OperatorAiOrganizationQuery),
            Description: "List AI webhook configurations for one explicit organization, including enabled/archived state and signing-secret availability; stored secrets remain write-only."),
        new(Tool, "create_webhook", "POST", Webhooks + "/", BodyType: typeof(OperatorAiWebhookConfiguration),
            Description: "Create an organization-bound AI assistance webhook with numeric ticket areas (0 incidents, 1 requests, 2 changes). The API validates the configured endpoint; no investigation is dispatched by saving settings."),
        new(Tool, "update_webhook", "PUT", Webhooks + "/{webhookId}", BodyType: typeof(OperatorAiWebhookConfiguration),
            Description: "Replace the selected AI webhook configuration within its explicit organization. Omit signingSecret or supply an empty value to preserve it; a supplied nonempty secret rotates it without readback."),
        new(Tool, "set_webhook_state", "POST", Webhooks + "/{webhookId}/state", BodyType: typeof(OperatorAiWebhookState),
            RequiredBodyFields: ["enabled", "archived"], Description: "Enable, disable, archive or restore an organization-bound AI webhook configuration with an attributed administrative audit."),
        new(Tool, "eligible_configurations", "GET", Ticket + "/eligible-configurations", QueryType: typeof(OperatorAiTicketQuery), Authority: TicketAuthority,
            Description: "List active AI webhook configurations eligible for this incident, request or change and its actual organization."),
        new(Tool, "dispatch_investigation", "POST", Ticket + "/investigations", BodyType: typeof(OperatorAiDispatch), QueryType: typeof(OperatorAiTicketQuery),
            ExternalIo: true, Authority: TicketAuthority,
            Description: "Dispatch one AI investigation using a saved eligible configuration and an explicit operator assistance request. Keep the same idempotencyKey for retries of this exact ticket, configuration and request; read investigation_worklog before retrying an uncertain result."),
        new(Tool, "investigation_worklog", "GET", Ticket + "/worklog", QueryType: typeof(OperatorAiTicketQuery), Authority: TicketAuthority,
            Description: "Read persisted investigation identities, correlation IDs, progress and outcomes for this ticket. This snapshot provides the worklog evidence also shown by the UI stream."),
        new(Tool, "chat_capabilities", "GET", Ticket + "/chat-capabilities", QueryType: typeof(OperatorAiTicketQuery), Authority: TicketAuthority,
            Description: "Read native chat availability and its actual disabled reason. SQLite or deployment-disabled native chat is an unavailable capability, not a successful chat campaign."),
        new(Tool, "chat_open", "GET", Chat, QueryType: typeof(OperatorAiTicketQuery), Authority: ChatAuthority, MutatingRead: true,
            Description: "Open the current native conversation, creating the first one when absent. This canonical GET can persist a conversation, so the MCP operation requires explicit confirmation. Read chat_capabilities first."),
        new(Tool, "chat", "GET", Chat, QueryType: typeof(OperatorAiChatQuery), Authority: ChatAuthority,
            Description: "Read at most 200 durable events after cursor from an explicit existing conversation without creating one. Use its returned lastSequence and event sequence values to read remaining pages."),
        new(Tool, "chat_history", "GET", Chat + "/history", QueryType: typeof(OperatorAiChatHistoryQuery), Authority: ChatAuthority,
            Description: "List up to 50 native conversations for this ticket, active first, with skip for older pages. Provider credentials and session paths are excluded."),
        new(Tool, "chat_send", "POST", Chat + "/messages", BodyType: typeof(OperatorAiChatMessage), QueryType: typeof(OperatorAiTicketQuery),
            ExternalIo: true, Authority: ChatAuthority,
            Description: "Submit one native chat message with a stable clientMessageId. Retry only the exact same text and identity; if delivery is uncertain, inspect chat and use chat_reconcile without resending."),
        new(Tool, "chat_respond", "POST", Chat + "/interactions/{callId}", BodyType: typeof(OperatorAiChatResponse), QueryType: typeof(OperatorAiTicketQuery),
            ExternalIo: true, Authority: ChatAuthority,
            Description: "Select one exact offered option for the current interaction. Conversation, call ID, option and current awaiting-approval state remain enforced by the canonical API."),
        new(Tool, "chat_reconcile", "POST", Chat + "/reconcile", BodyType: typeof(ChatNewRequest), QueryType: typeof(OperatorAiTicketQuery),
            RequiredBodyFields: ["conversationId"], ExternalIo: true, Authority: ChatAuthority,
            Description: "Request conservative recovery of an unresolved native turn using the saved provider session. This never resends the operator message."),
        new(Tool, "chat_stop_waiting", "POST", Chat + "/stop-waiting", BodyType: typeof(ChatStopWaitingRequest), QueryType: typeof(OperatorAiTicketQuery),
            RequiredBodyFields: ["conversationId", "expectedMessageId"], ExternalIo: true, Authority: ChatAuthority,
            Description: "Stop waiting for the explicitly identified uncertain turn and retire its local transport owner. This records uncertainty and never claims remote cancellation or resends the message."),
        new(Tool, "chat_abandon", "POST", Chat + "/abandon", BodyType: typeof(OperatorAiChatAbandon), QueryType: typeof(OperatorAiTicketQuery),
            RequiredBodyFields: ["acknowledgePossibleDelivery"], Authority: ChatAuthority,
            Description: "Abandon unresolved delivery using stable resolutionId and expectedMessageId with explicit acknowledgement that the previous remote turn may still execute. The canonical API audits and serializes this resolution."),
        new(Tool, "chat_new", "POST", Chat + "/new", BodyType: typeof(ChatNewRequest), QueryType: typeof(OperatorAiTicketQuery),
            RequiredBodyFields: ["conversationId"], Authority: ChatAuthority,
            Description: "Archive the selected idle conversation and open a blank one. Active or unresolved turns must first complete or use the explicit recovery/abandon lifecycle.")
    ];
}

internal sealed record OperatorAiOrganizationQuery([property: Required, MaxLength(128)] string OrganizationId);
internal sealed record OperatorAiTicketQuery([property: Required, AllowedValues("incidents", "requests", "changes")] string TicketType);
internal sealed record OperatorAiChatQuery(
    [property: Required, AllowedValues("incidents", "requests", "changes")] string TicketType,
    [property: Required] Guid ConversationId,
    [property: Range(typeof(long), "0", "9223372036854775807")] long? Cursor = null);
internal sealed record OperatorAiChatHistoryQuery(
    [property: Required, AllowedValues("incidents", "requests", "changes")] string TicketType,
    [property: Range(0, int.MaxValue)] int? Skip = null);
internal sealed record OperatorAiWebhookConfiguration(
    [property: Required, MaxLength(128)] string OrganizationId,
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MinLength(1), MaxLength(3)] IReadOnlyList<AiAssistantTicketArea> TicketAreas,
    [property: Required, MaxLength(2048)] string Endpoint,
    [property: MaxLength(200)] string? RouteName = null,
    [property: MaxLength(65536)] string? PromptTemplate = null,
    [property: MaxLength(65536)] string? PermittedInputsSchemaJson = null,
    [property: Range(1, 120)] int TimeoutSeconds = 30,
    [property: Range(1024, 262144)] int MaxBodyBytes = 65536,
    [property: DataType(DataType.Password), MaxLength(4096)] string? SigningSecret = null,
    bool IsEnabled = true);
internal sealed record OperatorAiWebhookState([property: Required, MaxLength(128)] string OrganizationId, bool Enabled, bool Archived);
internal sealed record OperatorAiDispatch(
    [property: Required] Guid ConfigurationId,
    [property: Required, MaxLength(16000)] string OperatorAssistanceRequest,
    [property: Required, MaxLength(128)] string IdempotencyKey);
internal sealed record OperatorAiChatMessage(
    [property: Required] Guid ConversationId,
    [property: Required] Guid ClientMessageId,
    [property: Required, MaxLength(16000)] string Text);
internal sealed record OperatorAiChatResponse([property: Required] Guid ConversationId, [property: Required, MaxLength(1024)] string SelectedKey);
internal sealed record OperatorAiChatAbandon(
    [property: Required] Guid ConversationId,
    [property: Required] Guid ResolutionId,
    [property: Required] Guid ExpectedMessageId,
    [property: AllowedValues(true)] bool AcknowledgePossibleDelivery);
