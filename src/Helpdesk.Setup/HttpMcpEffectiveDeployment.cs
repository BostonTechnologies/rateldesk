using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Http.Configuration;
using Helpdesk.Shared.DTOs.Auth;
using HelpDesk.NewWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Helpdesk.Setup;

public sealed record DeploymentDiagnostic(string Code, string Layer, string Key, string Source, string Message, string NextAction);
public sealed record HttpMcpDeploymentProposal(HttpMcpHostingSettings Settings, string SourceHash, string ProposalHash,
    JsonObject Overlay, IReadOnlyList<DeploymentDiagnostic> Diagnostics, IReadOnlyList<string> Changes)
{
    public bool Valid => Diagnostics.Count == 0;
    public string? SnapshotPath { get; init; }
    public IReadOnlyList<HostDeploymentSetting> ReviewSettings { get; init; } = [];
    public HostDeploymentProposalRequest Approval => new("RatelDesk HTTP MCP deployment", ProposalHash, SourceHash,
        Settings.PublicOrigin, Settings.PublicResourceUri, Settings.Instance, Settings.AuthenticationMode,
        Settings.ApiBaseUrl, Settings.McpDestination, Changes) { ReviewSettings = ReviewSettings };
}

/// <summary>Consumes Docker Compose's rendered result; binds it through the actual serving resolvers.</summary>
public static class HttpMcpEffectiveDeployment
{
    public const string EffectiveSourceVariable = "RATELDESK_SETUP_EFFECTIVE_COMPOSE_SOURCE";
    public const string RuntimeSourceVariable = "RATELDESK_SETUP_RUNTIME_ENVIRONMENT_SOURCE";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Services = ["api", "web", "mcp-http"];

