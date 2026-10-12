namespace Helpdesk.Shared.DTOs.User;

public sealed record AssigneeSearchResult(List<UserDto> Items, bool HasMore);
