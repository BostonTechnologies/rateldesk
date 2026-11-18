using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Helpdesk.Application.AiAssistant.Chat;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed class OpenAiAgentsClient(HttpClient http, OpenAiAgentsConfiguration configuration) : IChatProviderSession
{
    internal const string ClientName = "OpenAiAgents";
    private const string Sessions = "agents/sessions";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxPages = 250;
    private static readonly OpenAiAgentsJson Json = OpenAiAgentsJson.Default;

    public ChatProviderFeatures Features => ChatProviderFeatures.SavedHistory | ChatProviderFeatures.RemoteCancellation |
        ChatProviderFeatures.DelegatedWork | ChatProviderFeatures.Usage |
        (configuration.Environment.Type == "openai_hosted" ? ChatProviderFeatures.Artifacts : ChatProviderFeatures.None);

    internal async Task<string> CheckReadAccessAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, Sessions + "?limit=1");
        using var response = await SendAsync(request, ct);
        return "OpenAI project authentication and Agents read permission verified.";
    }

    internal Task<List<AgentArtifact>> ListArtifactsAsync(string session, CancellationToken ct) =>
        GetPagesAsync(SessionPath(session) + "/artifacts", Json.AgentPageAgentArtifact, ct);

    internal async Task<byte[]> ReadArtifactAsync(string session, AgentArtifact artifact, int maxBytes, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, SessionPath(session) + "/artifacts/" + Segment(artifact.Id) + "/content");
        using var response = await SendAsync(request, ct);
        using var body = await ReadBoundedAsync(response.Content, maxBytes, ct);
        if (artifact.SizeBytes != body.Length)
            throw new InvalidDataException("The provider returned an artifact with an unexpected length.");
        return body.ToArray();
    }

    public async Task<string> CreateAsync(Guid conversationId, string firstMessage, CancellationToken ct)
    {
        var agent = new AgentConfiguration(configuration.Model, configuration.Instructions,
            new(true, configuration.SubagentLimit), configuration.Tools);
        var input = new AgentCreateRequest(agent, configuration.Environment, firstMessage, new()
        {
            ["rateldesk_conversation"] = conversationId.ToString("D")
        });
        using var request = Request(HttpMethod.Post, Sessions);
        request.Content = JsonContent.Create(input, Json.AgentCreateRequest);
        var session = await GetJsonAsync(request, Json.AgentSession, ct);
        return ValidId(session.Id);
    }

    public async Task<IChatProviderObservation> ObserveAsync(string sessionId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, SessionPath(sessionId) + "/events?stream=true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await SendAsync(request, ct);
        try
        {
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new InvalidDataException("OpenAI did not return a session event stream.");
            return new OpenAiObservation(response, await response.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public Task SubmitAsync(string sessionId, string requestId, string message, CancellationToken ct) =>
        PostEventAsync(sessionId, requestId, new("agent.session.input.message", [new([new("input_text", message)])]), ct);

    public Task CancelAsync(string sessionId, string requestId, CancellationToken ct) =>
        PostEventAsync(sessionId, requestId, new("agent.session.input.cancel"), ct);

    public Task RespondAsync(string sessionId, string requestId, ProviderPendingAction action, ProviderDecision decision, CancellationToken ct)
    {
        var input = action.Kind switch
        {
            ProviderActionKind.McpApproval => new AgentInputEvent("agent.session.input.mcp_approval_request_result",
                RequestId: action.Id, Response: new(decision.Approved ? "approve" : "deny")),
            ProviderActionKind.Function => new AgentInputEvent("agent.session.input.tool_result",
                TurnId: action.TurnId, CallId: action.Id, Success: decision.Approved,
                Output: decision.Approved ? decision.Result : null, Error: decision.Approved ? null : decision.Result),
            _ => throw new ArgumentException("This interaction cannot be answered by RatelDesk.", nameof(action))
        };
        return PostEventAsync(sessionId, requestId, input, ct);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, SessionPath(sessionId));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) EnsureSuccess(response);
    }

    public async Task<ProviderSessionHistory> RetrieveAsync(string sessionId, CancellationToken ct)
    {
        var path = SessionPath(sessionId);
        var session = await GetAsync(path, Json.AgentSession, ct);
        if (!string.Equals(sessionId, session.Id, StringComparison.Ordinal))
            throw new InvalidDataException("OpenAI returned a different session identity.");
        var items = new List<ProviderItem>();
        var turns = new List<ProviderTurn>();
        await ReadAgentAsync(path, null, items, turns, ct);

        var children = await GetPagesAsync(path + "/subagents", Json.AgentPageAgentSubagent, ct);
        foreach (var child in children)
            await ReadAgentAsync(path + "/subagents/" + Segment(child.Id), child.Id, items, turns, ct);

        var pending = (session.RequiredActions ?? []).Select(MapAction).ToArray();
        return new(sessionId, session.Status, items, turns,
            children.Select(child => new ProviderSubagent(child.Id, child.Name, child.Status, Date(child.OpenedAt))).ToArray(), pending);
    }

    private async Task ReadAgentAsync(string path, string? childId, List<ProviderItem> items, List<ProviderTurn> turns, CancellationToken ct)
    {
        var savedItems = await GetPagesAsync(path + "/items", Json.AgentPageAgentItem, ct);
        foreach (var item in savedItems)
            if (MapItem(item, childId) is { } normalized) items.Add(normalized);
        var savedTurns = await GetPagesAsync(path + "/turns", Json.AgentPageAgentTurn, ct);
        turns.AddRange(savedTurns.Select(turn => new ProviderTurn(ValidId(turn.Id), childId ?? turn.SubagentId,
            turn.Status, Date(turn.CreatedAt), Date(turn.CompletedAt), turn.Usage is { } usage
                ? new(usage.InputTokens, usage.OutputTokens, usage.TotalTokens) : null, turn.Error?.Code)));
    }

    private static ProviderPendingAction MapAction(AgentAction action) => action.Type switch
    {
        "mcp_approval_request" => new(ProviderActionKind.McpApproval, ValidId(action.RequestId),
            ValidId(action.TurnId), action.Request?.Name, action.Request?.Arguments ?? default, action.Request?.ServerLabel),
        "function_call" => new(ProviderActionKind.Function, ValidId(action.CallId),
            ValidId(action.TurnId), action.Name, action.Arguments, null),
        _ => new(ProviderActionKind.Unsupported, action.RequestId ?? "", action.TurnId ?? "", null, default, null)
    };

    private static ProviderItem? MapItem(AgentItem item, string? childId)
    {
        if (item.Type == "message" && item.Role is "assistant" or "user")
        {
            var kind = item.Role == "user" ? ProviderItemKind.UserMessage : ProviderItemKind.AssistantMessage;
            return new(item.Id, item.TurnId, childId, kind, item.Status,
                string.Join("\n", (item.Content ?? []).Where(part => part.Type is "input_text" or "output_text").Select(part => part.Text)));
        }
        if (item.Type is "mcp_call" or "function_call" or "command_execution" or "create_subagent_call" or "wait_for_subagents_call" or "send_subagent_input_call" or "resume_subagent_call")
            return new(item.Id, item.TurnId, childId, ProviderItemKind.ToolCall, item.Status,
                item.Output.ValueKind == JsonValueKind.String ? item.Output.GetString() ?? "" : "", item.Name ?? item.Type,
                item.CallId, ReadOperation(item.Arguments), item.Type switch
                {
                    "mcp_call" => "mcp",
                    "function_call" => "function",
                    "command_execution" => "command",
                    _ => "subagent"
                });
        return null;
    }

    private static string? ReadOperation(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("operation", out var operation) &&
            operation.ValueKind == JsonValueKind.String)
            return operation.GetString();
        return null;
    }

    private async Task PostEventAsync(string sessionId, string requestId, AgentInputEvent input, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, SessionPath(sessionId) + "/events");
        request.Headers.Add("Idempotency-Key", requestId);
        request.Content = JsonContent.Create(new AgentInputEvents([input]), Json.AgentInputEvents);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw new InvalidDataException("OpenAI did not confirm event admission.");
    }

    private async Task<List<T>> GetPagesAsync<T>(string path, JsonTypeInfo<AgentPage<T>> type, CancellationToken ct)
    {
        var result = new List<T>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var target = path + "?order=asc&limit=100";
        for (var page = 0; page < MaxPages; page++)
        {
            var response = await GetAsync(target, type, ct);
            result.AddRange(response.Data);
            if (!response.HasMore) return result;
            var cursor = ValidId(response.LastId);
            if (response.Data.Length == 0 || !cursors.Add(cursor))
                throw new InvalidDataException("OpenAI returned an invalid saved-history cursor.");
            target = path + "?order=asc&limit=100&after=" + Segment(cursor);
        }
        throw new InvalidDataException("OpenAI saved history exceeded the bounded reconciliation limit.");
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path);
        return await GetJsonAsync(request, type, ct);
    }

    private async Task<T> GetJsonAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await SendAsync(request, ct);
        using var body = await ReadBoundedAsync(response.Content, MaxResponseBytes, ct);
        return await JsonSerializer.DeserializeAsync(body, type, ct)
            ?? throw new InvalidDataException("OpenAI returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;
        using (response)
            throw new OpenAiRequestException(response.StatusCode, response.Headers.RetryAfter?.Delta);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new OpenAiRequestException(response.StatusCode, response.Headers.RetryAfter?.Delta);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri("https://api.openai.com/v1/" + path));
        request.Headers.Authorization = new("Bearer", configuration.ApiKey);
        request.Headers.Add("OpenAI-Beta", "agents=v1");
        if (configuration.Project is { } project) request.Headers.Add("OpenAI-Project", project);
        if (configuration.Organization is { } organization) request.Headers.Add("OpenAI-Organization", organization);
        return request;
    }

    internal static async Task<MemoryStream> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum)
            throw new InvalidDataException("OpenAI response exceeded the allowed size.");
        var output = new MemoryStream();
        try
        {
            await using var source = await content.ReadAsStreamAsync(ct);
            var buffer = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + read > maximum)
                    throw new InvalidDataException("OpenAI response exceeded the allowed size.");
                output.Write(buffer, 0, read);
            }
            output.Position = 0;
            return output;
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    private static string SessionPath(string id) => Sessions + "/" + Segment(id);
    private static string Segment(string? id) => Uri.EscapeDataString(ValidId(id));

    private static string ValidId(string? id) => id is { Length: > 0 and <= 512 } && id is not ("." or "..")
        ? id
        : throw new InvalidDataException("OpenAI returned a missing or invalid resource identity.");

    private static DateTimeOffset? Date(long? seconds) => seconds is >= 0 and <= 253402300799
        ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
}