    public static HttpMcpDeploymentProposal Create(HttpMcpHostingSettings settings, string? snapshotPath = null)
    {
        snapshotPath ??= Environment.GetEnvironmentVariable(EffectiveSourceVariable);
        if (string.IsNullOrWhiteSpace(snapshotPath))
            throw new AgentClientValidationException("HOST_SOURCE_REQUIRED: Run the deployment archive's ./setup-http-mcp.sh on the Docker host. A container console cannot inspect or apply its owner's Compose deployment.");
        var text = SecureConfigurationFiles.ReadPrivateDocument(snapshotPath);
        var source = Parse(text);
        settings = settings with { Outbound = settings.AuthenticationMode == "gateway"
            ? AgentClientConfiguration.Empty with { ApiBaseUrl = settings.ApiBaseUrl, CredentialMode = "gateway" }
            : settings.Outbound is { } outbound ? HttpMcpHostingCoordinator.ContainerOutbound(outbound) : null };
        var overlay = HttpMcpHostingCoordinator.ComposeOverlay(settings);
        PrepareOverlay(overlay, source, settings);
        var proposed = source.DeepClone().AsObject();
        Merge(proposed, overlay);
        ProposedOutbound(proposed, settings);
        var diagnostics = Validate(proposed, settings).Concat(HttpMcpHostingCoordinator.Validate(settings).Select(message =>
            new DeploymentDiagnostic("HOST_SETTINGS_INVALID", "proposal", "reviewed hosting settings", "reviewed proposal", message, "Correct the indicated setting in Advanced before approval."))).ToArray();
        var changes = SemanticChanges(source, proposed);
        var sourceHash = Hash(text);
        var proposalHash = Hash(Canonical(proposed) + "\n" + sourceHash);
        return new(settings, sourceHash, proposalHash, overlay, diagnostics, changes)
        { SnapshotPath = snapshotPath, ReviewSettings = Review(proposed, settings, sourceHash) };
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

    public static IReadOnlyList<DeploymentDiagnostic> Validate(JsonObject complete, HttpMcpHostingSettings expected)
    {
        var failures = new List<DeploymentDiagnostic>();
        void Fail(string code, string layer, string key, string message, string source = "effective Compose / application providers") =>
            failures.Add(new(code, layer, key, source, message, "Correct the winning configuration source, then run the host launcher again."));
        foreach (var service in Services)
            if (complete["services"]?[service] is not JsonObject) Fail("HOST_SERVICE_MISSING", service, "services:" + service, "The complete deployment must contain this service.");
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
            ValidateStorage(complete, Fail);
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
        var failures = Validate(runtime, approved.Settings).ToList();
        var source = Parse(SecureConfigurationFiles.ReadPrivateDocument(approved.SnapshotPath ?? Environment.GetEnvironmentVariable(EffectiveSourceVariable)!));
        var proposed = source.DeepClone().AsObject(); Merge(proposed, approved.Overlay);
        ProposedOutbound(proposed, approved.Settings);
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
        var expected = original.DeepClone().AsObject(); Merge(expected, approved.Overlay); ProposedOutbound(expected, approved.Settings);
        var failures = Validate(current, approved.Settings).ToList();
        foreach (var service in Services)
            if (EffectiveFingerprint(current, service) != EffectiveFingerprint(expected, service) ||
                original["x-rateldesk-image-ids"]?[service]?.ToString() != current["x-rateldesk-image-ids"]?[service]?.ToString())
                failures.Add(new("HOST_SOURCE_CHANGED", service, "effective imported configuration", "reviewed complete deployment document", "The imported effective configuration differs from the approved generation.", "Review the current sources before resuming."));
        return failures;
    }

    private static void PrepareOverlay(JsonObject overlay, JsonObject source, HttpMcpHostingSettings settings)
    {
        var services = overlay["services"]!.AsObject();
        var mcp = services["mcp-http"]!.AsObject();
        mcp["image"] = source["services"]?["mcp-http"]?["image"]?.DeepClone();
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
        initializer["image"] = source["services"]?["mcp-http"]?["image"]?.DeepClone();
        initializer["user"] = "0:0";
        initializer["entrypoint"] = new JsonArray("/bin/sh", "-ec");
        initializer["command"] = new JsonArray("mkdir -p /run/rateldesk-mcp && cp /output/mcp-outbound.json /run/rateldesk-mcp/config.json && chown 10001:10001 /run/rateldesk-mcp/config.json && chmod 0400 /run/rateldesk-mcp/config.json");
        var configVolumeName = "mcp-http-config";
        var initMounts = new JsonArray(new JsonObject { ["type"] = "volume", ["source"] = configVolumeName, ["target"] = "/run/rateldesk-mcp" });
        if (mounts.FirstOrDefault(mount => mount?["target"]?.GetValue<string>() == "/output") is { } hostMount) initMounts.Add(hostMount.DeepClone());
        initializer["volumes"] = initMounts;
        overlay["volumes"] ??= new JsonObject(); overlay["volumes"]![configVolumeName] ??= new JsonObject();
        mounts.Add(new JsonObject { ["type"] = "volume", ["source"] = configVolumeName, ["target"] = "/run/rateldesk-mcp", ["read_only"] = true });
        mcp["volumes"] = mounts;
        mcp["depends_on"] = new JsonObject { ["mcp-config-init"] = new JsonObject { ["condition"] = "service_completed_successfully" } };
    }

    private static void ValidateStorage(JsonObject complete, Action<string, string, string, string, string> fail)
    {
        string? ResolveMount(JsonNode? mount)
        {
            var source = mount?["source"]?.GetValue<string>();
            return mount?["type"]?.GetValue<string>() == "volume" ? complete["volumes"]?[source ?? ""]?["name"]?.GetValue<string>() ?? source : source;
        }
        foreach (var kind in new[] { "host", "client" })
        {
            var storage = complete["x-rateldesk-storage"]?[kind];
            if (storage?["private"]?.GetValue<bool>() != true || storage["readOnly"]?.GetValue<bool>() != false ||
                storage["type"]?.GetValue<string>() is not ("bind" or "volume") || string.IsNullOrWhiteSpace(storage["source"]?.GetValue<string>()))
                fail("HOST_STORAGE_REQUIRED", "storage", kind, "Private writable persistent storage must be tested as the serving identity before approval.", "Docker host storage preflight");
            var target = storage?["target"]?.GetValue<string>();
            var source = storage?["source"]?.GetValue<string>();
            if (complete["services"]?["mcp-http"]?["volumes"] is not JsonArray servingMounts ||
                !servingMounts.Any(mount => mount?["target"]?.GetValue<string>() == target && ResolveMount(mount) == source && mount?["read_only"]?.GetValue<bool>() != true))
                fail("HOST_STORAGE_MOUNT_MISSING", "storage", kind, "The serving MCP deployment must mount the reviewed private persistent store writable at its exact setup path.", "effective Compose mounts");
        }
        if (complete["x-rateldesk-storage"]?["host"]?["source"]?.GetValue<string>() == complete["x-rateldesk-storage"]?["client"]?["source"]?.GetValue<string>())
            fail("HOST_STORAGE_ISOLATION", "storage", "client", "Personal client secrets require a separate persistent store.", "Docker host storage preflight");
        var clientSource = complete["x-rateldesk-storage"]?["client"]?["source"]?.GetValue<string>();
        if (complete["services"]?["api"]?["volumes"] is JsonArray apiMounts && apiMounts.Any(mount => ResolveMount(mount) == clientSource))
            fail("HOST_STORAGE_API_EXPOSURE", "storage", "client", "API must not receive personal client storage.", "effective Compose mounts");
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
        var remaining = files.Where(file => file?["path"]?.GetValue<string>() != "/run/rateldesk-mcp/config.json").Select(file => file?.DeepClone()).ToList();
        remaining.Add(new JsonObject { ["path"] = "/run/rateldesk-mcp/config.json", ["source"] = "reviewed protected outbound export",
            ["content"] = JsonSerializer.Serialize(settings.AuthenticationMode == "gateway" ? AgentClientConfiguration.Empty with { ApiBaseUrl = settings.ApiBaseUrl, CredentialMode = "gateway" } : settings.Outbound, AgentClientConfiguration.JsonOptions) });
        complete["x-rateldesk-protected-configuration"]!["mcp-http"] = new JsonArray(remaining.ToArray());
    }

    private static AgentClientConfiguration Outbound(JsonObject complete, IConfiguration configuration, Dictionary<string, string?> environment)
    {
        // Materialize no protected files in the operator's profile. The actual resolver gets a private transient snapshot.
        var selected = configuration["Helpdesk:Mcp:ConfigurationPath"] ?? environment.GetValueOrDefault("RATELDESK_MCP_CONFIG");
        string? candidate = null;
        if (complete["x-rateldesk-protected-configuration"]?["mcp-http"] is JsonArray files)
            candidate = files.FirstOrDefault(file => file?["path"]?.GetValue<string>() == selected)?["content"]?.GetValue<string>();
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
        builder.AddInMemoryCollection(EnvironmentFor(complete, service).Select(entry => new KeyValuePair<string, string?>(entry.Key.Replace("__", ":", StringComparison.Ordinal), entry.Value)));
        var entrypoint = complete["services"]?[service]?["entrypoint"];
        var imageEntrypoint = complete["x-rateldesk-image-entrypoints"]?[service];
        if (entrypoint is not null && !JsonNode.DeepEquals(entrypoint, imageEntrypoint))
        {
            var parts = entrypoint as JsonArray;
            var assembly = service switch { "api" => "Helpdesk.API.dll", "web" => "HelpDesk.NewWeb.dll", _ => "Helpdesk.Mcp.Http.dll" };
            if (parts is not { Count: 2 } || parts[0]?.GetValue<string>() != "dotnet" || parts[1]?.GetValue<string>() != assembly)
                throw new AgentClientValidationException("HOST_DISPATCH_UNSUPPORTED: Preserve the image's serving entrypoint; review custom shell wrappers with the deployment owner before setup.");
        }
        var command = complete["services"]?[service]?["command"] ?? complete["x-rateldesk-image-commands"]?[service];
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
        return (ConfigurationRoot)builder.Build();
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
            new("Source generation", sourceHash, "all ordered source bytes, interpolation, image defaults and protected provider hashes")
        };
        if (settings.AuthenticationMode == "authentik") values.AddRange(new[]
        {
            new HostDeploymentSetting("Authentik issuer", settings.AuthentikAuthority ?? "", Source("mcp-http", "Authentication:AuthentikMcp:Authority")),
            new HostDeploymentSetting("Authentik audience", settings.AuthentikAudience ?? "", Source("mcp-http", "Authentication:AuthentikMcp:Audience")),
            new HostDeploymentSetting("Required scopes", string.Join(", ", settings.RequiredScopes), Source("mcp-http", "Authentication:AuthentikMcp:RequiredScopes")),
            new HostDeploymentSetting("Required groups", string.Join(", ", settings.RequiredGroups), Source("mcp-http", "Authentication:AuthentikMcp:RequiredGroups"))
        });
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
                return complete["services"]?[service]?["command"] is not null
                    ? $"services.{service}.command; highest command-line precedence"
                    : $"{service} image Config.Cmd; highest command-line precedence";
            if (type == "MemoryConfigurationProvider")
            {
                var canonical = key.Replace(":", "__", StringComparison.Ordinal);
                var environment = complete["services"]?[service]?["environment"];
                bool Matches(string candidate) => string.Equals(candidate.Replace(":", "__", StringComparison.Ordinal), canonical, StringComparison.OrdinalIgnoreCase);
                var inline = environment is JsonObject map && map.Any(entry => Matches(entry.Key) && entry.Value is not null) ||
                    environment is JsonArray list && list.Any(item => item?.GetValue<string>() is { } entry && entry.IndexOf('=') is var separator && separator > 0 && Matches(entry[..separator]));
                return inline
                    ? $"reviewed compose.host.json / services.{service}.environment; overrides env_file, image Config.Env and appsettings"
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
        if (complete["services"]?[service]?["environment"] is JsonObject map)
            foreach (var entry in map)
            {
                var key = entry.Key.Replace(":", "__", StringComparison.Ordinal);
                if (Proxy(key)) { Add(key, entry.Value?.ToString(), false); continue; }
                if (!inline.TryAdd(key, entry.Value?.ToString())) throw new AgentClientValidationException("Duplicate canonical environment keys require correction at their source.");
            }
        else if (complete["services"]?[service]?["environment"] is JsonArray list)
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
    private static IReadOnlyList<string> SemanticChanges(JsonObject source, JsonObject proposed)
    {
        var changes = new List<string>();
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
        changes.Add("Separate protected persistent host and personal client storage; API receives no personal client secrets.");
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
                foreach (var mount in mounts) { merged.RemoveAll(item => item?["target"]?.ToString() == mount?["target"]?.ToString()); merged.Add(mount?.DeepClone()); }
                target[entry.Key] = new JsonArray(merged.ToArray());
            }
            else target[entry.Key] = entry.Value?.DeepClone();
        }
    }
}
