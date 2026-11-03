using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Helpdesk.Mcp.Prompts;

[McpServerPromptType]
public sealed class HelpdeskPrompts
{
    [McpServerPrompt(Name = "triage_incident")]
    public static string TriageIncident([Description("Incident identifier")] string incidentId) => $"Inspect incident '{incidentId}' using helpdesk_incidents get, timeline, and bounded diagnostics where relevant. Summarize evidence and next actions. If closure is requested, first explain that helpdesk_incidents operation=close requires request.incidentId, request.closureNote, and explicit confirm=true; use an internal note unless customer-visible communication is specifically requested.";

    [McpServerPrompt(Name = "diagnose_request_task")]
    public static string DiagnoseRequestTask([Description("Request identifier")] string requestId) => $"Inspect request '{requestId}' with helpdesk_requests get and tasks. Review task status and evidence, then propose a controlled next action. To start, retry, complete, fail, assign, or otherwise mutate a request task, use helpdesk_request_tasks with the closed operation, typed request.taskId or request.ids, and a separate confirm=true tool call.";

    [McpServerPrompt(Name = "review_change")]
    public static string ReviewChange([Description("Change identifier")] string changeId) => $"Review change '{changeId}' through helpdesk_changes get, timeline, worklogs, and ai_review. State risks and evidence. If a lifecycle, AI-review, worklog, assignment, state, create, update, or delete action is requested, present the exact typed request and call helpdesk_changes again with confirm=true.";

    [McpServerPrompt(Name = "controlled_operator_action")]
    public static string ControlledOperatorAction() => "Inspect the target first, identify affected records and external effects, then present the exact closed operation and typed request. Perform a mutation only through a separate explicit tool call with confirm=true; prompts never bypass confirmation. Do not use helpdesk_mutations: it is intentionally not exposed because it cannot perform an action.";
}
