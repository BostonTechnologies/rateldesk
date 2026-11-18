using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed record AgentCreateRequest(AgentConfiguration Agent, AgentEnvironment Environment,
    string Input, Dictionary<string, string> Metadata);

internal sealed record AgentConfiguration(string Model, string? Instructions, AgentMultiAgent MultiAgent, object[] Tools);
internal sealed record AgentMultiAgent(bool Enabled, int MaxConcurrentSubagents);
internal sealed record AgentEnvironment(string Type, string? ContainerSize = null, AgentNetwork? Network = null,
    string? EnvironmentTemplateId = null, AgentPlugin[]? Plugins = null);
internal sealed record AgentNetwork(string Access);
internal sealed record AgentPlugin(string Type, string Name, string Description, AgentPluginSource Source);
internal sealed record AgentPluginSource(string Type, string MediaType, string Data);

internal sealed record AgentMcpTool(string ServerLabel, AgentMcpTransport Transport, string[] AllowedTools)
{
    public string Type => "mcp";
    public string ConnectionOrigin => "service";
    public bool Required => true;
}

internal sealed record AgentMcpTransport(string ServerUrl, string Authorization)
{
    public string Type => "http";
}

internal sealed record AgentFunction(string Name, string Description, JsonElement Parameters)
{
    public string Type => "function";
}

internal sealed record AgentInputEvents(AgentInputEvent[] Events);
internal sealed record AgentInputEvent(string Type, AgentMessage[]? Input = null, string? RequestId = null,
    AgentApprovalResponse? Response = null, string? TurnId = null, string? CallId = null,
    bool? Success = null, string? Output = null, string? Error = null);

internal sealed record AgentApprovalResponse(string Decision)
{
    public string Type => "mcp_tool_call";
}

internal sealed record AgentMessage(AgentContent[] Content)
{
    public string Type => "message";
    public string Role => "user";
}

internal sealed record AgentContent(string Type, string? Text);
internal sealed record AgentSession(string Id, string Status, AgentAction[]? RequiredActions);
internal sealed record AgentAction(string Type, string? RequestId, string? CallId, string? TurnId,
    string? Name, JsonElement Arguments, AgentMcpRequest? Request);
internal sealed record AgentMcpRequest(string? Name, string? ServerLabel, JsonElement Arguments);
internal sealed record AgentPage<T>(T[] Data, bool HasMore, string? LastId);
internal sealed record AgentItem(string? Id, string? TurnId, string Type, string? Role, string? Status,
    AgentContent[]? Content, string? Name, string? CallId, JsonElement Arguments, JsonElement Output);
internal sealed record AgentTurn(string Id, string? SubagentId, string Status, long? CreatedAt, long? CompletedAt,
    AgentUsage? Usage, AgentError? Error);
internal sealed record AgentUsage(long InputTokens, long OutputTokens, long TotalTokens);
internal sealed record AgentError(string? Code);
internal sealed record AgentSubagent(string Id, string? Name, string Status, long? OpenedAt);
internal sealed record AgentEvent(string Type, string? ItemId, string? TurnId, string? SubagentId, string? Delta,
    string? Text, int ContentIndex);
internal sealed record AgentArtifact(string Id, string Path, string TurnId, long SizeBytes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentCreateRequest))]
[JsonSerializable(typeof(AgentMcpTool))]
[JsonSerializable(typeof(AgentFunction))]
[JsonSerializable(typeof(AgentInputEvents))]
[JsonSerializable(typeof(AgentSession))]
[JsonSerializable(typeof(AgentPage<AgentItem>))]
[JsonSerializable(typeof(AgentPage<AgentTurn>))]
[JsonSerializable(typeof(AgentPage<AgentSubagent>))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(AgentPage<AgentArtifact>))]
internal sealed partial class OpenAiAgentsJson : JsonSerializerContext;
