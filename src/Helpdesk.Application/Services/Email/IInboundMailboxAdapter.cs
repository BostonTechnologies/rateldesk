using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Services.Email;

public sealed record InboundSourceMessage(string Key, InboundEmailContext? Message, bool Ignore = false, string? HoldReason = null);
public sealed record InboundSourceBatch(IReadOnlyList<InboundSourceMessage> Messages, string? NextCursor, bool InitializationComplete);
public sealed record MailboxConnectionTest(bool Success, string Message);
public sealed class InboundSourceMissingException(string message) : Exception(message);

public interface IInboundMailboxAdapter
{
    InboundMailboxProvider Provider { get; }
    Task<MailboxConnectionTest> TestAsync(EmailInboxSettings settings, CancellationToken ct);
    Task<InboundSourceBatch> FetchAsync(EmailInboxSettings settings, MailboxIngestionState state,
        IReadOnlySet<string> knownKeys, CancellationToken ct);
    Task AcknowledgeAsync(EmailInboxSettings settings, string key, CancellationToken ct);
}

public sealed record HistoricalSourcePreview(string Key, string? Sender, string? Subject,
    DateTimeOffset? ReceivedAt, bool Available, string? ErrorCode);

public interface IHistoricalMailboxAdapter
{
    InboundMailboxProvider Provider { get; }
    Task<IReadOnlyList<HistoricalSourcePreview>> PreviewAsync(EmailInboxSettings settings,
        IReadOnlyList<string> keys, CancellationToken ct);
    Task<InboundSourceMessage> FetchHistoricalAsync(EmailInboxSettings settings, string key, CancellationToken ct);
}
