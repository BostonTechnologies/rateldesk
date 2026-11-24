using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Change;

public sealed record ChangeLifecycleOptionsDto(
    string Id,
    ChangeLifecycleState LifecycleState,
    IReadOnlyList<ChangeLifecycleOptionDto> Choices,
    string? Guidance)
{
    public DateTime? UpdatedAt { get; init; }
}

public sealed record ChangeLifecycleOptionDto(ChangeLifecycleState State, string? UnavailableReason)
{
    public bool RequiresComment { get; init; }
    public bool RequiresDetailUpdate { get; init; }
}