internal sealed class OpenAiAgentsConfiguration(string apiKey, string model, string? instructions, string? project,
    string? organization, AgentEnvironment environment, object[] tools, int subagentLimit)
{
    public string ApiKey { get; } = apiKey;
    public string Model { get; } = model;
    public string? Instructions { get; } = instructions;
    public string? Project { get; } = project;
    public string? Organization { get; } = organization;
    public AgentEnvironment Environment { get; } = environment;
    public object[] Tools { get; } = tools;
    public int SubagentLimit { get; } = subagentLimit;
}

internal sealed class OpenAiRequestException(HttpStatusCode status, TimeSpan? retryAfter)
    : HttpRequestException(MessageFor(status), null, status)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;

    private static string MessageFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "OpenAI rejected the project key.",
        HttpStatusCode.Forbidden => "The OpenAI project lacks permission for this request.",
        HttpStatusCode.BadRequest => "OpenAI rejected the request configuration or model.",
        HttpStatusCode.NotFound => "The OpenAI session or requested resource is unavailable.",
        HttpStatusCode.TooManyRequests => "OpenAI reported a rate or quota limit.",
        _ => $"OpenAI returned HTTP {(int)status}."
    };
}

internal sealed class OpenAiObservation(HttpResponseMessage response, Stream stream) : IChatProviderObservation
{
    private const int MaxFrameCharacters = 1024 * 1024;

