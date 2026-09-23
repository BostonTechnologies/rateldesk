namespace Helpdesk.Shared.Models;

public sealed record HistoricalMailboxPreviewRequest(long? FromUnixMilliseconds, long? ToUnixMilliseconds,
    int Count = 25, int Skip = 0);

public sealed record HistoricalMailboxPreviewItem(Guid ReceiptId, string? Sender, string? Subject,
    long? ReceivedUnixMilliseconds, bool Available, string? ErrorCode);

public sealed record HistoricalMailboxPreviewResult(IReadOnlyList<HistoricalMailboxPreviewItem> Items,
    bool HasMore, int Skip);

public sealed record HistoricalMailboxImportRequest(IReadOnlyList<Guid> ReceiptIds, bool Confirmed);

public sealed record HistoricalMailboxImportResult(Guid RequestId, int QueuedCount, string Status);
