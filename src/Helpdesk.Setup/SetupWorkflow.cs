using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.AgentClient;

namespace Helpdesk.Setup;

public enum SetupTransport { Cli, Stdio, Http }
public enum SetupStep { Purpose, Profile, Instance, Authentication, Approval, Hosting, Review, Checks, Complete }
public sealed record SetupOptions(SetupTransport Transport = SetupTransport.Cli, string Profile = "default",
    string? ConfigurationPath = null, bool Hosting = false, bool Monochrome = false,
    string? OutputDirectory = null, string? StdioExecutable = null, string? StdioEntryAssembly = null,
    string? WebUrl = null, string? Authentication = null, string? InstanceLabel = null,
    AgentClientConfiguration? SeedConfiguration = null, bool ExplicitProfile = false);
public sealed record SetupProfile(ConfigurationResolution Resolution, AgentClientConfiguration Stored,
    PendingPairingProfile? Pending = null, AgentClientConfiguration? PendingConfiguration = null);
public sealed record SetupCheck(string Name, string Status, string Message)
{
    public string? Code { get; init; }
    public string? Layer { get; init; }
    public int? UpstreamStatus { get; init; }
    public string? CorrelationId { get; init; }
    public string? NextAction { get; init; }
    public bool? TransportSucceeded { get; init; }
    public bool? OperationSucceeded { get; init; }
}

/// <summary>UI boundary; configuration, persistence and protocol rules remain headless.</summary>
public interface ISetupWorkflow : IDisposable
{
    SetupProfile Load(string profile, SetupTransport transport);
    void PreflightStorage(string profile, SetupTransport transport) { }
    Task<PairingCapabilities?> DiscoverAsync(string origin, CancellationToken cancellationToken);
    Task<OAuthProtectedResourceMetadata> DiscoverOAuthAsync(string origin, IEnumerable<string>? expectedAuthorities, CancellationToken cancellationToken);
    Task<PairingSession> StartAsync(string origin, string profile, SetupTransport transport, CancellationToken cancellationToken);
    Task<PairingPollResult> PollAsync(string origin, PairingSession session, CancellationToken cancellationToken);
    Task CancelAsync(string origin, PairingSession session, CancellationToken cancellationToken);
    Task AcknowledgeAsync(string origin, PairingSession session, Guid credentialId, CancellationToken cancellationToken);
    ConfigurationProfileSaveResult Save(string profile, SetupTransport transport, AgentClientConfiguration configuration, string revision, CancellationToken cancellationToken);
    void Rollback(ConfigurationProfileSaveResult saved, SetupProfile previous);
    PendingPairingProfile Stage(string profile, SetupTransport transport, AgentClientConfiguration configuration, string revision,
        string origin, PairingSession session, Guid credentialId, DateTimeOffset deliveryDeadline, CancellationToken cancellationToken);
    PendingPairingProfile MarkAcknowledged(PendingPairingProfile pending);
    ConfigurationProfileSaveResult Activate(PendingPairingProfile pending, CancellationToken cancellationToken);
    void Discard(PendingPairingProfile pending);
    Task<IReadOnlyList<SetupCheck>> CheckAsync(ConfigurationResolution resolution, SetupTransport transport, CancellationToken cancellationToken, string? instance = null);
    Task OpenBrowserAsync(string trustedUrl, CancellationToken cancellationToken);
}