    public async IAsyncEnumerable<ProviderStreamEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var line = new StringBuilder();
        var frame = new StringBuilder();
        var buffer = new char[4096];
        var skipLineFeed = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (skipLineFeed && character == '\n')
                {
                    skipLineFeed = false;
                    continue;
                }
                skipLineFeed = character == '\r';
                if (character is not ('\r' or '\n'))
                {
                    if (line.Length + frame.Length >= MaxFrameCharacters)
                        throw new InvalidDataException("OpenAI sent an oversized event.");
                    line.Append(character);
                    continue;
                }
                if (line.Length == 0)
                {
                    if (frame.Length > 0 && Parse(frame.ToString()) is { } message) yield return message;
                    frame.Clear();
                }
                else
                {
                    var field = line.ToString();
                    if (field.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (frame.Length != 0) frame.Append('\n');
                        frame.Append(field.AsSpan(field.Length > 5 && field[5] == ' ' ? 6 : 5));
                    }
                    line.Clear();
                }
            }
        }
    }

    private static ProviderStreamEvent? Parse(string data)
    {
        if (data == "[DONE]") return null;
        var item = JsonSerializer.Deserialize(data, OpenAiAgentsJson.Default.AgentEvent);
        return item?.Type switch
        {
            "agent.session.turn.output_text.delta" => new(ProviderStreamEventKind.TextDelta,
                item.ItemId, item.TurnId, item.SubagentId, item.Delta, item.ContentIndex),
            "agent.session.turn.output_text.done" => new(ProviderStreamEventKind.TextDone,
                item.ItemId, item.TurnId, item.SubagentId, item.Text, item.ContentIndex),
            "agent.session.turn.item.done" or "agent.session.turn.completed" or
            "agent.session.turn.cancelled" or "agent.session.turn.failed" or "agent.session.failed" or
            "agent.session.idle" or "agent.session.requires_action" or "agent.session.environment.expired" =>
                new(ProviderStreamEventKind.HistoryChanged, item.ItemId, item.TurnId, item.SubagentId, null),
            _ => null
        };
    }

    public ValueTask DisposeAsync()
    {
        response.Dispose();
        return ValueTask.CompletedTask;
    }
}
