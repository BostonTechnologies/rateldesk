namespace Helpdesk.Mcp.Tools;

// These contracts define the stable MCP request shape for common single-record operations.
public sealed record IncidentRequest(string IncidentId);
public sealed record RequestRequest(string RequestId);
public sealed record ChangeRequest(string ChangeId);
public sealed record RequestTaskRequest(string TaskId);
public sealed record NotificationRequest(string NotificationId);
public sealed record SearchRequest(string Query, int? Limit = null);
public sealed record ListRequest(int? Page = null, int? PageSize = null, string? Query = null, string? Q = null);
public sealed record LogSearchRequest(long? Since, string? CorrelationId, string? Contains, int? Limit = null, string? Level = null);
public sealed record TicketMutationRequest(string? IncidentId = null, string? RequestId = null, string? ChangeId = null, IReadOnlyList<string>? Ids = null);
public sealed record RequestTaskMutationRequest(string? TaskId = null, IReadOnlyList<string>? Ids = null, string? AssignedToId = null);
public sealed record TicketSeenRequest(string TicketId);
public sealed record NotificationMarkReadRequest(IReadOnlyList<string> Ids);
public sealed record RequestFormRequest(string RequestFormId);
public sealed record RequestFormsByServiceRequest(string ServiceId);
public sealed record TicketCountRequest(string TicketType, string TicketId);
public sealed record IncidentCountRequest(string IncidentId);
public sealed record RequestCountRequest(string RequestId);
public sealed record ChangeCountRequest(string ChangeId);
public sealed record TicketStateRequest(string? IncidentId = null, string? RequestId = null, string? ChangeId = null, string? NewState = null);
public sealed record TicketWorklogRequest(string? IncidentId = null, string? RequestId = null, string? ChangeId = null, decimal? Hours = null, string? Notes = null, bool? IsInternalNote = null);
public sealed record IncidentWorklogRequest(string IncidentId, decimal Hours, string Notes, bool IsInternalNote = true);
public sealed record IncidentCloseRequest(string IncidentId, string ClosureNote, bool IsInternalNote = true);
public sealed record ConfigKeyRequest(string Key);
public sealed record ConfigValueRequest(string Key, string Value);
public sealed record AiAssistantWorklogRequest(Guid InvocationId, string EventId, string TicketId, string TicketType, string CorrelationId, string Status, string Message, string? Severity = null, string? MetadataJson = null, string? ArtifactReferencesJson = null, string? RunReference = null);