public sealed class SetupWorkflow(SetupOptions options) : ISetupWorkflow
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IntegrationCredentialPairingClient> _clients = new(StringComparer.Ordinal);
    private IntegrationCredentialPairingClient Client(string origin)
    {
        var canonical = IntegrationCredentialPairingClient.ValidateOrigin(origin).AbsoluteUri;
        return _clients.GetOrAdd(canonical, value => new(value));
    }
    private string PathFor(string profile, SetupTransport transport) => new AgentClientConfigurationStore(SelectedPathFor(profile, transport)).Path;
    private string SelectedPathFor(string profile, SetupTransport transport) => options.ConfigurationPath ??
        (!options.ExplicitProfile && transport != SetupTransport.Http
            ? Environment.GetEnvironmentVariable(transport == SetupTransport.Cli ? "RATELDESK_CONFIG" : "RATELDESK_MCP_CONFIG") : null) ??
        (!options.ExplicitProfile && transport == SetupTransport.Cli
            ? AgentClientConfigurationStore.DefaultPath : null) ??
        new ConfigurationProfiles(rootPath: transport == SetupTransport.Http ? SetupInput.HttpClientProfileRoot : null,
            surface: transport == SetupTransport.Cli ? ConfigurationSurface.Cli : ConfigurationSurface.Mcp).PathFor(profile);
    public SetupProfile Load(string profile, SetupTransport transport)
    {
        var path = PathFor(profile, transport);
        var resolution = AgentClientConfigurationResolver.ResolveEffective(
            transport == SetupTransport.Cli ? ConfigurationSurface.Cli : ConfigurationSurface.Mcp,
            path, allowMissingFile: true,
            environment: transport == SetupTransport.Http ? _ => null : null);
        var pending = PendingPairingProfiles.Load(path);
        return new(resolution, File.Exists(path) ? new AgentClientConfigurationStore(path).Load() : AgentClientConfiguration.Empty,
            pending, pending is null ? null : PendingPairingProfiles.ReadCandidate(pending));
    }
    public void PreflightStorage(string profile, SetupTransport transport) => SetupInput.CheckPrivateStorage(PathFor(profile, transport));
    public Task<PairingCapabilities?> DiscoverAsync(string origin, CancellationToken ct) => Client(origin).DiscoverAsync(ct);
    public async Task<OAuthProtectedResourceMetadata> DiscoverOAuthAsync(string origin, IEnumerable<string>? expectedAuthorities, CancellationToken ct)
    {
        using var discovery = new OAuthProtectedResourceClient(origin);
        return await discovery.DiscoverAsync(expectedAuthorities, ct);
    }
    public Task<PairingSession> StartAsync(string origin, string profile, SetupTransport transport, CancellationToken ct) =>
        Client(origin).StartAsync(new(profile, transport == SetupTransport.Http ? "mcp" : "api",
            transport == SetupTransport.Http ? new Uri(IntegrationCredentialPairingClient.ValidateOrigin(origin), "/mcp").AbsoluteUri : null), ct);
    public Task<PairingPollResult> PollAsync(string origin, PairingSession session, CancellationToken ct) => Client(origin).PollAsync(session.DeviceCode, ct);
    public Task CancelAsync(string origin, PairingSession session, CancellationToken ct) => Client(origin).CancelAsync(session.DeviceCode, ct);
    public Task AcknowledgeAsync(string origin, PairingSession session, Guid id, CancellationToken ct) => Client(origin).AcknowledgeAsync(session.DeviceCode, id, ct);
    public ConfigurationProfileSaveResult Save(string profile, SetupTransport transport, AgentClientConfiguration configuration, string revision, CancellationToken ct) =>
        ConfigurationProfiles.SaveFile(PathFor(profile, transport), configuration, revision, ct);
    public void Rollback(ConfigurationProfileSaveResult saved, SetupProfile previous) =>
        ConfigurationProfiles.RestoreFile(saved.Path, previous.Resolution.Revision is null ? null : previous.Stored, saved.Revision);
    public PendingPairingProfile Stage(string profile, SetupTransport transport, AgentClientConfiguration configuration, string revision,
        string origin, PairingSession session, Guid credentialId, DateTimeOffset deliveryDeadline, CancellationToken ct) =>
        PendingPairingProfiles.Stage(PathFor(profile, transport), configuration, revision, origin, session, credentialId, deliveryDeadline, ct);
    public PendingPairingProfile MarkAcknowledged(PendingPairingProfile pending) => PendingPairingProfiles.MarkAcknowledged(pending);
    public ConfigurationProfileSaveResult Activate(PendingPairingProfile pending, CancellationToken ct) => PendingPairingProfiles.Activate(pending, ct);
    public void Discard(PendingPairingProfile pending) => PendingPairingProfiles.Discard(pending);

    public async Task<IReadOnlyList<SetupCheck>> CheckAsync(ConfigurationResolution resolution, SetupTransport transport, CancellationToken ct, string? instance = null)
    {
        if (transport == SetupTransport.Http) return await McpSetupDiagnostics.CheckHttpAsync(resolution.Configuration, ct);
        var report = await ConfigurationDoctor.CheckAsync(resolution, network: true, authenticate: true, ct: ct);
        var checks = report.Checks.Select(check => new SetupCheck(check.Name, check.Ok ? "passed" : "failed", check.Message)).ToList();
        if (transport == SetupTransport.Stdio)
        {
            checks.AddRange(await McpSetupDiagnostics.CheckStdioAsync(resolution, options, instance ?? Environment.GetEnvironmentVariable("RATELDESK_MCP_INSTANCE") ?? "local", ct));
        }
        return checks;
    }

    public async Task OpenBrowserAsync(string trustedUrl, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.Run(() => Process.Start(new ProcessStartInfo(trustedUrl) { UseShellExecute = true }), cancellationToken);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(trustedUrl);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser launch is unavailable.");
        var output = DiscardAsync(process.StandardOutput, deadline.Token);
        var error = DiscardAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new InvalidOperationException("Browser launch is unavailable.");
        }
        finally
        {
            deadline.Cancel();
            if (!process.HasExited) { try { process.Kill(); } catch (InvalidOperationException) { } }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
        }
    }
    private static async Task DiscardAsync(StreamReader reader, CancellationToken ct)
    { var buffer = new char[1024]; while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { } }
    public void Dispose() { foreach (var client in _clients.Values) client.Dispose(); }
}

public static class TerminalText
{
    public static string Sanitize(string? value, int maximumLength = 2048)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return new string(value.Where(character => !char.IsControl(character) && char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format).Take(maximumLength).ToArray());
    }
}
