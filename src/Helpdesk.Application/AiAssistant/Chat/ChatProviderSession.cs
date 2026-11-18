using System.Text.Json;

namespace Helpdesk.Application.AiAssistant.Chat;

[Flags]
public enum ChatProviderFeatures
{
    None = 0,
    SavedHistory = 1,
    RemoteCancellation = 2,
    DelegatedWork = 4,
    Usage = 8,
    Artifacts = 16
}

public interface IChatProviderSession
{
    ChatProviderFeatures Features { get; }
    Task<string> CreateAsync(Guid conversationId, string firstMessage, CancellationToken ct);
    Task<IChatProviderObservation> ObserveAsync(string sessionId, CancellationToken ct);
    Task<ProviderSessionHistory> RetrieveAsync(string sessionId, CancellationToken ct);
    Task SubmitAsync(string sessionId, string requestId, string message, CancellationToken ct);
    Task RespondAsync(string sessionId, string requestId, ProviderPendingAction action, ProviderDecision decision, CancellationToken ct);
    Task CancelAsync(string sessionId, string requestId, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
}

public interface IChatProviderObservation : IAsyncDisposable
{
    IAsyncEnumerable<ProviderStreamEvent> ReadAsync(CancellationToken ct);
}

public enum ProviderStreamEventKind { TextDelta, TextDone, HistoryChanged }

public sealed record ProviderStreamEvent(ProviderStreamEventKind Kind, string? ItemId,
    string? TurnId, string? SubagentId, string? Text, int ContentIndex = 0);

public sealed record ProviderSessionHistory(string Id, string Status, IReadOnlyList<ProviderItem> Items,
    IReadOnlyList<ProviderTurn> Turns, IReadOnlyList<ProviderSubagent> Subagents, IReadOnlyList<ProviderPendingAction> PendingActions);

public enum ProviderItemKind { UserMessage, AssistantMessage, ToolCall }

public sealed record ProviderItem(string? Id, string? TurnId, string? SubagentId, ProviderItemKind Kind,
    string? Status, string Text, string? ToolName = null, string? CallId = null, string? Operation = null, string? ToolKind = null);

public sealed record ProviderTurn(string Id, string? SubagentId, string Status, DateTimeOffset? CreatedAt,
    DateTimeOffset? CompletedAt, ProviderUsage? Usage, string? FailureCode);

public sealed record ProviderUsage(long InputTokens, long OutputTokens, long TotalTokens);
public sealed record ProviderSubagent(string Id, string? Name, string Status, DateTimeOffset? OpenedAt);
public enum ProviderActionKind { McpApproval, Function, Unsupported }
public sealed record ProviderPendingAction(ProviderActionKind Kind, string Id, string TurnId,
    string? Name, JsonElement Arguments, string? ServerLabel);
public sealed record ProviderDecision(bool Approved, string? Result = null);
