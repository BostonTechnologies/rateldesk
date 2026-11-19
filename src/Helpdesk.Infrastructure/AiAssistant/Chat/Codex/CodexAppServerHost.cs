using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.Codex;

public sealed class CodexAppServerOptions
{
    public const string SectionName = "AiProviders:CodexAppServer";
    public bool PrivateDeviceLogin { get; set; }
    public string? ExecutablePath { get; set; }
    public string? StateDirectory { get; set; }
}

public sealed class CodexAppServerHost(IOptions<CodexAppServerOptions> options) : IAsyncDisposable
{
    public const string Version = "0.170.0";
    private const string McpTokenVariable = "RATELDESK_CODEX_MCP_TOKEN";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly string[] DisabledFeatures =
    [
        "shell_tool", "unified_exec", "apply_patch_freeform", "multi_agent", "multi_agent_v2", "collab",
        "code_mode", "code_mode_host", "apps", "plugins", "connectors", "codex_hooks", "plugin_hooks",
        "computer_use", "browser_use", "in_app_browser", "tool_search", "request_permissions_tool",
        "agent_message_board", "agent_message_board_mcp"
    ];
    private static readonly string[] NetworkEnvironment = ["HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR"];
    private readonly ConcurrentDictionary<Guid, Slot> connections = new();
    private readonly SemaphoreSlim start = new(1);
    private FileStream? hostLease;
    private Guid? hostId;

    public bool Configured => OperatingSystem.IsLinux() && options.Value.PrivateDeviceLogin &&
        Path.IsPathFullyQualified(options.Value.ExecutablePath ?? "") &&
        Path.IsPathFullyQualified(options.Value.StateDirectory ?? "");

