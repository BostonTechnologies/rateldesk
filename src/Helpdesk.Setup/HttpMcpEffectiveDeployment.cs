using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Http.Configuration;
using Helpdesk.Shared.DTOs.Auth;
using HelpDesk.NewWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Helpdesk.Setup;

public sealed record DeploymentDiagnostic(string Code, string Layer, string Key, string Source, string Message, string NextAction);
public enum HttpMcpHostIntent { VerifyExisting, PrepareChanges, LegacyInitializer }

public sealed record HttpMcpDeploymentProposal(HttpMcpHostingSettings Settings, string SourceHash, string ProposalHash,
    JsonObject Overlay, IReadOnlyList<DeploymentDiagnostic> Diagnostics, IReadOnlyList<string> Changes)
{
    public bool Valid => Diagnostics.Count == 0;
    public HttpMcpHostIntent Intent { get; init; } = HttpMcpHostIntent.PrepareChanges;
    public bool IsAdoption => Intent == HttpMcpHostIntent.VerifyExisting;
    public string? SnapshotPath { get; init; }
    public string? RuntimeSnapshotPath { get; init; }
    public string? RuntimeBinding { get; init; }
    public string[] ServicesBefore { get; init; } = [];
    public string[] ServicesAfter { get; init; } = [];
    public int ServiceCountBefore => ServicesBefore.Length;
    public int ServiceCountAfter => ServicesAfter.Length;
    public JsonObject CompleteDeployment { get; init; } = new();
    public IReadOnlyList<HostDeploymentSetting> ReviewSettings { get; init; } = [];
    public HostDeploymentProposalRequest Approval => new(IsAdoption ? "Adopt existing HTTP MCP baseline — no deployment changes" : "Review HTTP MCP configuration changes", ProposalHash, SourceHash,
        Settings.PublicOrigin, Settings.PublicResourceUri, Settings.Instance, Settings.AuthenticationMode,
        Settings.ApiBaseUrl, Settings.McpDestination, Changes) { ReviewSettings = ReviewSettings };
}

/// <summary>Consumes Docker Compose's rendered result; binds it through the actual serving resolvers.</summary>
public static class HttpMcpEffectiveDeployment
{
    public const string EffectiveSourceVariable = "RATELDESK_SETUP_EFFECTIVE_COMPOSE_SOURCE";
    public const string RuntimeSourceVariable = "RATELDESK_SETUP_RUNTIME_ENVIRONMENT_SOURCE";
    public const string IntentVariable = "RATELDESK_SETUP_HOST_INTENT";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Services = ["api", "web", "mcp-http"];
    private const string RuntimeDirectory = "/run/rateldesk-mcp";
    private const string RuntimeConfiguration = RuntimeDirectory + "/config.json";
    private const string Migrations = "x-rateldesk-mount-migrations";

    public static HttpMcpHostIntent SelectedIntent => Environment.GetEnvironmentVariable(IntentVariable) switch
    {
        null or "" or "verify-existing" => HttpMcpHostIntent.VerifyExisting,
        "prepare-changes" => HttpMcpHostIntent.PrepareChanges,
        "legacy-initializer" => HttpMcpHostIntent.LegacyInitializer,
        _ => throw new AgentClientValidationException("HOST_INTENT_INVALID: Select verify-existing, prepare-changes or the explicit legacy-initializer alternative.")
    };

    public static HttpMcpDeploymentProposal AdoptBaseline(HttpMcpHostingSettings settings, string? snapshotPath = null) =>
        Create(settings, snapshotPath, HttpMcpHostIntent.VerifyExisting);

    public static string ProposalRevision(JsonObject complete, string sourceHash, HttpMcpHostIntent intent) =>
        Hash((intent == HttpMcpHostIntent.LegacyInitializer ? "" : intent + "\n") + Canonical(complete) + "\n" + sourceHash);

    public static HttpMcpDeploymentProposal Create(HttpMcpHostingSettings settings, string? snapshotPath = null, HttpMcpHostIntent intent = HttpMcpHostIntent.PrepareChanges, bool renderSourceReview = true)
    {
        snapshotPath ??= Environment.GetEnvironmentVariable(EffectiveSourceVariable);
        if (string.IsNullOrWhiteSpace(snapshotPath))
            throw new AgentClientValidationException("HOST_SOURCE_REQUIRED: Run the deployment archive's ./setup-http-mcp.sh on the Docker host. A container console cannot inspect or apply its owner's Compose deployment.");
        var text = SecureConfigurationFiles.ReadPrivateDocument(snapshotPath);
        var source = Parse(text);
        if (intent == HttpMcpHostIntent.VerifyExisting)
        {
            var baselineDiagnostics = Validate(source, settings).ToList();
            var runtimePath = Environment.GetEnvironmentVariable(RuntimeSourceVariable);
            JsonObject? runtime = null;
            if (string.IsNullOrWhiteSpace(runtimePath)) baselineDiagnostics.Add(new("HOST_RUNTIME_REQUIRED", "runtime", "existing deployment", "Docker host", "A source review alone cannot establish an adopted host baseline.", "Run the host launcher to capture the actual running deployment before baseline approval."));
            else runtime = Parse(SecureConfigurationFiles.ReadPrivateDocument(runtimePath));
            var sourceRevision = Hash(text);
            var binding = runtime is null ? null : RuntimeIdentity(runtime);
            var baseline = new HttpMcpDeploymentProposal(settings, sourceRevision, Hash("verify-existing\n" + Canonical(source) + "\n" + sourceRevision + "\n" + binding), new(), baselineDiagnostics, [])
            {
                Intent = intent, SnapshotPath = snapshotPath, RuntimeSnapshotPath = runtimePath, RuntimeBinding = binding,
                CompleteDeployment = source.DeepClone().AsObject(), ServicesBefore = ServiceNames(source), ServicesAfter = ServiceNames(source),
                ReviewSettings = Review(source, settings, sourceRevision)
            };
            if (runtime is not null) baselineDiagnostics.AddRange(Observe(baseline, runtimePath));
            return baseline with { Diagnostics = baselineDiagnostics, Changes = InventoryChanges(source, source, true) };
        }
        settings = settings with { Outbound = settings.AuthenticationMode == "gateway"
            ? AgentClientConfiguration.Empty with { ApiBaseUrl = settings.ApiBaseUrl, CredentialMode = "gateway" }
            : settings.Outbound is { } outbound ? HttpMcpHostingCoordinator.ContainerOutbound(outbound) : null };
        var legacy = intent == HttpMcpHostIntent.LegacyInitializer;
        var overlay = HttpMcpHostingCoordinator.ComposeOverlay(settings, legacy);
        if (legacy) PrepareOverlay(overlay, source, settings);
        else PrepareEnvironmentOverlay(overlay, source, settings);
        var migrationDiagnostics = legacy ? PrepareLegacyMigration(overlay, source, settings) : [];
        TranslateOverlayServices(overlay, source);
        var proposed = Materialize(source, overlay, settings, legacy);
        if (!legacy && renderSourceReview) proposed = RenderSourceReview(source, overlay, proposed);
        var sourceMountDiagnostics = new List<DeploymentDiagnostic>();
        if (legacy) ValidateMountCollisions(source, (code, layer, key, message, origin) =>
            sourceMountDiagnostics.Add(new(code, layer, key, origin, message, "Resolve the indicated owning mount before approval; setup only migrates the selected legacy config mapping.")), overlay[Migrations] as JsonArray);
        var diagnostics = migrationDiagnostics.Concat(sourceMountDiagnostics).Concat(Validate(proposed, settings, legacy)).Concat(HttpMcpHostingCoordinator.Validate(settings).Select(message =>
            new DeploymentDiagnostic("HOST_SETTINGS_INVALID", "proposal", "reviewed hosting settings", "reviewed proposal", message, "Correct the indicated setting in Advanced before approval."))).ToArray();
        var changes = InventoryChanges(source, proposed, false).Concat(SemanticChanges(source, proposed, overlay, legacy))
            .Concat(SourceDiffLines(proposed)).Take(32).Select(change => TerminalText.Sanitize(change, 512)).ToArray();
        var sourceHash = Hash(text);
        var proposalHash = ProposalRevision(proposed, sourceHash, intent);
        return new(settings, sourceHash, proposalHash, overlay, diagnostics, changes)
        { Intent = intent, SnapshotPath = snapshotPath, CompleteDeployment = proposed, ReviewSettings = Review(proposed, settings, sourceHash),
            ServicesBefore = ServiceNames(source), ServicesAfter = ServiceNames(proposed) };
    }

