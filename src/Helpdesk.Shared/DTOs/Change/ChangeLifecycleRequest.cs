using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Change;

public sealed record ChangeLifecycleRequest(
    ChangeLifecycleState LifecycleState,
    string? Comment = null,
    ChangeLifecycleState? ExpectedLifecycleState = null,
    DateTime? ExpectedUpdatedAt = null)
{
    public const int MaxCommentLength = 1000;
}

public sealed record ChangeCancellationSelection(string Id, ChangeLifecycleState? ExpectedLifecycleState, DateTime? ExpectedUpdatedAt = null);

public sealed record BulkChangeCancellationRequest(IReadOnlyList<ChangeCancellationSelection> Selection, string? Comment = null)
{
    public const int MaxSelectionSize = 100;
}

public sealed record BulkChangeCancellationResult(string Id, string Outcome);
