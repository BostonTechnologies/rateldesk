using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Helpdesk.AgentClient;

namespace Helpdesk.Setup;

/// <summary>Bounded actual transport probes. Only initialize and authenticated read-only status are invoked.</summary>
internal static class McpSetupDiagnostics
{
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        client.DefaultRequestHeaders.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var stage = "reachability";
        try
        {
            using var initialize = await HttpCallAsync(client, resource, 1, "initialize", Initialize, deadline.Token);
            checks.Add(new("reachability", "passed", "The configured MCP resource responded."));
            stage = "initialize";
            if (!ValidInitialization(initialize.RootElement))
            { checks.Add(new("initialize", "failed", "The MCP endpoint rejected or returned an invalid initialization result.")); return checks; }
            checks.Add(new("initialize", "passed", "The endpoint returned a compatible MCP initialization result."));
            using (var notification = new HttpRequestMessage(HttpMethod.Post, resource) { Content = JsonContent.Create(new { jsonrpc = "2.0", method = "notifications/initialized" }) })
            using (var response = await client.SendAsync(notification, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
                response.EnsureSuccessStatusCode();
            stage = "authentication";
            using var tool = await HttpCallAsync(client, resource, 2, "tools/call", AuthStatus, deadline.Token);
            var ok = SuccessfulTool(tool.RootElement);
            checks.Add(new("authentication", ok ? "passed" : "failed", ok ? "The current API account endpoint accepted the caller's request-scoped delegated identity." : "The authenticated read-only status operation returned an access error."));
            checks.Add(new("tool-result", ok ? "passed" : "failed", ok ? "helpdesk_auth/status returned a successful upstream read-only result." : "MCP transport responded, but its read-only upstream tool did not succeed."));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { checks.Add(new(stage, "failed", "MCP checks exceeded their 30-second deadline.")); }
        catch (HttpRequestException exception) { checks.Add(new(exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "authentication" : stage, "failed", "Check the trusted URL, TLS, credential grants and configured ingress. The MCP request failed.")); }
        catch (JsonException) { checks.Add(new(stage, "failed", "The endpoint returned an invalid or oversized MCP protocol response.")); }
        return checks;
    }

    private static async Task<JsonDocument> HttpCallAsync(HttpClient client, Uri resource, int id, string method, object parameters, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, resource) { Content = JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = parameters }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new JsonException();
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions) && !client.DefaultRequestHeaders.Contains("Mcp-Session-Id"))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Mcp-Session-Id", sessions.First());
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
            if (!ValidInitialization(initialize.RootElement)) { checks.Add(new("initialize", "failed", "The stdio process returned an invalid initialization result.")); return checks; }
            checks.Add(new("initialize", "passed", "The isolated stdio distribution initialized with JSON-RPC output."));
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), deadline.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = AuthStatus }).AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            stage = "tool-result";
            using var tool = await ReadStdioAsync(reader, 2, deadline.Token);
            var ok = SuccessfulTool(tool.RootElement);
            checks.Add(new("tool-result", ok ? "passed" : "failed", ok ? "helpdesk_auth/status completed an authenticated upstream read without mutation." : "The stdio transport responded, but its authenticated upstream read failed."));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { checks.Add(new(stage, "failed", "Stdio checks exceeded their 30-second deadline.")); }
        catch (Exception exception) when (exception is IOException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { checks.Add(new(stage, "failed", "The explicitly selected stdio distribution did not return valid bounded JSON-RPC. Check its configuration and stderr separately.")); }
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
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var message = JsonDocument.Parse(line);
            if (MatchingResponse(message.RootElement, id)) return message;
            message.Dispose();
        }
        throw new JsonException();
    }
    private static bool MatchingResponse(JsonElement json, int id) => json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty("jsonrpc", out var version) && version.GetString() == "2.0" && json.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == id;
    private static bool ValidInitialization(JsonElement json) => !json.TryGetProperty("error", out _) && json.TryGetProperty("result", out var result) &&
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString());
    private static bool SuccessfulTool(JsonElement json) => !json.TryGetProperty("error", out _) && json.TryGetProperty("result", out var result) &&
        (!result.TryGetProperty("isError", out var isError) || !isError.GetBoolean()) && result.TryGetProperty("structuredContent", out var structured) &&
        structured.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;

    private sealed class BoundedProtocolReader(Stream stream)
    {
        private readonly StreamReader _reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        private int _characters;
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var line = new StringBuilder(); var character = new char[1];
            while (await _reader.ReadAsync(character.AsMemory(), ct) > 0)
            {
                if (++_characters > 65536) throw new JsonException();
                if (character[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append(character[0]);
            }
            return line.Length > 0 ? line.ToString() : null;
        }
    }
}