    private static JsonObject RenderSourceReview(JsonObject source, JsonObject overlay, JsonObject proposed)
    {
        if (overlay["services"] is JsonObject { Count: 0 })
        {
            var unchanged = proposed.DeepClone().AsObject(); unchanged["x-rateldesk-source-candidates"] = new JsonArray(); return unchanged;
        }
        var commandText = Environment.GetEnvironmentVariable("RATELDESK_SETUP_SOURCE_REVIEW_COMMAND");
        if (string.IsNullOrWhiteSpace(commandText)) return proposed; // Headless model validation remains independently testable.
        string[] command;
        try { command = JsonSerializer.Deserialize<string[]>(commandText) ?? []; }
        catch (JsonException) { throw new AgentClientValidationException("HOST_SOURCE_REVIEW_INVALID: The host launcher did not provide a valid source-review command."); }
        if (command.Length is < 1 or > 16 || command.Any(value => value.Length is < 1 or > 8192 || value.Any(char.IsControl)))
            throw new AgentClientValidationException("HOST_SOURCE_REVIEW_INVALID: The host source-review command is invalid.");
        var start = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AgentClientValidationException("HOST_SOURCE_REVIEW_REQUIRED: The source-review helper could not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(overlay.ToJsonString()); process.StandardInput.Close();
        if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new AgentClientValidationException("HOST_SOURCE_REVIEW_TIMEOUT: Source review exceeded its bounded validation time; no source was applied."); }
        var resultText = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
        if (Encoding.UTF8.GetByteCount(resultText) > SecureConfigurationFiles.DefaultMaxBytes * 8)
            throw new AgentClientValidationException("HOST_SOURCE_REVIEW_FAILED: The original source could not be edited and validated losslessly. Review the source-specific recovery action from the host launcher before approval.");
        if (process.ExitCode != 0)
        {
            try
            {
                var failure = Parse(resultText)["error"];
                var code = failure?["code"]?.GetValue<string>();
                var known = new[] { "unreadable", "shape", "ambiguous", "dependency", "yaml", "flow", "alias", "scalar", "overlay", "context", "compose", "changed", "service", "candidate_changed", "impact", "invalid" };
                if (code is not null && known.Contains(code.Replace("source_review_", "", StringComparison.Ordinal)) && code.StartsWith("source_review_", StringComparison.Ordinal))
                    throw new AgentClientValidationException("HOST_SOURCE_REVIEW_FAILED [" + code + "]: " + TerminalText.Sanitize(failure?["message"]?.ToString(), 1024) + " Source: " + TerminalText.Sanitize(failure?["source"]?.ToString(), 512));
            }
            catch (JsonException) { }
            throw new AgentClientValidationException("HOST_SOURCE_REVIEW_FAILED: The original source could not be edited and validated losslessly. Re-read its owning source and prepare a source-specific change before approval.");
        }
        JsonObject result;
        try { result = Parse(resultText); }
        catch (JsonException) { throw new AgentClientValidationException("HOST_SOURCE_REVIEW_INVALID: Source review returned an invalid candidate description."); }
        if (result["completeDeployment"] is not JsonObject rendered || result["sourceCandidates"] is not JsonArray candidates)
            throw new AgentClientValidationException("HOST_SOURCE_REVIEW_INVALID: Source review did not return an effective candidate and original-source bindings.");
        var reviewed = proposed.DeepClone().AsObject();
        foreach (var entry in rendered.Where(entry => !entry.Key.StartsWith("x-rateldesk-", StringComparison.Ordinal))) reviewed[entry.Key] = entry.Value?.DeepClone();
        var expected = proposed.DeepClone().AsObject();
        // Compose, rather than this model merger, decides real effective semantics.
        foreach (var entry in source.Where(entry => !entry.Key.StartsWith("x-rateldesk-", StringComparison.Ordinal) && rendered[entry.Key] is null).ToArray()) reviewed.Remove(entry.Key);
        var collateral = new List<DeploymentDiagnostic>(); CompareTopology(expected, reviewed, collateral, "HOST_SOURCE_REVIEW_COLLATERAL");
        if (collateral.Count > 0 || Services.Any(service => EffectiveFingerprint(expected, service) != EffectiveFingerprint(reviewed, service)))
            throw new AgentClientValidationException("HOST_SOURCE_REVIEW_COLLATERAL: Compose resolved an unrelated change. Review the source-specific edit before approval.");
        reviewed["x-rateldesk-source-candidates"] = candidates.DeepClone();
        reviewed["x-rateldesk-source-review-changes"] = result["changes"]?.DeepClone();
        return reviewed;
    }

