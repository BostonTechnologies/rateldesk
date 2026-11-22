namespace Helpdesk.Shared.DTOs.Request;

public sealed record CloseWorkSelection(string Id, long StateVersion);
public sealed record CloseRequestWorkDto(string Reason, List<CloseWorkSelection> Selection);
public sealed record CloseWorkResult(string Id, string Outcome, int SettledTasks = 0, int RemainingTasks = 0);
