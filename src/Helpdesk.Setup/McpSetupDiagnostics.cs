using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;

namespace Helpdesk.Setup;

/// <summary>Bounded actual transport probes. Only initialize and authenticated read-only status are invoked.</summary>
internal static class McpSetupDiagnostics
{
    // The real RatelDesk catalog contains detailed discriminated request schemas. A 64KiB
    // ceiling truncated valid catalogs. Bound each response to 1MiB and all pages to 4MiB.
    internal const int MaximumResponseCharacters = 1024 * 1024;
    private static readonly HashSet<string> CompatibleVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly IReadOnlyDictionary<string, JsonElement> ExpectedTools = HelpdeskToolDefinitions.Create()
        .ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema, StringComparer.Ordinal);
    private static readonly object Initialize = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "rateldesk-setup", version = "1" } };
    private static readonly object AuthStatus = new { name = "helpdesk_auth", arguments = new { operation = "status", confirm = false } };

    public static async Task<IReadOnlyList<SetupCheck>> CheckHttpAsync(AgentClientConfiguration configuration, CancellationToken ct, HttpClient? testClient = null)
    {
        var checks = new List<SetupCheck>();
        if (configuration.HttpClientAuthentication == "oauth")
        {
            using var discovery = new OAuthProtectedResourceClient(configuration.ApiBaseUrl!, testClient);
            await discovery.DiscoverAsync(configuration.OAuthAuthorizationServers, ct);
            return [new("configuration", "passed", "Non-secret OAuth client resource and authority metadata are configured."),
                new("reachability", "passed", "The exact canonical protected-resource metadata responded and matched the reviewed authorities."),
                new("authentication", "unverified", "The external MCP client owns OAuth login, grants and token storage."),
                new("initialize", "unverified", "External-client initialization requires that client's configured OAuth flow."),
                new("catalog", "unverified", "Read the complete catalog in the configured external MCP client."),
                new("tool-result", "unverified", "Run a permitted read-only status operation in the configured external MCP client.")];
        }
        if (configuration.ClientTransport != "http" || configuration.CredentialPurpose != "mcp" ||
            !Uri.TryCreate(configuration.ApiBaseUrl, UriKind.Absolute, out var origin) || !Uri.TryCreate(configuration.McpResourceUri, UriKind.Absolute, out var resource) ||
            resource.Scheme != "https" || resource.AbsolutePath != "/mcp" || !string.IsNullOrEmpty(resource.UserInfo) || !string.IsNullOrEmpty(resource.Query) || !string.IsNullOrEmpty(resource.Fragment) ||
            !AgentClientConfigurationValidator.SameTarget(origin, new Uri(resource.GetLeftPart(UriPartial.Authority))) ||
            (configuration.CredentialTarget is { } target && !AgentClientConfigurationValidator.SameTarget(origin, new Uri(target))))
            return [new("configuration", "failed", "HTTP clients require an MCP-purpose credential bound to this reviewed HTTPS /mcp resource.")];
        var secret = configuration.IntegrationCredential ?? (configuration.IntegrationCredentialFile is { } path
            ? SecureConfigurationFiles.ReadText(path, 16384, requirePrivate: true).TrimEnd('\r', '\n') : null);
        if (string.IsNullOrWhiteSpace(secret)) return [new("configuration", "failed", "An MCP-purpose client credential is required.")];
        secret = AgentClientConfigurationValidator.IntegrationSecret(secret);
        checks.Add(new("configuration", "passed", "MCP client resource and private credential reference are configured."));
        using var ownedClient = testClient is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan } : null;
        var client = testClient ?? ownedClient!;
        var session = new HttpProbeSession(client, resource, secret);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var stage = "initialize";
        try
        {
            using var initialize = await session.CallAsync(1, "initialize", Initialize, deadline.Token);
            checks.Add(new("reachability", "passed", "The configured MCP resource responded."));
            stage = "initialize";
            if (!ValidInitialization(initialize.RootElement))
            { checks.Add(Failed("initialize", "initialize_invalid", "protocol", "The MCP endpoint rejected or returned an incompatible initialization result.", "Check the MCP URL and supported protocol version.")); return checks; }
            session.ProtocolVersion = initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString()!;
            checks.Add(new("initialize", "passed", "The endpoint returned a compatible MCP initialization result."));
            await session.NotifyAsync(deadline.Token);
            stage = "catalog";
            await CheckCatalogAsync((id, parameters) => session.CallAsync(id, "tools/list", parameters, deadline.Token));
            checks.Add(new("catalog", "passed", $"The complete expected RatelDesk catalog ({ExpectedTools.Count} tools) and request schemas were returned."));
            stage = "authentication";
            using var tool = await session.CallAsync(100, "tools/call", AuthStatus, deadline.Token);
            var ok = SuccessfulTool(tool.RootElement);
            checks.Add(new("authentication", ok ? "passed" : "failed", ok ? "The current API account endpoint accepted the caller's request-scoped delegated identity." : "The authenticated read-only status operation returned an access error."));
            checks.Add(DescribeTool(tool.RootElement, ok));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { checks.Add(Failed(stage, "mcp_deadline", "transport", "MCP checks exceeded their 30-second deadline.", "Check endpoint reachability and retry verification.")); }
        catch (HttpRequestException exception) { checks.Add(new(exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "authentication" : checks.Any(check => check.Name == "reachability") ? stage : "reachability", "failed", "The MCP request failed.") { Code = "mcp_http_failed", Layer = "transport", UpstreamStatus = (int?)exception.StatusCode, NextAction = "Check the trusted URL, TLS, credential grants and configured ingress." }); }
        catch (JsonException)
        {
            if (!checks.Any(check => check.Name == "reachability") && session.ResponseReceived)
                checks.Add(new("reachability", "passed", "The configured MCP resource responded; its protocol response was invalid."));
            checks.Add(Failed(stage, "mcp_response_invalid", "protocol", "The endpoint returned an invalid, incomplete or oversized MCP protocol response.", "Check the selected MCP server version and gateway diagnostics."));
        }
        return checks;
    }

    // Authentication and session headers belong to one probe, never HttpClient defaults shared by callers.
    private sealed class HttpProbeSession(HttpClient client, Uri resource, string secret)
    {
        private string? sessionId;
        public string ProtocolVersion { get; set; } = "2025-11-25";
        public bool ResponseReceived { get; private set; }
        private HttpRequestMessage Request(object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, resource) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
            if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            return request;
        }
        public async Task NotifyAsync(CancellationToken ct)
        {
            using var request = Request(new { jsonrpc = "2.0", method = "notifications/initialized" });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        public async Task<JsonDocument> CallAsync(int id, string method, object parameters, CancellationToken ct)
        {
            using var request = Request(new { jsonrpc = "2.0", id, method, @params = parameters });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            ResponseReceived = true;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumResponseCharacters) throw new JsonException();
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions))
            {
                var values = sessions.ToArray();
                if (values.Length != 1 || values[0].Length is < 1 or > 256 || values[0].Any(char.IsControl)) throw new JsonException();
                if (sessionId is not null && sessionId != values[0]) throw new JsonException();
                sessionId = values[0];
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var reader = new BoundedProtocolReader(stream);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                var data = new StringBuilder();
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length == 0 && data.Length > 0)
                    {
                        var message = JsonDocument.Parse(data.ToString()); data.Clear();
                        if (MatchingResponse(message.RootElement, id)) return message;
                        message.Dispose();
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(line[5..].TrimStart());
                }
                if (data.Length > 0)
                {
                    var message = JsonDocument.Parse(data.ToString());
                    if (MatchingResponse(message.RootElement, id)) return message;
                    message.Dispose();
                }
                throw new JsonException();
            }
            var text = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line) text.AppendLine(line);
            var json = JsonDocument.Parse(text.ToString());
            if (!MatchingResponse(json.RootElement, id)) { json.Dispose(); throw new JsonException(); }
            return json;
        }
    }

    private static async Task CheckCatalogAsync(Func<int, object, Task<JsonDocument>> call)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        long size = 0;
        for (var page = 0; page < 16; page++)
        {
            using var response = await call(2 + page, cursor is null ? new { } : new { cursor });
            size += response.RootElement.GetRawText().Length;
            if (size > 4 * MaximumResponseCharacters || response.RootElement.TryGetProperty("error", out _) ||
                !response.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) throw new JsonException();
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !ExpectedTools.TryGetValue(name.GetString()!, out var schema) || !found.Add(name.GetString()!) ||
                    !tool.TryGetProperty("inputSchema", out var inputSchema) || !JsonElement.DeepEquals(schema, inputSchema)) throw new JsonException();
            }
            if (!result.TryGetProperty("nextCursor", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                if (!found.SetEquals(ExpectedTools.Keys)) throw new JsonException();
                return;
            }
            if (next.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(next.GetString()) || next.GetString()!.Length > 2048 || !cursors.Add(next.GetString()!)) throw new JsonException();
            cursor = next.GetString();
        }
        throw new JsonException();
    }

    public static async Task<IReadOnlyList<SetupCheck>> CheckStdioAsync(ConfigurationResolution resolution, SetupOptions options, string instance, CancellationToken ct)
    {
        var executable = options.StdioExecutable;
        var assembly = options.StdioEntryAssembly;
        if (executable is null && assembly is null)
            return [new("initialize", "skipped", "No stdio executable was selected. Run rateldesk-mcp setup, or pass --stdio-executable to verify this transport."), new("tool-result", "skipped", "No stdio process was launched.")];
        if (resolution.ProfilePath is null) return [new("configuration", "failed", "Stdio transport checks require an explicitly saved isolated profile.")];
        var start = new ProcessStartInfo { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (executable?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true) { assembly = Path.GetFullPath(executable); executable = null; }
        if (executable is not null) start.FileName = Path.GetFullPath(executable);
        else
        {
            start.FileName = Environment.ProcessPath ?? "dotnet";
            // A framework-dependent process must invoke the actual stdio assembly. A self-contained apphost invokes itself.
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(assembly!);
            else if (!Path.GetFileNameWithoutExtension(start.FileName).Equals("rateldesk-mcp", StringComparison.OrdinalIgnoreCase))
                return [new("initialize", "skipped", "Select --stdio-executable when running setup from another distribution."), new("tool-result", "skipped", "No stdio process was launched.")];
        }
        start.Environment["RATELDESK_MCP_CONFIG"] = resolution.ProfilePath;
        start.Environment["RATELDESK_MCP_INSTANCE"] = instance;
        start.Environment["RATELDESK_MCP_EXPECTED_API_BASE_URL"] = resolution.Configuration.ApiBaseUrl;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        var checks = new List<SetupCheck>();
        Task? drain = null;
        var stage = "initialize";
        try
        {
            if (!process.Start()) throw new IOException();
            drain = DrainErrorAsync(process.StandardError, deadline.Token);
            var reader = new BoundedProtocolReader(process.StandardOutput.BaseStream);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = Initialize }).AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            using var initialize = await ReadStdioAsync(reader, 1, deadline.Token);
            if (!ValidInitialization(initialize.RootElement)) { checks.Add(Failed("initialize", "initialize_invalid", "protocol", "The stdio process returned an incompatible initialization result.", "Use the matching RatelDesk stdio distribution.")); return checks; }
            checks.Add(new("initialize", "passed", "The isolated stdio distribution initialized with JSON-RPC output."));
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), deadline.Token);
            stage = "catalog";
            await CheckCatalogAsync(async (id, parameters) =>
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/list", @params = parameters }).AsMemory(), deadline.Token);
                await process.StandardInput.FlushAsync(deadline.Token);
                return await ReadStdioAsync(reader, id, deadline.Token);
            });
            checks.Add(new("catalog", "passed", $"The complete expected RatelDesk catalog ({ExpectedTools.Count} tools) and request schemas were returned."));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 100, method = "tools/call", @params = AuthStatus }).AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            stage = "tool-result";
            using var tool = await ReadStdioAsync(reader, 100, deadline.Token);
            var ok = SuccessfulTool(tool.RootElement);
            checks.Add(new("authentication", ok ? "passed" : "failed", ok ? "The API account accepted the isolated stdio identity." : "The API-backed read-only status check failed."));
            checks.Add(DescribeTool(tool.RootElement, ok));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { checks.Add(Failed(stage, "stdio_deadline", "transport", "Stdio checks exceeded their 30-second deadline.", "Check the selected executable and private API connection, then retry.")); }
        catch (Exception exception) when (exception is IOException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { checks.Add(Failed(stage, "stdio_protocol_failed", "protocol", "The selected stdio distribution did not return valid bounded JSON-RPC.", "Check its configuration and stderr separately.")); }
        finally
        {
            deadline.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (drain is not null) { try { await drain; } catch (OperationCanceledException) { } }
        }
        return checks;
    }

    private static async Task DrainErrorAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1024];
        // Discard operational stderr without echoing server-controlled text or credentials into the TUI.
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { }
    }
    private static async Task<JsonDocument> ReadStdioAsync(BoundedProtocolReader reader, int id, CancellationToken ct)
    {
        reader.ResetLimit();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var message = JsonDocument.Parse(line);
            if (MatchingResponse(message.RootElement, id)) return message;
            message.Dispose();
        }
        throw new JsonException();
    }
    private static bool MatchingResponse(JsonElement json, int id) => json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0" && json.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == id;
    private static bool ValidInitialization(JsonElement json) => !json.TryGetProperty("error", out _) && json.TryGetProperty("result", out var result) &&
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String && CompatibleVersions.Contains(version.GetString()!) &&
        result.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("serverInfo", out var server) && server.ValueKind == JsonValueKind.Object &&
        server.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()) &&
        server.TryGetProperty("version", out var serverVersion) && serverVersion.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(serverVersion.GetString());
    private static bool SuccessfulTool(JsonElement json) => !json.TryGetProperty("error", out _) && json.TryGetProperty("result", out var result) &&
        result.ValueKind == JsonValueKind.Object && (!result.TryGetProperty("isError", out var isError) || isError.ValueKind == JsonValueKind.False) && result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object &&
        structured.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;

    private static SetupCheck Failed(string name, string code, string layer, string message, string action) =>
        new(name, "failed", message) { Code = code, Layer = layer, NextAction = action };

    private static SetupCheck DescribeTool(JsonElement json, bool ok)
    {
        string? code = null, reference = null; int? upstream = null;
        if (json.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("structuredContent", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("correlationId", out var correlation) && correlation.ValueKind == JsonValueKind.String) reference = SafeToken(correlation.GetString());
            if (content.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.Object)
            {
                if (failure.TryGetProperty("code", out var failureCode) && failureCode.ValueKind == JsonValueKind.String) code = SafeToken(failureCode.GetString());
                if (failure.TryGetProperty("upstreamStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var number) && number is >= 100 and <= 599) upstream = number;
            }
        }
        return new("tool-result", ok ? "passed" : "failed", ok ? "helpdesk_auth/status returned a successful API-backed read-only result." : "MCP transport responded, but its authenticated status operation failed.")
        {
            Code = code ?? (ok ? "status_succeeded" : "status_failed"), Layer = upstream is null ? "operation" : "api", UpstreamStatus = upstream,
            CorrelationId = reference, TransportSucceeded = true, OperationSucceeded = ok,
            NextAction = ok ? null : "Review the API account, approved organization and gateway diagnostics using the correlation reference."
        };
    }
    private static string? SafeToken(string? value) => value is { Length: > 0 and <= 128 } && !value.Contains("rdk_", StringComparison.OrdinalIgnoreCase) && !value.Contains("rdx_", StringComparison.OrdinalIgnoreCase) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? value : null;

    private sealed class BoundedProtocolReader(Stream stream)
    {
        private readonly StreamReader _reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        private int _characters;
        public void ResetLimit() => _characters = 0;
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var line = new StringBuilder(); var character = new char[1];
            while (await _reader.ReadAsync(character.AsMemory(), ct) > 0)
            {
                if (++_characters > MaximumResponseCharacters) throw new JsonException();
                if (character[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append(character[0]);
            }
            return line.Length > 0 ? line.ToString() : null;
        }
    }
}
