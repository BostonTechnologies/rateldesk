using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Change;

public sealed record ChangeLifecycleOptionsDto(
    string Id,
    ChangeLifecycleState LifecycleState,
    IReadOnlyList<ChangeLifecycleOptionDto> Choices,
    string? Guidance);

public sealed record ChangeLifecycleOptionDto(ChangeLifecycleState State, string? UnavailableReason);