    public static HttpMcpHostingSettings Adopt(HttpMcpHostingSettings fallback, string? snapshotPath = null)
    {
        snapshotPath ??= Environment.GetEnvironmentVariable(EffectiveSourceVariable);
        if (string.IsNullOrWhiteSpace(snapshotPath)) return fallback;
        var source = Parse(SecureConfigurationFiles.ReadPrivateDocument(snapshotPath));
        using var mcp = Configuration(source, "mcp-http");
        using var web = Configuration(source, "web");
        string? Get(IConfiguration configuration, string key, string? otherwise) => configuration[key] ?? otherwise;
        var http = mcp.GetSection(HelpdeskMcpHttpOptions.SectionName).Get<HelpdeskMcpHttpOptions>();
        var env = EnvironmentFor(source, "mcp-http");
        var outbound = Outbound(source, mcp, env);
        var resource = http?.PublicResourceUri;
        var origin = web["WebGateway:PublicOrigin"] ?? (Uri.TryCreate(resource, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : fallback.PublicOrigin);
        return fallback with
        {
            PublicOrigin = origin, ApiBaseUrl = http?.ExpectedApiBaseUrl is { Length: > 0 } api ? api : web["ApiBaseUrl"] ?? outbound.ApiBaseUrl ?? fallback.ApiBaseUrl,
            McpDestination = Get(web, "WebGateway:Mcp:Destination", fallback.McpDestination)!,
            Instance = http?.Instance is { Length: > 0 } instance ? instance : env.GetValueOrDefault("RATELDESK_MCP_INSTANCE") ?? fallback.Instance,
            AuthenticationMode = mcp["Helpdesk:Mcp:AuthenticationMode"] ?? web["WebGateway:Mcp:AuthenticationMode"] ?? fallback.AuthenticationMode,
            Listener = mcp["urls"] ?? mcp["DOTNET_URLS"] ?? mcp["ASPNETCORE_URLS"] ?? fallback.Listener,
            AllowedOrigins = http?.AllowedOrigins is { Length: > 0 } allowed ? allowed : fallback.AllowedOrigins,
            DelegationTimeoutSeconds = http?.DelegationTimeoutSeconds ?? fallback.DelegationTimeoutSeconds,
            AuthentikAuthority = mcp["Authentication:AuthentikMcp:Authority"], AuthentikAudience = mcp["Authentication:AuthentikMcp:Audience"],
            RequiredScopes = mcp.GetSection("Authentication:AuthentikMcp:RequiredScopes").Get<string[]>() ?? [],
            RequiredGroups = mcp.GetSection("Authentication:AuthentikMcp:RequiredGroups").Get<string[]>() ?? [],
            Outbound = outbound.ApiBaseUrl is null ? fallback.Outbound : outbound
        };
    }

    public static IReadOnlyList<DeploymentDiagnostic> Validate(JsonObject complete, HttpMcpHostingSettings expected, bool requireSetupStorage = false)
    {
        var failures = new List<DeploymentDiagnostic>();
        void Fail(string code, string layer, string key, string message, string source = "effective Compose / application providers") =>
            failures.Add(new(code, layer, key, source, message, "Correct the winning configuration source, then run the host launcher again."));
        foreach (var service in Services)
            if (complete["services"]?[ServiceName(complete, service)] is not JsonObject) Fail("HOST_SERVICE_MISSING", service, "services:" + service, "The complete deployment must contain this service.");
        if (failures.Count > 0) return failures;
        try
        {
            using var web = Configuration(complete, "web");
            using var mcp = Configuration(complete, "mcp-http");
            using var api = Configuration(complete, "api");
            var http = mcp.GetSection(HelpdeskMcpHttpOptions.SectionName).Get<HelpdeskMcpHttpOptions>() ?? new();
            var ingress = new HelpdeskMcpHttpOptionsValidator().Validate(null, http);
            foreach (var failure in ingress.Failures ?? []) Fail("MCP_OPTIONS_INVALID", "mcp-http", failure.Split(' ')[0], failure);
            var env = EnvironmentFor(complete, "mcp-http");
            var outbound = Outbound(complete, mcp, env);
            var instance = string.IsNullOrWhiteSpace(http.Instance) ? env.GetValueOrDefault("RATELDESK_MCP_INSTANCE") : http.Instance;
            var expectedApi = string.IsNullOrWhiteSpace(http.ExpectedApiBaseUrl) ? env.GetValueOrDefault("RATELDESK_MCP_EXPECTED_API_BASE_URL") ?? env.GetValueOrDefault($"RATELDESK_MCP_{instance?.ToUpperInvariant()}_API_BASE_URL") : http.ExpectedApiBaseUrl;
            _ = HelpdeskMcpTarget.Resolve(outbound, null, instance, expectedApi);
            _ = outbound.Resolve();
            if (http.AuthenticationMode == "gateway" && outbound.CredentialMode != "gateway")
                Fail("MCP_OUTBOUND_MODE", "mcp-http", "Helpdesk:Mcp:Outbound:CredentialMode", "Gateway mode requires credentialMode=gateway without a stored deployment credential.");
            if (http.AuthenticationMode == "authentik")
            {
                var external = new AuthentikMcpOptionsValidator(Options.Create(http)).Validate(null,
                    mcp.GetSection(AuthentikMcpOptions.SectionName).Get<AuthentikMcpOptions>() ?? new());
                foreach (var failure in external.Failures ?? []) Fail("MCP_EXTERNAL_INVALID", "mcp-http", failure.Split(' ')[0], failure);
            }
            using var gateway = new WebGatewayConfiguration(web);
            var options = gateway.Options;
            if (!options.McpEnabled || options.McpHardDisabled)
                Fail("WEB_MCP_DISABLED", "web", "WebGateway:Mcp:Enabled", "MCP is disabled in the effective deployment; enable it explicitly in its owning source before approval.");
            if (!Same(options.PublicOrigin?.AbsoluteUri, expected.PublicOrigin) ||
                !Same(options.PublicMcpResource?.AbsoluteUri, expected.PublicResourceUri) || !Same(http.PublicResourceUri, expected.PublicResourceUri))
                Fail("HOST_RESOURCE_MISMATCH", "web/mcp-http", "WebGateway:PublicOrigin", "Web origin and the canonical MCP resource must agree with the reviewed URL.");
            if (options.McpAuthenticationMode != http.AuthenticationMode || http.AuthenticationMode != expected.AuthenticationMode)
                Fail("HOST_AUTHENTICATION_MISMATCH", "web/mcp-http", "Helpdesk:Mcp:AuthenticationMode", "Web ingress and MCP authentication modes must agree; authentication never falls back.");
            if (!Same(options.ApiBaseUri.AbsoluteUri, expected.ApiBaseUrl) || !Same(expectedApi, expected.ApiBaseUrl) || !Same(outbound.ApiBaseUrl, expected.ApiBaseUrl))
                Fail("HOST_API_TARGET_MISMATCH", "web/mcp-http", "Helpdesk:Mcp:ExpectedApiBaseUrl", "The effective pinned API destinations must equal the reviewed backend.");
            if (!Same(api["IntegrationCredentials:Pairing:PublicOrigin"], expected.PublicOrigin))
                Fail("HOST_PAIRING_ORIGIN_MISMATCH", "api", "IntegrationCredentials:Pairing:PublicOrigin", "API pairing must advertise the reviewed public Web origin.");
            if (instance != expected.Instance)
                Fail("HOST_INSTANCE_MISMATCH", "mcp-http", "Helpdesk:Mcp:Instance", "The effective instance differs from the reviewed instance label.");
            if ((mcp["urls"] ?? mcp["DOTNET_URLS"] ?? mcp["ASPNETCORE_URLS"]) is { } listener && listener != expected.Listener)
                Fail("HOST_LISTENER_MISMATCH", "mcp-http", "urls", "The effective serving listener differs from the reviewed listener; command arguments override environment values.");
            if (!http.AllowedOrigins.SequenceEqual(expected.AllowedOrigins, StringComparer.Ordinal) || http.DelegationTimeoutSeconds != expected.DelegationTimeoutSeconds)
                Fail("HOST_EFFECTIVE_OPTIONS_MISMATCH", "mcp-http", "Helpdesk:Mcp:AllowedOrigins", "The effective origin set or timeout differs from the reviewed values. An inherited array member may be masking the proposed clear.");
            if (http.AuthenticationMode == "authentik")
            {
                var external = mcp.GetSection(AuthentikMcpOptions.SectionName).Get<AuthentikMcpOptions>() ?? new();
                if (!external.RequiredScopes.SequenceEqual(expected.RequiredScopes, StringComparer.Ordinal) || !external.RequiredGroups.SequenceEqual(expected.RequiredGroups, StringComparer.Ordinal) ||
                    external.Authority.TrimEnd('/') != expected.AuthentikAuthority?.TrimEnd('/') || external.Audience.TrimEnd('/') != expected.AuthentikAudience?.TrimEnd('/'))
                    Fail("HOST_EFFECTIVE_OPTIONS_MISMATCH", "mcp-http", "Authentication:AuthentikMcp", "The effective external authorization requirements differ from the reviewed scope/group sets.");
            }
            ValidateRoutes(gateway.EffectiveConfiguration, expected, Fail);
            if (requireSetupStorage) ValidateStorage(complete, Fail);
            else ValidateRetainedMounts(complete, Fail);
        }
        catch (Exception error) when (error is InvalidOperationException or AgentClientValidationException or FormatException or JsonException or ArgumentException)
        {
            // Provider exceptions may contain raw values; emit a bounded canonical diagnosis instead.
            Fail("HOST_EFFECTIVE_INVALID", "configuration", "effective configuration", "The actual serving validator rejected the effective configuration. Inspect its winning provider and protected file references without logging their contents.");
        }
        return failures.GroupBy(failure => (failure.Code, failure.Layer, failure.Key)).Select(group => group.First()).ToArray();
    }

    public static IReadOnlyList<DeploymentDiagnostic> Observe(HttpMcpDeploymentProposal approved, string? runtimePath = null)
    {
        runtimePath ??= Environment.GetEnvironmentVariable(RuntimeSourceVariable);
        if (string.IsNullOrWhiteSpace(runtimePath)) return [new("HOST_RUNTIME_REQUIRED", "runtime", "observed deployment", "Docker host", "Saved files are not an observed running deployment.", "Run ./setup-http-mcp.sh --verify on the Docker host after recreation.")];
        var runtime = Parse(SecureConfigurationFiles.ReadPrivateDocument(runtimePath));
        var failures = Validate(runtime, approved.Settings, approved.Intent == HttpMcpHostIntent.LegacyInitializer).ToList();
        var source = Parse(SecureConfigurationFiles.ReadPrivateDocument(approved.SnapshotPath ?? Environment.GetEnvironmentVariable(EffectiveSourceVariable)!));
        var proposed = approved.CompleteDeployment;
        // Legacy checkpoints predate the full Docker inventory capture. Their
        // established provider/image/protected-volume checks remain compatible.
        if (approved.Intent != HttpMcpHostIntent.LegacyInitializer)
            CompareTopology(proposed, runtime, failures, "HOST_RUNTIME_TOPOLOGY_MISMATCH");
        foreach (var service in Services)
        {
            if (EffectiveFingerprint(proposed, service) != EffectiveFingerprint(runtime, service))
                failures.Add(new("HOST_RUNTIME_PROVIDER_MISMATCH", service, "effective providers", "observed application providers", "Running effective configuration differs from the reviewed provider generation.", "Apply the reviewed providers and recreate the service before verification."));
            var desired = EnvironmentFor(proposed, service);
            var observed = EnvironmentFor(runtime, service);
            foreach (var setting in desired.Where(setting => Relevant(setting.Key)))
                if (!observed.TryGetValue(setting.Key, out var value) || value != setting.Value)
                    failures.Add(new("HOST_RUNTIME_MISMATCH", service, setting.Key.Replace("__", ":", StringComparison.Ordinal), "observed container environment", "Running configuration differs from the approved generation.", "Apply the reviewed complete deployment and recreate the affected service, then invoke verify again."));
            if (source["x-rateldesk-image-ids"]?[service]?.GetValue<string>() is { } expectedImage &&
                runtime["x-rateldesk-image-ids"]?[service]?.GetValue<string>() != expectedImage)
                failures.Add(new("HOST_RUNTIME_IMAGE", service, "image", "Docker image ID", "The running image differs from the reviewed image.", "Revalidate the changed image before approval."));
        }
        if (approved.IsAdoption)
        {
            if (runtime["x-rateldesk-runtime-inventory"] is not JsonObject inventory || ServiceNames(proposed).Any(service => inventory[service] is not JsonObject))
                failures.Add(new("HOST_RUNTIME_IDENTITY_REQUIRED", "runtime", "container inventory", "Docker host observation", "The complete running-container identity inventory is required for baseline adoption.", "Capture all defined services through the deployment owner's Docker host launcher."));
            else
            {
                foreach (var service in inventory)
                    if ((!ApprovedOneShot(proposed, service.Key) || service.Value?["state"]?.ToString() != "exited" || service.Value?["exitCode"]?.GetValue<int>() != 0) &&
                        (service.Value?["state"]?.ToString() != "running" || service.Value?["healthStatus"]?.ToString() is "unhealthy" or "starting"))
                        failures.Add(new("HOST_RUNTIME_NOT_READY", service.Key, "runtime state", "Docker host observation", "A defined service is not running and ready for baseline verification.", "Resolve its observed state through the existing deployment owner; a missing healthcheck remains a separate absence."));
                if (RuntimeIdentity(runtime) != approved.RuntimeBinding)
                    failures.Add(new("HOST_RUNTIME_BASELINE_CHANGED", "runtime", "container identity", "adopted running-container baseline", "Container identity, starts, restart counts or deployment configuration changed after baseline review.", "Re-read the actual source and running deployment and review a new baseline."));
            }
        }
        if (approved.Intent != HttpMcpHostIntent.LegacyInitializer) return failures;
        var desiredRuntime = Mounts(proposed, "mcp-http").FirstOrDefault(mount => mount.Kind == "volumes" && mount.Target == RuntimeDirectory);
        var observedRuntime = Mounts(runtime, "mcp-http").FirstOrDefault(mount => mount.Kind == "volumes" && mount.Target == RuntimeDirectory);
        if (desiredRuntime is null || observedRuntime is null || desiredRuntime.Type != observedRuntime.Type ||
            VolumeName(proposed, desiredRuntime.Source) != VolumeName(runtime, observedRuntime.Source))
            failures.Add(new("HOST_RUNTIME_CONFIG_STORAGE_MISMATCH", "mcp-http", "protected runtime volume", "observed Docker mount identity", "The running protected configuration store differs from the reviewed persistent volume.", "Recreate the service from the approved complete deployment before verification."));
        return failures;
    }

    private static string EffectiveFingerprint(JsonObject complete, string service)
    {
        using var configuration = Configuration(complete, service);
        var relevant = configuration.AsEnumerable().Where(entry => entry.Value is not null && !entry.Key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) &&
            (Relevant(entry.Key.Replace(":", "__", StringComparison.Ordinal)) || entry.Key.StartsWith("ReverseProxy:", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(entry => new { key = entry.Key.ToUpperInvariant(), entry.Value }).ToArray();
        var outbound = service == "mcp-http" ? Outbound(complete, configuration, EnvironmentFor(complete, service)) : null;
        return Hash(JsonSerializer.Serialize(new { relevant, outbound }, Json));
    }

    public static IReadOnlyList<DeploymentDiagnostic> CheckApprovedEffective(HttpMcpDeploymentProposal approved, JsonObject current)
    {
        var original = Parse(SecureConfigurationFiles.ReadPrivateDocument(approved.SnapshotPath!));
        var expected = approved.CompleteDeployment;
        var failures = Validate(current, approved.Settings, approved.Intent == HttpMcpHostIntent.LegacyInitializer).ToList();
        CompareTopology(expected, current, failures, "HOST_SOURCE_CHANGED");
        foreach (var service in Services)
            if (EffectiveFingerprint(current, service) != EffectiveFingerprint(expected, service) ||
                MountFingerprint(current, service) != MountFingerprint(expected, service) ||
                original["x-rateldesk-image-ids"]?[service]?.ToString() != current["x-rateldesk-image-ids"]?[service]?.ToString())
                failures.Add(new("HOST_SOURCE_CHANGED", service, "effective imported configuration", "reviewed complete deployment document", "The imported effective configuration differs from the approved generation.", "Review the current sources before resuming."));
        if (MountFingerprint(current, "mcp-config-init") != MountFingerprint(expected, "mcp-config-init") ||
            !JsonNode.DeepEquals(current["services"]?["mcp-config-init"]?["command"], expected["services"]?["mcp-config-init"]?["command"]) ||
            !JsonNode.DeepEquals(current["services"]?["mcp-config-init"]?["entrypoint"], expected["services"]?["mcp-config-init"]?["entrypoint"]))
            failures.Add(new("HOST_SOURCE_CHANGED", "mcp-config-init", "protected initializer", "reviewed complete deployment document", "The complete deployment initializer differs from the bounded approved generation.", "Review the current sources before resuming."));
        return failures;
    }

    private static string ServiceName(JsonObject complete, string role) => complete["x-rateldesk-service-roles"]?[role]?.GetValue<string>() ?? role;

    private static bool ApprovedOneShot(JsonObject complete, string service) => complete["services"] is JsonObject services && services.Any(entry =>
        entry.Value?["depends_on"]?[service]?["condition"]?.ToString() == "service_completed_successfully");

    private static void TranslateOverlayServices(JsonObject overlay, JsonObject source)
    {
        var services = overlay["services"]!.AsObject();
        foreach (var role in Services)
        {
            var name = ServiceName(source, role);
            if (name == role || !services.ContainsKey(role)) continue;
            var value = services[role]!.DeepClone(); services.Remove(role); services[name] = value;
        }
    }

    private static string[] ServiceNames(JsonObject complete) => complete["services"] is JsonObject services
        ? services.Select(service => service.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray() : [];

    private static JsonObject Topology(JsonObject complete, string service)
    {
        var result = complete["services"]?[service]?.DeepClone().AsObject() ?? new();
        result.Remove("environment");
        // Raw resolved references, including labels and absent healthchecks, remain authoritative.
        return result;
    }

    private static void CompareTopology(JsonObject expected, JsonObject current, List<DeploymentDiagnostic> failures, string code)
    {
        if (!ServiceNames(expected).SequenceEqual(ServiceNames(current)))
            failures.Add(new(code, "deployment", "defined service inventory", "reviewed Compose model", "Defined service names or count differ from the reviewed deployment.", "Review the current saved sources and actual runtime before continuing."));
        foreach (var service in ServiceNames(expected).Intersect(ServiceNames(current), StringComparer.Ordinal))
            if (Canonical(Topology(expected, service)) != Canonical(Topology(current, service)))
                failures.Add(new(code, service, "service configuration", "reviewed Compose model", "Image, mounts, dependencies, ports, networks, command, healthcheck, labels or restart policy differ from the reviewed deployment.", "Review the changed source and affected-service recreation through its existing owner."));
        foreach (var key in new[] { "volumes", "configs", "secrets", "networks" })
            if (Canonical(expected[key]) != Canonical(current[key]))
                failures.Add(new(code, "deployment", key + " references", "reviewed Compose model", "Deployment resource definitions differ from the reviewed source.", "Review the current source without deleting any existing deployment resources."));
    }

    private static string RuntimeIdentity(JsonObject runtime)
    {
        var inventory = runtime["x-rateldesk-runtime-inventory"]?.DeepClone().AsObject() ?? new();
        foreach (var service in inventory)
            if (service.Value is JsonObject observed) { observed.Remove("state"); observed.Remove("healthStatus"); }
        return Hash(Canonical(inventory));
    }

    private static IReadOnlyList<string> InventoryChanges(JsonObject source, JsonObject proposed, bool adoption)
    {
        var before = ServiceNames(source); var after = ServiceNames(proposed);
        var added = after.Except(before).ToArray(); var removed = before.Except(after).ToArray();
        var changed = before.Intersect(after).Where(service => Canonical(source["services"]?[service]) != Canonical(proposed["services"]?[service])).ToArray();
        var changes = new List<string>
        {
            $"Defined services: {before.Length} → {after.Length}. {string.Join(", ", after)}.",
            $"Services added: {(added.Length == 0 ? "none" : string.Join(", ", added))}; removed: {(removed.Length == 0 ? "none" : string.Join(", ", removed))}; changed: {(changed.Length == 0 ? "none" : string.Join(", ", changed))}.",
            adoption ? "No deployment changes; zero service recreation. Review adopts the current baseline, not an earlier provisioning approval. Temporary verifier issuance/revocation uses explicit consent and API writes." :
                changed.Length == 0 && added.Length == 0 && removed.Length == 0 ? "No effective deployment changes are needed; choose Verify existing deployment." :
                "Changes prepared, not applied. Only the listed changed services may require recreation after explicit owner apply."
        };
        foreach (var service in after)
        {
            var definition = proposed["services"]![service]!;
            string Present(string key) => definition[key] is null ? "absent" : "present";
            changes.Add(service + $": mounts/configs/secrets {Mounts(proposed, service, actualName: true).Count()}; ports {Present("ports")}; networks {Present("networks")}; dependencies {Present("depends_on")}; command {Present("command")}; entrypoint {Present("entrypoint")}; healthcheck {Present("healthcheck")}; restart policy {Present("restart")}.");
        }
        return changes.Take(32).Select(change => TerminalText.Sanitize(change, 512)).ToArray();
    }

    private static IEnumerable<string> SourceDiffLines(JsonObject proposed)
    {
        if (proposed["x-rateldesk-source-candidates"] is not JsonArray candidates) yield break;
        foreach (var candidate in candidates)
            if (candidate?["safeDiff"]?.GetValue<string>() is { } diff)
                foreach (var line in diff.Split('\n', StringSplitOptions.RemoveEmptyEntries)) yield return "Source diff: " + line;
    }

    private static void ValidateRetainedMounts(JsonObject complete, Action<string, string, string, string, string> fail)
    {
        foreach (var service in ServiceNames(complete))
            foreach (var collision in Mounts(complete, service, actualName: true).GroupBy(mount => mount.Target).Where(group => group.Count() > 1))
                fail("HOST_MOUNT_COLLISION", service, "mount destination", "Multiple configuration, secret or volume mounts select the same effective destination.", "effective Compose mount destinations");
    }

    public static IReadOnlyList<DeploymentDiagnostic> ValidateExport(HttpMcpDeploymentProposal approved, JsonObject exported)
    {
        // The host renders the actual manager document through Compose. Retain
        // reviewed protected providers/image metadata, but validate its real final
        // services and mount definitions rather than the generated fragment.
        var complete = approved.CompleteDeployment.DeepClone().AsObject();
        foreach (var entry in exported) complete[entry.Key] = entry.Value?.DeepClone();
        return CheckApprovedEffective(approved, complete);
    }

    private static void PrepareOverlay(JsonObject overlay, JsonObject source, HttpMcpHostingSettings settings)
    {
        var services = overlay["services"]!.AsObject();
        var mcp = services["mcp-http"]!.AsObject();
        mcp["image"] = source["services"]?[ServiceName(source, "mcp-http")]?["image"]?.DeepClone();
        var environment = mcp["environment"]!.AsObject();
        if (source["x-rateldesk-mcp-proposed"]?.GetValue<bool>() == true)
        {
            var webEnvironment = EnvironmentFor(source, "web");
            if (webEnvironment.TryGetValue("NO_PROXY", out var proxyExclusions))
            { environment["NO_PROXY"] = proxyExclusions; environment["no_proxy"] = proxyExclusions; }
        }
        environment["RATELDESK_MCP_CONFIG"] = "/run/rateldesk-mcp/config.json";
        environment["Helpdesk__Mcp__ConfigurationPath"] = "/run/rateldesk-mcp/config.json";
        if (settings.AuthenticationMode == "gateway")
        {
            environment["Helpdesk__Mcp__Outbound__ApiBaseUrl"] = settings.ApiBaseUrl;
            environment["Helpdesk__Mcp__Outbound__CredentialMode"] = "gateway";
            // Explicit clears at the winning canonical source prevent aliases/image defaults resurfacing.
            foreach (var key in new[] { "IntegrationCredential", "IntegrationCredentialFile", "AuthentikTokenUrl", "AuthentikClientId", "AuthentikUsername", "AuthentikAppPassword", "AuthentikAppPasswordFile", "CredentialTarget" })
                environment["Helpdesk__Mcp__Outbound__" + key] = "";
            foreach (var entry in EnvironmentFor(source, "mcp-http").Keys.Where(key => key.StartsWith("Authentication__AuthentikMcp__", StringComparison.OrdinalIgnoreCase))) environment[entry] = "";
        }
        var storage = source["x-rateldesk-storage"] as JsonObject;
        var volumes = new JsonObject();
        var mounts = new JsonArray();
        foreach (var kind in new[] { "host", "client" })
        {
            if (storage?[kind] is not JsonObject store) continue;
            var type = store["type"]?.GetValue<string>(); var path = store["source"]?.GetValue<string>();
            var target = store["target"]?.GetValue<string>();
            if (type == "volume")
            {
                var key = "rateldesk-setup-" + kind;
                volumes[key] = new JsonObject { ["external"] = true, ["name"] = path };
                mounts.Add(new JsonObject { ["type"] = "volume", ["source"] = key, ["target"] = target });
            }
            else if (type == "bind") mounts.Add(new JsonObject { ["type"] = "bind", ["source"] = path, ["target"] = target });
            environment[kind == "host" ? "RATELDESK_MCP_HOST_DIRECTORY" : "RATELDESK_MCP_CLIENT_DIRECTORY"] = target;
        }
        if (mounts.Count > 0) mcp["volumes"] = mounts;
        if (volumes.Count > 0) overlay["volumes"] = volumes;
        var initializer = services["mcp-config-init"]!.AsObject();
        initializer["image"] = source["services"]?[ServiceName(source, "mcp-http")]?["image"]?.DeepClone();
        initializer["user"] = "0:0";
        initializer["entrypoint"] = new JsonArray("/bin/sh", "-ec");
        initializer["command"] = new JsonArray("mkdir -p /run/rateldesk-mcp && cp /output/mcp-outbound.json /run/rateldesk-mcp/config.json && chown 10001:10001 /run/rateldesk-mcp/config.json && chmod 0400 /run/rateldesk-mcp/config.json");
        // Reuse a reviewed existing private runtime volume, including its Compose
        // alias, so an already-migrated stack does not accumulate generations.
        var configVolumeName = Mounts(source, "mcp-http").FirstOrDefault(mount => mount.Kind == "volumes" &&
            mount.Type == "volume" && mount.Target == RuntimeDirectory && mount.ReadOnly && !string.IsNullOrWhiteSpace(mount.Source))?.Source ?? "mcp-http-config";
        var initMounts = new JsonArray(new JsonObject { ["type"] = "volume", ["source"] = configVolumeName, ["target"] = "/run/rateldesk-mcp" });
        if (mounts.FirstOrDefault(mount => mount?["target"]?.GetValue<string>() == "/output") is { } hostMount) initMounts.Add(hostMount.DeepClone());
        initializer["volumes"] = initMounts;
        overlay["volumes"] ??= new JsonObject();
        overlay["volumes"]![configVolumeName] ??= source["volumes"]?[configVolumeName]?.DeepClone() ?? new JsonObject();
        mounts.Add(new JsonObject { ["type"] = "volume", ["source"] = configVolumeName, ["target"] = "/run/rateldesk-mcp", ["read_only"] = true });
        mcp["volumes"] = mounts;
        mcp["depends_on"] = new JsonObject { ["mcp-config-init"] = new JsonObject { ["condition"] = "service_completed_successfully" } };
    }

    private static void PrepareEnvironmentOverlay(JsonObject overlay, JsonObject source, HttpMcpHostingSettings settings)
    {
        var services = overlay["services"]!.AsObject();
        var mcpEnvironment = services["mcp-http"]!["environment"]!.AsObject();
        using var mcp = Configuration(source, "mcp-http");
        var existing = Outbound(source, mcp, EnvironmentFor(source, "mcp-http"));
        if ((mcp["urls"] ?? mcp["DOTNET_URLS"] ?? mcp["ASPNETCORE_URLS"]) == settings.Listener)
            mcpEnvironment.Remove("ASPNETCORE_URLS");
        if (existing.ApiBaseUrl is { } existingApi && Same(existingApi, settings.ApiBaseUrl))
            mcpEnvironment.Remove("Helpdesk__Mcp__Outbound__ApiBaseUrl");
        if (existing.CredentialMode == settings.AuthenticationMode && settings.AuthenticationMode == "gateway")
            mcpEnvironment.Remove("Helpdesk__Mcp__Outbound__CredentialMode");
        if (settings.AuthenticationMode == "authentik" && settings.Outbound?.Resolve() != existing.Resolve())
            throw new AgentClientValidationException("HOST_EXTERNAL_IDENTITY_CHANGED: Preserve the separately configured external downstream identity. Review changes in its protected owning provider before preparing a host configuration patch.");
        if (settings.AuthenticationMode == "gateway")
        {
            // Clear a competing lower provider only as an explicit reviewed source change.
            foreach (var key in new[] { "IntegrationCredential", "IntegrationCredentialFile", "AuthentikTokenUrl", "AuthentikClientId", "AuthentikUsername", "AuthentikAppPassword", "AuthentikAppPasswordFile", "CredentialTarget" })
            {
                if (key == "CredentialTarget" && Same(existing.CredentialTarget, settings.ApiBaseUrl)) continue;
                if (mcp["Helpdesk:Mcp:Outbound:" + key] is { Length: > 0 } || typeof(AgentClientConfiguration).GetProperty(key)?.GetValue(existing) is string { Length: > 0 })
                    mcpEnvironment["Helpdesk__Mcp__Outbound__" + key] = "";
            }
        }
        foreach (var service in Services)
        {
            var environment = services[service]!["environment"]!.AsObject();
            using var current = Configuration(source, service);
            foreach (var entry in environment.ToArray())
                if (current[entry.Key.Replace("__", ":", StringComparison.Ordinal)] == entry.Value?.ToString()) environment.Remove(entry.Key);
            if (environment.Count == 0) services.Remove(service);
        }
    }

    private static void ValidateStorage(JsonObject complete, Action<string, string, string, string, string> fail)
    {
        string ResolveMount(DeploymentMount mount) => mount.Type == "volume" ? VolumeName(complete, mount.Source) : mount.Source;
        foreach (var kind in new[] { "host", "client" })
        {
            var storage = complete["x-rateldesk-storage"]?[kind];
            if (storage?["private"]?.GetValue<bool>() != true || storage["readOnly"]?.GetValue<bool>() != false ||
                storage["type"]?.GetValue<string>() is not ("bind" or "volume") || string.IsNullOrWhiteSpace(storage["source"]?.GetValue<string>()))
                fail("HOST_STORAGE_REQUIRED", "storage", kind, "Private writable persistent storage must be tested as the serving identity before approval.", "Docker host storage preflight");
            var target = storage?["target"]?.GetValue<string>();
            var source = storage?["source"]?.GetValue<string>();
            if (!Mounts(complete, "mcp-http").Any(mount => mount.Kind == "volumes" && mount.Target == target && ResolveMount(mount) == source && !mount.ReadOnly))
                fail("HOST_STORAGE_MOUNT_MISSING", "storage", kind, "The serving MCP deployment must mount the reviewed private persistent store writable at its exact setup path.", "effective Compose mounts");
        }
        if (complete["x-rateldesk-storage"]?["host"]?["source"]?.GetValue<string>() == complete["x-rateldesk-storage"]?["client"]?["source"]?.GetValue<string>())
            fail("HOST_STORAGE_ISOLATION", "storage", "client", "Personal client secrets require a separate persistent store.", "Docker host storage preflight");
        var clientSource = complete["x-rateldesk-storage"]?["client"]?["source"]?.GetValue<string>();
        if (Mounts(complete, "api").Any(mount => mount.Kind == "volumes" && ResolveMount(mount) == clientSource))
            fail("HOST_STORAGE_API_EXPOSURE", "storage", "client", "API must not receive personal client storage.", "effective Compose mounts");
        if (!Mounts(complete, "mcp-http").Any(mount => mount.Kind == "volumes" && mount.Target == RuntimeDirectory && mount.Type == "volume" && mount.ReadOnly))
            fail("HOST_RUNTIME_CONFIG_MOUNT", "mcp-http", "protected runtime configuration", "The serving MCP process requires its reviewed protected runtime volume mounted read-only.", "complete Compose mount destinations");
        ValidateMountCollisions(complete, fail);
    }

    private sealed record DeploymentMount(string Kind, string Source, string Target, string Type, bool ReadOnly);

    private static string VolumeName(JsonObject complete, string source) => complete["volumes"]?[source]?["name"]?.GetValue<string>() ??
        (complete["volumes"]?[source]?["external"]?.GetValue<bool>() != true && complete["name"]?.GetValue<string>() is { } project ? project + "_" + source : source);

    private static string MountFingerprint(JsonObject complete, string service) => Hash(JsonSerializer.Serialize(Mounts(complete, service)
        .Select(mount => mount with { Source = mount.Type == "volume" ? VolumeName(complete, mount.Source) : mount.Source })
        .OrderBy(mount => mount.Kind, StringComparer.Ordinal).ThenBy(mount => mount.Target, StringComparer.Ordinal).ToArray(), Json));

    // Docker destinations use Linux path semantics even when setup runs on another
    // host OS. Resolve segments rather than comparing arbitrary string prefixes.
    private static string NormalizeDestination(string path)
    {
        if (!path.StartsWith('/') || path.Any(char.IsControl) || path.Contains('\\'))
            throw new AgentClientValidationException("HOST_MOUNT_PATH_INVALID: Restore an explicit absolute Linux mount destination before approval.");
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment == ".") continue;
            else if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        return "/" + string.Join('/', segments);
    }

    private static bool Overlaps(string left, string right) => left == right || left == "/" || right == "/" ||
        left.StartsWith(right + "/", StringComparison.Ordinal) || right.StartsWith(left + "/", StringComparison.Ordinal);

    private static string VolumeDestination(JsonNode? mount)
    {
        if (mount is JsonObject map) return NormalizeDestination(map["target"]?.GetValue<string>() ?? "");
        if (mount is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var parts = text.Split(':');
            var options = parts.Length > 1 && !parts[^1].StartsWith('/');
            return NormalizeDestination(parts[parts.Length - (options ? 2 : 1)]);
        }
        throw new AgentClientValidationException("HOST_MOUNT_INVALID: Restore a valid bounded Compose volume before approval.");
    }

    private static IEnumerable<DeploymentMount> Mounts(JsonObject complete, string service, bool actualName = false)
    {
        foreach (var kind in new[] { "volumes", "configs", "secrets" })
        {
            if (complete["services"]?[actualName ? service : ServiceName(complete, service)]?[kind] is not JsonArray mounts) continue;
            foreach (var node in mounts)
            {
                string source, target, type;
                bool readOnly;
                if (node is JsonObject mount)
                {
                    source = mount["source"]?.GetValue<string>() ?? "";
                    target = mount["target"]?.GetValue<string>() ?? (kind == "configs" ? "/" + source : kind == "secrets" ? "/run/secrets/" + source : "");
                    type = mount["type"]?.GetValue<string>() ?? (kind == "volumes" ? "volume" : kind);
                    readOnly = kind != "volumes" || mount["read_only"]?.GetValue<bool>() == true;
                }
                else if (node is JsonValue value && value.TryGetValue<string>(out var shortMount))
                {
                    if (kind == "volumes")
                    {
                        var parts = shortMount.Split(':');
                        var options = parts.Length > 1 && !parts[^1].StartsWith('/') ? parts[^1].Split(',') : [];
                        var targetIndex = parts.Length - (options.Length > 0 ? 2 : 1);
                        target = parts[targetIndex];
                        source = targetIndex == 0 ? "" : string.Join(':', parts.Take(targetIndex));
                        type = source.StartsWith('/') || source.StartsWith('.') ? "bind" : "volume";
                        readOnly = options.Contains("ro", StringComparer.Ordinal);
                    }
                    else { source = shortMount; target = kind == "configs" ? "/" + source : "/run/secrets/" + source; type = kind; readOnly = true; }
                }
                else throw new AgentClientValidationException("HOST_MOUNT_INVALID: Restore a valid bounded Compose mount before approval.");
                yield return new(kind, source, NormalizeDestination(target), type, readOnly);
            }
        }
    }

    private static void ValidateMountCollisions(JsonObject complete, Action<string, string, string, string, string> fail, JsonArray? migrations = null)
    {
        var reserved = new[] { RuntimeDirectory, "/output", "/var/lib/rateldesk/http-client" };
        foreach (var service in new[] { "mcp-http", "mcp-config-init" })
        {
            var mounts = Mounts(complete, service).ToArray();
            foreach (var mount in mounts)
            {
                if (mount.Kind == "volumes" && mount.Target == RuntimeDirectory && string.IsNullOrWhiteSpace(mount.Source))
                {
                    fail("HOST_RUNTIME_CONFIG_STORAGE_REQUIRED", service, "protected runtime volume", "Anonymous runtime storage cannot be bound to an approved generation. Select an explicit named private persistent configuration volume before approval.", "complete Compose mount destinations");
                    continue;
                }
                if (migrations?.Any(item => item?["service"]?.GetValue<string>() == service && item["kind"]?.GetValue<string>() == mount.Kind && item["source"]?.GetValue<string>() == mount.Source && item["target"]?.GetValue<string>() == mount.Target) == true) continue;
                foreach (var root in reserved.Where(root => Overlaps(root, mount.Target)))
                {
                    // A parent volume can contain a file or symlink at the child
                    // destination. Permit it only after bounded read-only inspection
                    // confirms every existing child component is a directory.
                    var safeAncestor = mount.Kind == "volumes" && mount.Type == "volume" && !mount.ReadOnly && mount.Target != "/" &&
                        root.StartsWith(mount.Target + "/", StringComparison.Ordinal) &&
                        complete["x-rateldesk-mount-ancestor-checks"] is JsonArray checks && checks.Any(check =>
                            check?["service"]?.GetValue<string>() == service && check["source"]?.GetValue<string>() == VolumeName(complete, mount.Source) &&
                            check["target"]?.GetValue<string>() == mount.Target && check["children"] is JsonArray children && children.Any(child => child?.GetValue<string>() == root));
                    var known = mount.Kind == "volumes" && !string.IsNullOrWhiteSpace(mount.Source) && mount.Target == root &&
                        (root == RuntimeDirectory ? mount.Type == "volume" && mount.ReadOnly == (service == "mcp-http") : !mount.ReadOnly &&
                            complete["x-rateldesk-storage"]?[root == "/output" ? "host" : "client"] is { } store &&
                            store["type"]?.GetValue<string>() == mount.Type &&
                            (mount.Type == "volume" ? complete["volumes"]?[mount.Source]?["name"]?.GetValue<string>() ?? mount.Source : mount.Source) == store["source"]?.GetValue<string>());
                    if (!known && !safeAncestor)
                        fail("HOST_MOUNT_COLLISION", service, mount.Kind + ":" + root,
                            "An unrecognized mount overlaps reviewed setup storage or the protected runtime configuration. Resolve this owning mapping before approval; unrelated mounts are retained.", "complete Compose mount destinations");
                }
            }
            foreach (var root in reserved)
                if (mounts.Count(mount => mount.Target == root) > 1)
                    fail("HOST_MOUNT_COLLISION", service, root, "Multiple effective mounts select the same reviewed setup destination.", "complete Compose mount destinations");
        }
    }

    private static IReadOnlyList<DeploymentDiagnostic> PrepareLegacyMigration(JsonObject overlay, JsonObject source, HttpMcpHostingSettings settings)
    {
        var legacy = Mounts(source, "mcp-http").Where(mount => mount.Kind == "configs" && mount.Target == RuntimeConfiguration).ToArray();
        if (legacy.Length == 0) return [];
        DeploymentDiagnostic Failure() => new("HOST_LEGACY_CONFIG_SOURCE", "mcp-http", "selected outbound configuration", "protected provider capture",
            "The legacy runtime mapping requires one captured, valid selected outbound provider matching the reviewed identity before replacement.",
            "Restore the exact readable selected config source and its private identity references, then run host setup for a new review.");
        try
        {
            using var configuration = Configuration(source, "mcp-http");
            var env = EnvironmentFor(source, "mcp-http");
            var selected = configuration["Helpdesk:Mcp:ConfigurationPath"];
            if (string.IsNullOrWhiteSpace(selected)) selected = env.GetValueOrDefault("RATELDESK_MCP_CONFIG");
            var files = source["x-rateldesk-protected-configuration"]?["mcp-http"]?.AsArray();
            var captured = files?.Where(file => file?["path"]?.GetValue<string>() is { } path && NormalizeDestination(path) == RuntimeConfiguration).ToArray();
            if (legacy.Length != 1 || selected is null || NormalizeDestination(selected) != RuntimeConfiguration || captured is not { Length: 1 } ||
                source["configs"]?[legacy[0].Source] is not JsonObject || captured[0]?["content"]?.GetValue<string>() is not { } content || string.IsNullOrWhiteSpace(content)) return [Failure()];
            // Capture decodes UTF-8 with utf-8-sig while retaining the raw file
            // digest. Reconstruct exactly that optional BOM without changing
            // the source hash or accepting any other byte transformation.
            if (captured[0]?["sha256"]?.GetValue<string>() is { } hash &&
                !string.Equals(Hash(content), hash, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Hash("\uFEFF" + content), hash, StringComparison.OrdinalIgnoreCase)) return [Failure()];
            var actual = Outbound(source, configuration, env);
            var prior = actual.Resolve();
            var reviewed = settings.Outbound?.Resolve();
            if (reviewed is null || prior != reviewed || configuration["Helpdesk:Mcp:AuthenticationMode"] is { } mode && mode != settings.AuthenticationMode) return [Failure()];
            overlay[Migrations] = new JsonArray(new JsonObject { ["service"] = "mcp-http", ["kind"] = "configs", ["source"] = legacy[0].Source, ["target"] = RuntimeConfiguration });
            return [];
        }
        catch (Exception error) when (error is InvalidOperationException or AgentClientValidationException or FormatException or JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { return [Failure()]; }
    }

    private static JsonObject Materialize(JsonObject source, JsonObject overlay, HttpMcpHostingSettings settings, bool replaceOutbound = true)
    {
        var proposed = source.DeepClone().AsObject();
        if (overlay[Migrations] is JsonArray migrations)
            foreach (var migration in migrations)
            {
                var service = migration!["service"]!.GetValue<string>(); var kind = migration["kind"]!.GetValue<string>();
                var selected = Mounts(proposed, service).Where(mount => mount.Kind == kind).ToArray();
                var mappings = proposed["services"]![ServiceName(proposed, service)]![kind]!.AsArray();
                for (var index = mappings.Count - 1; index >= 0; index--)
                    if (selected[index].Source == migration["source"]!.GetValue<string>() && selected[index].Target == migration["target"]!.GetValue<string>()) mappings.RemoveAt(index);
            }
        Merge(proposed, overlay);
        if (replaceOutbound) ProposedOutbound(proposed, settings);
        return proposed;
    }

    private static void ValidateRoutes(IConfiguration configuration, HttpMcpHostingSettings expected, Action<string, string, string, string, string> fail)
    {
        foreach (var (path, backend) in new[] { ("/api/v1/{**catch-all}", expected.ApiBaseUrl), ("/mcp/{**catch-all}", expected.McpDestination) })
        {
            var routes = configuration.GetSection("ReverseProxy:Routes").GetChildren().Where(route => route["Match:Path"] == path).ToArray();
            if (routes.Length != 1) { fail("HOST_ROUTE_INVALID", "web", path, "A unique effective route must serve the reviewed API/MCP path.", "effective YARP routes"); continue; }
            var cluster = configuration.GetSection("ReverseProxy:Clusters:" + routes[0]["ClusterId"]);
            var destinations = cluster.GetSection("Destinations").GetChildren().Select(destination => destination["Address"]).ToArray();
            if (destinations.Length == 0 || destinations.Any(destination => !Same(destination, backend)))
                fail("HOST_ROUTE_DESTINATION", "web", path, "Effective YARP destinations differ from the reviewed pinned destination.", "effective YARP clusters");
        }
    }

    private static void ProposedOutbound(JsonObject complete, HttpMcpHostingSettings settings)
    {
        complete["x-rateldesk-protected-configuration"] ??= new JsonObject();
        var files = complete["x-rateldesk-protected-configuration"]!["mcp-http"] as JsonArray ?? new JsonArray();
        var remaining = files.Where(file => file?["path"]?.GetValue<string>() is not { } path || NormalizeDestination(path) != RuntimeConfiguration).Select(file => file?.DeepClone()).ToList();
        remaining.Add(new JsonObject { ["path"] = "/run/rateldesk-mcp/config.json", ["source"] = "reviewed protected outbound export",
            ["content"] = JsonSerializer.Serialize(settings.AuthenticationMode == "gateway" ? AgentClientConfiguration.Empty with { ApiBaseUrl = settings.ApiBaseUrl, CredentialMode = "gateway" } : settings.Outbound, AgentClientConfiguration.JsonOptions) });
        complete["x-rateldesk-protected-configuration"]!["mcp-http"] = new JsonArray(remaining.ToArray());
    }

    private static AgentClientConfiguration Outbound(JsonObject complete, IConfiguration configuration, Dictionary<string, string?> environment)
    {
        // Materialize no protected files in the operator's profile. The actual resolver gets a private transient snapshot.
        var configuredPath = configuration["Helpdesk:Mcp:ConfigurationPath"];
        var selected = string.IsNullOrWhiteSpace(configuredPath) ? environment.GetValueOrDefault("RATELDESK_MCP_CONFIG") : configuredPath;
        if (!string.IsNullOrWhiteSpace(selected)) selected = NormalizeDestination(selected);
        string? candidate = null;
        if (complete["x-rateldesk-protected-configuration"]?["mcp-http"] is JsonArray files)
        {
            var matches = files.Where(file => file?["path"]?.GetValue<string>() is { } path && NormalizeDestination(path) == selected).ToArray();
            if (matches.Length > 1) throw new AgentClientValidationException("HOST_OUTBOUND_PROVIDER_CONFLICT: The selected protected MCP configuration has conflicting captured sources. Restore one exact serving source before approval.");
            candidate = matches.FirstOrDefault()?["content"]?.GetValue<string>();
        }
        if (!string.IsNullOrWhiteSpace(selected) && candidate is null)
            throw new AgentClientValidationException("HOST_OUTBOUND_PROVIDER_MISSING: The selected protected MCP configuration is not in the reviewed snapshot. Restore its exact serving source before approval.");
        string? temporary = null;
        try
        {
            if (candidate is not null)
            {
                temporary = Path.Combine(Path.GetTempPath(), "rateldesk-preflight-" + Guid.NewGuid().ToString("N"), "outbound.json");
                SecureConfigurationFiles.WriteAtomic(temporary, candidate, "");
            }
            var safeEnvironment = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
            safeEnvironment.Remove("RATELDESK_MCP_CONFIG");
            return AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, temporary,
                configuration: configuration, environment: key => safeEnvironment.GetValueOrDefault(key)).Configuration;
        }
        finally { if (temporary is not null) { var directory = Path.GetDirectoryName(temporary)!; if (Directory.Exists(directory)) Directory.Delete(directory, true); } }
    }

    private static ConfigurationRoot Configuration(JsonObject complete, string service)
    {
        var builder = new ConfigurationBuilder();
        if (complete["x-rateldesk-protected-configuration"]?[service] is JsonArray files)
            foreach (var file in files)
                if (file?["path"]?.GetValue<string>() is { } path && Path.GetFileName(path).StartsWith("appsettings", StringComparison.Ordinal) && file["content"]?.GetValue<string>() is { } content)
                    builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        var environment = EnvironmentFor(complete, service);
        builder.AddInMemoryCollection(environment.Select(entry => new KeyValuePair<string, string?>(entry.Key.Replace("__", ":", StringComparison.Ordinal), entry.Value)));
        var entrypoint = complete["services"]?[ServiceName(complete, service)]?["entrypoint"];
        var imageEntrypoint = complete["x-rateldesk-image-entrypoints"]?[service];
        if (entrypoint is not null && !JsonNode.DeepEquals(entrypoint, imageEntrypoint))
        {
            var parts = entrypoint as JsonArray;
            var assembly = service switch { "api" => "Helpdesk.API.dll", "web" => "HelpDesk.NewWeb.dll", _ => "Helpdesk.Mcp.Http.dll" };
            if (parts is not { Count: 2 } || parts[0]?.GetValue<string>() != "dotnet" || parts[1]?.GetValue<string>() != assembly)
                throw new AgentClientValidationException("HOST_DISPATCH_UNSUPPORTED: Preserve the image's serving entrypoint; review custom shell wrappers with the deployment owner before setup.");
        }
        var command = complete["services"]?[ServiceName(complete, service)]?["command"] ?? complete["x-rateldesk-image-commands"]?[service];
        if (command is not null)
        {
            if (command is not JsonArray arguments || arguments.Any(argument => argument is not JsonValue) || arguments.Count > 128)
                throw new AgentClientValidationException("HOST_COMMAND_UNSUPPORTED: Use an explicit bounded argument array for the serving process.");
            var values = arguments.Select(argument => argument!.GetValue<string>()).ToArray();
            if (values.Any(value => value.Length > 4096 || value.Any(char.IsControl)) || values.FirstOrDefault() is { } first && !first.StartsWith("--", StringComparison.Ordinal) ||
                service == "mcp-http" && values.Any(value => value is "--help" or "--version"))
                throw new AgentClientValidationException("HOST_COMMAND_UNSUPPORTED: Setup cannot approve a non-serving command or shell dispatch.");
            builder.AddCommandLine(values);
        }
        var configuration = (ConfigurationRoot)builder.Build();
        string? commandRoot = null;
        foreach (var provider in configuration.Providers.Where(provider => provider.GetType().Name == "CommandLineConfigurationProvider"))
            if (provider.TryGet("contentRoot", out var value)) commandRoot = value;
        var contentRoot = commandRoot ?? environment.GetValueOrDefault("DOTNET_CONTENTROOT") ??
            environment.GetValueOrDefault("ASPNETCORE_CONTENTROOT") ?? environment.GetValueOrDefault("contentRoot") ??
            complete["services"]?[ServiceName(complete, service)]?["working_dir"]?.GetValue<string>() ??
            complete["x-rateldesk-image-working-directories"]?[service]?.GetValue<string>() ?? "/app";
        if (contentRoot.TrimEnd('/') != "/app")
        {
            configuration.Dispose();
            throw new AgentClientValidationException("HOST_PROVIDER_ROOT_UNSUPPORTED: Alternate contentRoot or working_dir can select unobserved appsettings. Restore the canonical /app serving root and explicit private /app/appsettings files before setup approval or verification.");
        }
        return configuration;
    }

    private static IReadOnlyList<HostDeploymentSetting> Review(JsonObject proposed, HttpMcpHostingSettings settings, string sourceHash)
    {
        string Source(string service, params string[] keys) => ReviewSource(proposed, service, keys);
        var values = new List<HostDeploymentSetting>
        {
            new("Public Web origin", settings.PublicOrigin, Source("web", "WebGateway:PublicOrigin")),
            new("Canonical MCP resource", settings.PublicResourceUri, Source("mcp-http", "Helpdesk:Mcp:PublicResourceUri")),
            new("Pairing public origin", settings.PublicOrigin, Source("api", "IntegrationCredentials:Pairing:PublicOrigin")),
            new("Web API backend", settings.ApiBaseUrl, Source("web", "ApiBaseUrl")),
            new("Pinned API backend", settings.ApiBaseUrl, Source("mcp-http", "Helpdesk:Mcp:ExpectedApiBaseUrl", "RATELDESK_MCP_EXPECTED_API_BASE_URL", $"RATELDESK_MCP_{settings.Instance.ToUpperInvariant()}_API_BASE_URL")),
            new("Web MCP destination", settings.McpDestination, Source("web", "WebGateway:Mcp:Destination")),
            new("Instance", settings.Instance, Source("mcp-http", "Helpdesk:Mcp:Instance", "RATELDESK_MCP_INSTANCE")),
            new("Authentication mode", settings.AuthenticationMode, Source("mcp-http", "Helpdesk:Mcp:AuthenticationMode")),
            new("Browser origins", string.Join(", ", settings.AllowedOrigins), Source("mcp-http", "Helpdesk:Mcp:AllowedOrigins")),
            new("MCP listener", settings.Listener, Source("mcp-http", "urls", "DOTNET_URLS", "ASPNETCORE_URLS")),
            new("Delegation deadline", settings.DelegationTimeoutSeconds + " seconds", Source("mcp-http", "Helpdesk:Mcp:DelegationTimeoutSeconds")),
            new("Gateway identity", settings.AuthenticationMode == "gateway" ? "API-issued request-scoped execution tokens; no stored gateway credential" : "Separately configured target-bound external identity", Source("mcp-http", "Helpdesk:Mcp:AuthenticationMode")),
            new("Downstream identity mode", settings.Outbound?.CredentialMode ?? "", ReviewOutboundSource(proposed, "CredentialMode", "CREDENTIAL_MODE")),
            new("Downstream identity target", settings.Outbound?.ApiBaseUrl ?? settings.ApiBaseUrl, ReviewOutboundSource(proposed, "ApiBaseUrl", "API_BASE_URL")),
            new("Provider precedence", "Serving: command arguments > effective Compose/image environment > ordered appsettings. Outbound: command arguments > canonical environment > legacy aliases > ordered appsettings > protected outbound file.", "Actual serving IConfiguration providers and AgentClientConfigurationResolver"),
            new("Source generation", "Bound to this private checkpoint", "all ordered source bytes, interpolation, image defaults and protected provider revisions")
        };
        if (settings.AuthenticationMode == "authentik") values.AddRange(new[]
        {
            new HostDeploymentSetting("Authentik issuer", settings.AuthentikAuthority ?? "", Source("mcp-http", "Authentication:AuthentikMcp:Authority")),
            new HostDeploymentSetting("Authentik audience", settings.AuthentikAudience ?? "", Source("mcp-http", "Authentication:AuthentikMcp:Audience")),
            new HostDeploymentSetting("Required scopes", string.Join(", ", settings.RequiredScopes), Source("mcp-http", "Authentication:AuthentikMcp:RequiredScopes")),
            new HostDeploymentSetting("Required groups", string.Join(", ", settings.RequiredGroups), Source("mcp-http", "Authentication:AuthentikMcp:RequiredGroups"))
        });
        foreach (var service in ServiceNames(proposed))
        {
            var definition = proposed["services"]![service]!;
            var references = Mounts(proposed, service, actualName: true).Select(mount => $"{mount.Kind}:{mount.Source} → {mount.Target} ({(mount.ReadOnly ? "read-only" : "read-write")})");
            values.Add(new(service + " mount/config/secret references", string.Join("; ", references), "reviewed ordered Compose sources; retained unless explicitly changed"));
            string Keys(string key) => definition[key] is JsonObject map ? string.Join(", ", map.Select(entry => entry.Key)) : definition[key] is JsonArray array ? string.Join(", ", array.Select(entry => entry?.ToString())) : "absent";
            values.Add(new(service + " ports/networks/dependencies", "ports: " + Keys("ports") + "; networks: " + Keys("networks") + "; depends_on: " + Keys("depends_on"), "reviewed service configuration; runtime status shown separately"));
        }
        if (proposed["x-rateldesk-source-candidates"] is JsonArray candidates)
            foreach (var candidate in candidates.Take(8))
                if (candidate?["safeDiff"]?.GetValue<string>() is { } diff)
                    values.Add(new("Minimal redacted source diff", diff.Replace("\n", " | ", StringComparison.Ordinal), candidate["source"]?.ToString() ?? "owning source"));
        foreach (var kind in new[] { "host", "client" })
            if (proposed["x-rateldesk-storage"]?[kind] is { } storage)
                values.Add(new(kind + " persistent store", $"{storage["type"]}: {storage["source"]} at {storage["target"]}; private, writable UID {storage["uid"]}", "Docker host mount preflight"));
        if (proposed["x-rateldesk-source-files"] is JsonArray files)
            foreach (var file in files.Take(Math.Min(16, 48 - values.Count))) values.Add(new("Source file", file?["path"]?.ToString() ?? "", "ordered Compose, env_file or protected provider"));
        return values.Take(48).Select(value => value with { Value = TerminalText.Sanitize(value.Value, 2048), Source = TerminalText.Sanitize(value.Source, 256) }).ToArray();
    }

    private static string ReviewSource(JsonObject complete, string service, params string[] keys)
    {
        try
        {
            using var configuration = Configuration(complete, service);
            foreach (var key in keys)
            {
                var children = configuration.GetSection(key).GetChildren().Select(child => child.Path).ToArray();
                string[] selected = configuration[key] is not null ? [key] : children;
                if (selected.Length == 0) continue;
                return string.Join("; ", selected.Select(path => ReviewWinningProvider(complete, service, configuration, path)).Distinct(StringComparer.Ordinal));
            }
            return service + ": serving default (no explicit winning provider)";
        }
        catch (Exception error) when (error is AgentClientValidationException or InvalidOperationException or FormatException or JsonException or ArgumentException)
        { return service + ": invalid effective providers; resolve preflight diagnostics before approval"; }
    }

    private static string ReviewWinningProvider(JsonObject complete, string service, ConfigurationRoot configuration, string key)
    {
        var providers = configuration.Providers.ToArray();
        for (var index = providers.Length - 1; index >= 0; index--)
        {
            if (!providers[index].TryGet(key, out var value) || value is null) continue;
            var type = providers[index].GetType().Name;
            if (type == "CommandLineConfigurationProvider")
                return complete["services"]?[ServiceName(complete, service)]?["command"] is not null
                    ? $"services.{service}.command; highest command-line precedence"
                    : $"{service} image Config.Cmd; highest command-line precedence";
            if (type == "MemoryConfigurationProvider")
            {
                var canonical = key.Replace(":", "__", StringComparison.Ordinal);
                var environment = complete["services"]?[ServiceName(complete, service)]?["environment"];
                bool Matches(string candidate) => string.Equals(candidate.Replace(":", "__", StringComparison.Ordinal), canonical, StringComparison.OrdinalIgnoreCase);
                var inline = environment is JsonObject map && map.Any(entry => Matches(entry.Key) && entry.Value is not null) ||
                    environment is JsonArray list && list.Any(item => item?.GetValue<string>() is { } entry && entry.IndexOf('=') is var separator && separator > 0 && Matches(entry[..separator]));
                return inline
                    ? $"ordered owning Compose sources / services.{ServiceName(complete, service)}.environment; overrides env_file, image Config.Env and appsettings"
                    : $"{service} image Config.Env; overrides ordered appsettings";
            }
            if (type == "JsonStreamConfigurationProvider" && complete["x-rateldesk-protected-configuration"]?[service] is JsonArray files)
            {
                var appsettings = files.Where(file => file?["path"]?.GetValue<string>() is { } path && Path.GetFileName(path).StartsWith("appsettings", StringComparison.Ordinal)).ToArray();
                if (index < appsettings.Length)
                    return $"{service}: {appsettings[index]!["path"]}; ordered appsettings provider {index + 1}, below command/environment";
            }
            return service + ": " + type;
        }
        return service + ": serving default";
    }

    private static string ReviewOutboundSource(JsonObject complete, string field, string alias)
    {
        try
        {
            using var configuration = Configuration(complete, "mcp-http");
            var key = "Helpdesk:Mcp:Outbound:" + field;
            if (configuration.Providers.Reverse().Any(provider => provider.GetType().Name == "CommandLineConfigurationProvider" && provider.TryGet(key, out var value) && value is not null))
                return ReviewWinningProvider(complete, "mcp-http", configuration, key);
            var environment = EnvironmentFor(complete, "mcp-http");
            if (environment.ContainsKey(key.Replace(":", "__", StringComparison.Ordinal))) return ReviewSource(complete, "mcp-http", key);
            if (environment.ContainsKey("RATELDESK_MCP_" + alias)) return ReviewSource(complete, "mcp-http", "RATELDESK_MCP_" + alias);
            if (configuration[key] is not null) return ReviewWinningProvider(complete, "mcp-http", configuration, key);
            return "reviewed protected outbound export /run/rateldesk-mcp/config.json; below appsettings and environment";
        }
        catch (Exception error) when (error is AgentClientValidationException or InvalidOperationException or FormatException or JsonException or ArgumentException)
        { return "mcp-http: invalid effective outbound providers; resolve preflight diagnostics before approval"; }
    }

    private static Dictionary<string, string?> EnvironmentFor(JsonObject complete, string service)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var proxies = new Dictionary<string, string?>(StringComparer.Ordinal);
        static bool Proxy(string key) => new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }.Contains(key, StringComparer.OrdinalIgnoreCase);
        void Add(string key, string? value, bool rejectDuplicate)
        {
            key = key.Replace(":", "__", StringComparison.Ordinal);
            if (Proxy(key)) { if (value is null) proxies.Remove(key); else proxies[key] = value; return; }
            if (rejectDuplicate && result.ContainsKey(key)) throw new AgentClientValidationException("Duplicate canonical environment keys require correction at their source.");
            result[key] = value;
        }
        if (complete["x-rateldesk-image-environment"]?[service] is JsonArray image)
            foreach (var item in image) { var entry = item!.GetValue<string>(); var separator = entry.IndexOf('='); if (separator > 0) Add(entry[..separator], entry[(separator + 1)..], false); }
        var inline = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (complete["services"]?[ServiceName(complete, service)]?["environment"] is JsonObject map)
            foreach (var entry in map)
            {
                var key = entry.Key.Replace(":", "__", StringComparison.Ordinal);
                if (Proxy(key)) { Add(key, entry.Value?.ToString(), false); continue; }
                if (!inline.TryAdd(key, entry.Value?.ToString())) throw new AgentClientValidationException("Duplicate canonical environment keys require correction at their source.");
            }
        else if (complete["services"]?[ServiceName(complete, service)]?["environment"] is JsonArray list)
            foreach (var item in list)
            {
                var entry = item!.GetValue<string>(); var separator = entry.IndexOf('='); var key = (separator < 0 ? entry : entry[..separator]).Replace(":", "__", StringComparison.Ordinal);
                if (Proxy(key)) { Add(key, separator < 0 ? null : entry[(separator + 1)..], false); continue; }
                if (!inline.TryAdd(key, separator < 0 ? null : entry[(separator + 1)..])) throw new AgentClientValidationException("Duplicate canonical environment keys require correction at their source.");
            }
        foreach (var entry in inline) { if (entry.Value is null) result.Remove(entry.Key); else Add(entry.Key, entry.Value, false); }
        foreach (var key in proxies.Keys.Select(key => key.ToUpperInvariant()).Distinct(StringComparer.Ordinal))
            result[key] = proxies.GetValueOrDefault(key.ToLowerInvariant()) ?? proxies.GetValueOrDefault(key);
        return result;
    }
    private static bool Relevant(string key) => key.StartsWith("Helpdesk__Mcp__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Authentication__AuthentikMcp__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("WebGateway__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("IntegrationCredentials__Pairing__", StringComparison.OrdinalIgnoreCase) || key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) || key is "ApiBaseUrl" or "ASPNETCORE_URLS" or "RATELDESK_MCP_CONFIG";
    private static bool Same(string? left, string? right) => Uri.TryCreate(left, UriKind.Absolute, out var first) && Uri.TryCreate(right, UriKind.Absolute, out var second) && AgentClientConfigurationValidator.SameTarget(first, second);
    private static IReadOnlyList<string> SemanticChanges(JsonObject source, JsonObject proposed, JsonObject overlay, bool legacy = false)
    {
        var changes = new List<string>();
        if (overlay[Migrations] is JsonArray { Count: > 0 })
            changes.Add("Migrate the selected legacy config-file mapping at /run/rateldesk-mcp/config.json to the validated protected initializer and read-only runtime volume; preserve the reviewed identity and shared config definitions.");
        foreach (var service in Services)
        {
            var before = EnvironmentFor(source, service);
            ConfigurationRoot? previous = null;
            try { previous = Configuration(source, service); } catch (Exception error) when (error is InvalidOperationException or AgentClientValidationException or FormatException or JsonException or ArgumentException) { }
            using var previousLifetime = previous;
            foreach (var entry in EnvironmentFor(proposed, service).Where(entry => Relevant(entry.Key)))
            {
                var canonical = entry.Key.Replace("__", ":", StringComparison.Ordinal);
                var prior = previous?[canonical] ?? before.GetValueOrDefault(entry.Key);
                if (prior != entry.Value) changes.Add(service + ": " + canonical + ": " + DiffValue(entry.Key, prior) + " → " + DiffValue(entry.Key, entry.Value));
            }
        }
        if (legacy) changes.Add("Advanced initializer alternative: separate protected persistent host and personal client storage; API receives no personal client secrets.");
        return changes.Take(32).ToArray();
    }
    private static string DiffValue(string key, string? value)
    {
        if (value is null) return "<unset>";
        if (value.Length == 0) return "<cleared>";
        if (key.Contains("Credential", StringComparison.OrdinalIgnoreCase) || key.Contains("Password", StringComparison.OrdinalIgnoreCase) || key.Contains("Secret", StringComparison.OrdinalIgnoreCase)) return "[redacted]";
        if (key.Contains("Origin", StringComparison.OrdinalIgnoreCase) || key.Contains("Url", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Uri", StringComparison.OrdinalIgnoreCase) || key.Contains("Authority", StringComparison.OrdinalIgnoreCase) || key.Contains("Destination", StringComparison.OrdinalIgnoreCase) || key.Contains("Audience", StringComparison.OrdinalIgnoreCase))
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return "[invalid configured value]";
        return TerminalText.Sanitize(value, 160);
    }
    internal static JsonObject Parse(string text)
    { try { return JsonNode.Parse(text, documentOptions: new() { MaxDepth = 64 })?.AsObject() ?? throw new JsonException(); } catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new AgentClientValidationException("The bounded effective deployment snapshot is invalid. Run the Docker host launcher again."); } }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Canonical(JsonNode? value) => value switch
    {
        JsonObject map => "{" + string.Join(',', map.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => JsonSerializer.Serialize(entry.Key) + ":" + Canonical(entry.Value))) + "}",
        JsonArray array => "[" + string.Join(',', array.Select(Canonical)) + "]", _ => value?.ToJsonString() ?? "null"
    };
    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var entry in patch)
        {
            if (entry.Key == "environment" && entry.Value is JsonObject && target[entry.Key] is JsonArray environment)
            {
                var normalized = new JsonObject();
                foreach (var item in environment)
                {
                    var value = item!.GetValue<string>(); var separator = value.IndexOf('=');
                    var key = separator < 0 ? value : value[..separator];
                    normalized[key] = separator < 0 ? null : value[(separator + 1)..];
                }
                target[entry.Key] = normalized;
            }
            if (entry.Value is JsonObject map && target[entry.Key] is JsonObject existing) Merge(existing, map);
            else if (entry.Key == "volumes" && entry.Value is JsonArray mounts && target[entry.Key] is JsonArray previous)
            {
                var merged = previous.Select(item => item?.DeepClone()).ToList();
                foreach (var mount in mounts) { merged.RemoveAll(item => VolumeDestination(item) == VolumeDestination(mount)); merged.Add(mount?.DeepClone()); }
                target[entry.Key] = new JsonArray(merged.ToArray());
            }
            else target[entry.Key] = entry.Value?.DeepClone();
        }
    }
}