    public async Task<Guid> CheckAsync(CancellationToken ct)
    {
        if (!Configured) throw new ChatConflictException("Private Codex device sign-in is unavailable on this deployment.");
        await start.WaitAsync(ct);
        try
        {
            if (hostId is { } available) return available;
            using var deadline = BeginDeadline(ct);
            var folder = options.Value.StateDirectory!;
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            CheckPrivateDirectory(folder);
            var lease = new FileStream(Path.Combine(folder, "rateldesk-runtime.lock"), new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            try
            {
                lease.Lock(0, 1);
                await CheckVersionAsync(folder, deadline.Token);
                using var reader = new StreamReader(lease, leaveOpen: true);
                var identity = await reader.ReadLineAsync(deadline.Token);
                if (!Guid.TryParse(identity, out var id))
                {
                    id = Guid.NewGuid();
                    lease.SetLength(0);
                    lease.Position = 0;
                    await lease.WriteAsync(System.Text.Encoding.ASCII.GetBytes(id.ToString("D")), deadline.Token);
                    lease.Flush(flushToDisk: true);
                }
                hostLease = lease;
                hostId = id;
                return id;
            }
            catch
            {
                await lease.DisposeAsync();
                throw;
            }
        }
        finally { start.Release(); }
    }

    internal async Task<RuntimeUse> AcquireAsync(Guid connection, Guid expectedHost, CancellationToken ct)
    {
        if (await CheckAsync(ct) != expectedHost)
            throw new ChatConflictException("This connection belongs to a different Codex runtime host.");
        var slot = connections.GetOrAdd(connection, static _ => new Slot());
        await slot.Gate.WaitAsync(ct);
        return new(this, connection, slot);
    }

    private async Task CheckVersionAsync(string folder, CancellationToken ct)
    {
        var command = Command(folder);
        command.ArgumentList.Add("--version");
        using var process = Process.Start(command) ?? throw new IOException("The Codex executable could not start.");
        try
        {
            var version = await process.StandardOutput.ReadLineAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 || version != "codex-cli " + Version)
                throw new ChatConflictException($"Install Codex CLI {Version} for this runtime.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private ProcessStartInfo Command(string directory)
    {
        var command = new ProcessStartInfo(options.Value.ExecutablePath!)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        command.Environment.Clear();
        command.Environment["PATH"] = "/usr/bin:/bin";
        command.Environment["LANG"] = "C.UTF-8";
        command.Environment["CODEX_HOME"] = directory;
        command.Environment["XDG_CONFIG_HOME"] = directory;
        foreach (var variable in NetworkEnvironment)
            if (Environment.GetEnvironmentVariable(variable) is { } value)
                command.Environment[variable] = value;
        return command;
    }

    private async Task<CodexAppServerProcess> RunAsync(Guid connection, string? resource, string? token, string[] tools, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var directory = Path.Combine(options.Value.StateDirectory!, connection.ToString("N"));
        Directory.CreateDirectory(directory, PrivateDirectory);
        CheckPrivateDirectory(directory);
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace, PrivateDirectory);
        CheckPrivateDirectory(workspace);

        var command = Command(directory);
        command.WorkingDirectory = workspace;
        foreach (var value in new[]
        {
            "cli_auth_credentials_store=\"file\"", "forced_login_method=\"chatgpt\"", "model_provider=\"openai\"",
            "approval_policy=\"never\"", "sandbox_mode=\"read-only\"", "web_search=\"disabled\"",
            "allow_login_shell=false", "project_doc_max_bytes=0"
        })
            AddSetting(command, value);
        foreach (var feature in DisabledFeatures) AddSetting(command, $"features.{feature}=false");
        if (resource is not null && token is not null)
        {
            command.Environment[McpTokenVariable] = token;
            AddSetting(command, $"mcp_servers.rateldesk.url={JsonSerializer.Serialize(resource)}");
            AddSetting(command, $"mcp_servers.rateldesk.bearer_token_env_var=\"{McpTokenVariable}\"");
            AddSetting(command, $"mcp_servers.rateldesk.enabled_tools={JsonSerializer.Serialize(tools)}");
            AddSetting(command, "mcp_servers.rateldesk.required=true");
        }
        command.ArgumentList.Add("app-server");
        command.ArgumentList.Add("--listen");
        command.ArgumentList.Add("stdio://");
        return await CodexAppServerProcess.StartAsync(command, ct);
    }

    internal void RemoveLocalCredential(Guid connection)
    {
        var folder = Path.Combine(options.Value.StateDirectory!, connection.ToString("N"));
        if (!Directory.Exists(folder)) return;
        CheckPrivateDirectory(folder);
        File.Delete(Path.Combine(folder, "auth.json"));
    }

    private static void AddSetting(ProcessStartInfo command, string setting)
    {
        command.ArgumentList.Add("-c");
        command.ArgumentList.Add(setting);
    }

    private static void CheckPrivateDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var info = new DirectoryInfo(directory);
        if (!info.Exists || info.LinkTarget is not null || File.GetUnixFileMode(directory) != PrivateDirectory)
            throw new ChatConflictException("Mount a private, writable Codex state directory with permissions 0700.");
    }

    internal static CancellationTokenSource BeginDeadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in connections.Values)
        {
            if (item.Server is { } server) await server.DisposeAsync();
            item.Gate.Dispose();
        }
        if (hostLease is not null) await hostLease.DisposeAsync();
        start.Dispose();
    }

    internal sealed class Slot
    {
        internal SemaphoreSlim Gate { get; } = new(1);
        internal CodexAppServerProcess? Server { get; set; }
        internal Guid? Conversation { get; set; }
        internal CodexDeviceChallenge? Challenge { get; set; }
    }

    internal sealed class RuntimeUse(CodexAppServerHost host, Guid connection, Slot slot) : IAsyncDisposable
    {
        internal CodexDeviceChallenge? Challenge { get => slot.Challenge; set => slot.Challenge = value; }

        internal async Task<CodexAppServerProcess> OpenAsync(CancellationToken ct, Guid? conversation = null,
            string? resource = null, string? token = null, string[]? tools = null)
        {
            if (slot.Server is { IsRunning: true } server && (conversation is null || conversation == slot.Conversation))
                return server;
            await CloseAsync();
            slot.Server = await host.RunAsync(connection, resource, token, tools ?? [], ct);
            slot.Conversation = conversation;
            return slot.Server;
        }

        internal async Task CloseAsync()
        {
            if (slot.Server is not { } server) return;
            slot.Server = null;
            slot.Conversation = null;
            slot.Challenge = null;
            await server.DisposeAsync();
        }

        public ValueTask DisposeAsync()
        {
            slot.Gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class CodexDeviceChallenge(string loginId, string url, string code)
{
    internal string LoginId { get; } = loginId;
    internal string Url { get; } = url;
    internal string Code { get; } = code;
}
