using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Helpdesk.AgentClient;

/// <summary>Deployment exports are local configuration, never remote provisioning.</summary>
public sealed record HttpMcpHostingSettings
{
    public string PublicOrigin { get; init; } = "";
    public string ApiBaseUrl { get; init; } = "http://api:8222/";
    public string McpDestination { get; init; } = "http://mcp-http:8223/";
    public string Instance { get; init; } = "local";
    public string AuthenticationMode { get; init; } = "gateway";
    public string[] AllowedOrigins { get; init; } = [];
    public string Listener { get; init; } = "http://+:8223";
    public int DelegationTimeoutSeconds { get; init; } = 20;
    public string? AuthentikAuthority { get; init; }
    public string? AuthentikAudience { get; init; }
    public string[] RequiredScopes { get; init; } = [];
    public string[] RequiredGroups { get; init; } = [];
    public AgentClientConfiguration? Outbound { get; init; }
    public string PublicResourceUri => (PublicOrigin ?? "").TrimEnd('/') + "/mcp";
}

public sealed record HttpMcpHostingExport(IReadOnlyList<string> Files, string ApplyInstructions);
public sealed record HttpMcpHostingInspection(HttpMcpHostingSettings Settings, IReadOnlyDictionary<string, string> ManagedFields);

public static class HttpMcpHostingCoordinator
{
    private static readonly JsonSerializerOptions Json = AgentClientConfiguration.JsonOptions;
    private static readonly string[] Names = ["host-settings.json", "mcp-outbound.json", "compose.host.json", "APPLY.txt"];

    public static HttpMcpHostingSettings Load(string directory)
    {
        var path = Path.Combine(directory, "host-settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<HttpMcpHostingSettings>(SecureConfigurationFiles.ReadText(path, requirePrivate: true), Json)
                ?? throw new AgentClientValidationException("Hosting settings are invalid.");
            var outboundPath = Path.Combine(directory, "mcp-outbound.json");
            return settings with { Outbound = File.Exists(outboundPath) ? new AgentClientConfigurationStore(outboundPath).Load() : null };
        }
        catch (JsonException) { throw new AgentClientValidationException("Hosting settings are invalid."); }
    }

    /// <summary>Inspect dedicated deployment inputs without importing ambient CLI identities.</summary>
    public static HttpMcpHostingInspection Inspect(string directory, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var settings = Load(directory);
        var managed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? Pick(string field, params string[] names)
        {
            foreach (var name in names)
                if (environment(name) is { Length: > 0 } value) { managed[field] = name; return value.Trim(); }
            return null;
        }
        string[] Array(string field, string prefix, string[] fallback, string? alias = null)
        {
            var values = new List<string>();
            for (var i = 0; i < 64; i++)
                if (environment(prefix + "__" + i) is { } value) values.Add(value.Trim());
            if (values.Count > 0) { managed[field] = prefix; return values.ToArray(); }
            var single = alias is null ? null : Pick(field, alias);
            return single is null ? fallback : [single];
        }
        var resource = Pick("PublicOrigin", "Helpdesk__Mcp__PublicResourceUri", "RATELDESK_MCP_PUBLIC_RESOURCE_URI");
        var origin = Pick("PublicOrigin", "WebGateway__PublicOrigin", "IntegrationCredentials__Pairing__PublicOrigin", "RATELDESK_PUBLIC_ORIGIN");
        if (resource is not null)
        {
            if (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || resourceUri.Scheme != "https" || resourceUri.AbsolutePath.TrimEnd('/') != "/mcp"
                || !string.IsNullOrEmpty(resourceUri.UserInfo) || !string.IsNullOrEmpty(resourceUri.Query) || !string.IsNullOrEmpty(resourceUri.Fragment))
                throw new AgentClientValidationException("Managed public resource must be a canonical HTTPS /mcp URL.");
            origin ??= resourceUri.GetLeftPart(UriPartial.Authority);
        }
        var timeout = Pick("DelegationTimeoutSeconds", "Helpdesk__Mcp__DelegationTimeoutSeconds");
        if (timeout is not null && !int.TryParse(timeout, out _)) throw new AgentClientValidationException("Managed delegation timeout must be an integer.");
        settings = settings with
        {
            PublicOrigin = origin ?? settings.PublicOrigin,
            ApiBaseUrl = Pick("ApiBaseUrl", "Helpdesk__Mcp__ExpectedApiBaseUrl", "RATELDESK_MCP_EXPECTED_API_BASE_URL", "RATELDESK_MCP_API_BASE_URL") ?? settings.ApiBaseUrl,
            McpDestination = Pick("McpDestination", "WebGateway__Mcp__Destination", "RATELDESK_MCP_DESTINATION") ?? settings.McpDestination,
            Instance = Pick("Instance", "Helpdesk__Mcp__Instance", "RATELDESK_MCP_INSTANCE") ?? settings.Instance,
            AuthenticationMode = Pick("AuthenticationMode", "Helpdesk__Mcp__AuthenticationMode", "WebGateway__Mcp__AuthenticationMode") ?? settings.AuthenticationMode,
            Listener = Pick("Listener", "ASPNETCORE_URLS") ?? settings.Listener,
            DelegationTimeoutSeconds = timeout is null ? settings.DelegationTimeoutSeconds : int.Parse(timeout),
            AllowedOrigins = Array("AllowedOrigins", "Helpdesk__Mcp__AllowedOrigins", settings.AllowedOrigins, "RATELDESK_MCP_ALLOWED_ORIGIN"),
            AuthentikAuthority = Pick("AuthentikAuthority", "Authentication__AuthentikMcp__Authority", "RATELDESK_MCP_AUTHENTIK_AUTHORITY") ?? settings.AuthentikAuthority,
            AuthentikAudience = Pick("AuthentikAudience", "Authentication__AuthentikMcp__Audience") ?? settings.AuthentikAudience,
            RequiredScopes = Array("RequiredScopes", "Authentication__AuthentikMcp__RequiredScopes", settings.RequiredScopes, "RATELDESK_MCP_REQUIRED_SCOPE"),
            RequiredGroups = Array("RequiredGroups", "Authentication__AuthentikMcp__RequiredGroups", settings.RequiredGroups, "RATELDESK_MCP_REQUIRED_GROUP")
        };
        var outbound = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, environment: environment);
        if (!managed.ContainsKey("ApiBaseUrl"))
        {
            var legacyExpected = Pick("ApiBaseUrl", $"RATELDESK_MCP_{settings.Instance.ToUpperInvariant()}_API_BASE_URL");
            if (legacyExpected is not null) settings = settings with { ApiBaseUrl = legacyExpected };
        }
        if (outbound.Settings.Any(value => value.Source != "default" && value.Source != "unset"))
        {
            settings = settings with { Outbound = outbound.Configuration };
            managed["Outbound"] = outbound.ProfilePath is null ? "dedicated MCP environment" : "isolated MCP configuration/environment";
            if (!managed.ContainsKey("AuthenticationMode") && outbound.ProfilePath is not null)
                settings = settings with { AuthenticationMode = "authentik" }; // Existing HTTP runtime default, never inferred from reachability.
        }
        return new(settings, managed);
    }

    public static IReadOnlyList<string> Validate(HttpMcpHostingSettings settings)
    {
        var failures = new List<string>();
        if (!Origin(settings.PublicOrigin, https: true)) failures.Add("Public origin must be a complete HTTPS origin without credentials, path, query or fragment.");
        if (!Backend(settings.ApiBaseUrl)) failures.Add("API backend must be an explicit HTTP or HTTPS root URL without credentials, query or fragment.");
        if (!Backend(settings.McpDestination)) failures.Add("MCP destination must be an explicit HTTP or HTTPS root URL without credentials, query or fragment.");
        if (!Regex.IsMatch(settings.Instance ?? "", "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)) failures.Add("Instance must be a lowercase named label using letters, digits and hyphens.");
        if (settings.AuthenticationMode is not ("gateway" or "authentik")) failures.Add("Authentication mode must be gateway or authentik.");
        if (settings.AllowedOrigins is not { Length: > 0 } || settings.AllowedOrigins.Any(value => !Origin(value, https: false)) || settings.AllowedOrigins.Distinct(StringComparer.Ordinal).Count() != settings.AllowedOrigins.Length)
            failures.Add("At least one unique HTTP or HTTPS browser origin is required.");
        if (settings.DelegationTimeoutSeconds is < 1 or > 60) failures.Add("Delegation timeout must be between 1 and 60 seconds.");
        var listener = settings.Listener ?? "";
        if (!Regex.IsMatch(listener, "^https?://(\\+|\\*|localhost|127\\.0\\.0\\.1|\\[::\\]|[A-Za-z0-9.-]+):[0-9]{1,5}$", RegexOptions.CultureInvariant)
            || !int.TryParse(listener[(listener.LastIndexOf(':') + 1)..], out var port) || port is < 1 or > 65535)
            failures.Add("Listener must be an explicit HTTP or HTTPS listener with a valid port.");
        if (settings.AuthenticationMode == "gateway")
        {
            if (settings.Outbound is { } outbound && (outbound.CredentialMode != "gateway" || HasSecret(outbound)))
                failures.Add("Gateway hosting uses credentialMode=gateway without a stored downstream credential.");
            if (!string.IsNullOrWhiteSpace(settings.AuthentikAuthority) || !string.IsNullOrWhiteSpace(settings.AuthentikAudience) || settings.RequiredScopes is { Length: > 0 } || settings.RequiredGroups is { Length: > 0 })
                failures.Add("External Authentik ingress settings cannot be mixed with gateway mode.");
        }
        else if (settings.AuthenticationMode == "authentik")
        {
            if (!HttpsUrl(settings.AuthentikAuthority)) failures.Add("External Authentik authority must be an HTTPS URL.");
            if (!string.Equals(settings.AuthentikAudience?.TrimEnd('/'), settings.PublicResourceUri, StringComparison.Ordinal)) failures.Add("Authentik audience must equal the public /mcp resource.");
            if (!Values(settings.RequiredScopes) || !Values(settings.RequiredGroups)) failures.Add("External mode requires non-empty unique scopes and groups.");
            try
            {
                var outbound = settings.Outbound?.Resolve();
                if (outbound is null || outbound.CredentialMode == "gateway") failures.Add("External mode requires a separately configured downstream API identity.");
                if (!Backend(settings.Outbound?.ApiBaseUrl)) failures.Add("External downstream identity requires an explicit root API target matching the reviewed backend.");
            }
            catch (AgentClientValidationException) { failures.Add("External downstream API identity is incomplete or conflicting."); }
        }
        if (settings.Outbound?.ApiBaseUrl is { } target && Backend(target) && Backend(settings.ApiBaseUrl)
            && !string.Equals(new Uri(target).AbsoluteUri.TrimEnd('/'), new Uri(settings.ApiBaseUrl).AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal))
            failures.Add("Outbound API target must equal the reviewed API backend.");
        if (Backend(settings.McpDestination) && new Uri(settings.McpDestination).Host == "mcp-http")
        {
            if (int.TryParse(listener[(listener.LastIndexOf(':') + 1)..], out var listenerPort)
                && new Uri(settings.McpDestination).Port != listenerPort)
                failures.Add("The shared-network MCP destination port must match the selected listener port.");
            if (Uri.TryCreate(listener, UriKind.Absolute, out var listenerUri) && listenerUri.IsLoopback)
                failures.Add("A shared-network MCP destination requires a listener accessible from Web; use an all-interface listener instead of loopback.");
        }
        return failures;
    }

    public static object Preview(HttpMcpHostingSettings settings) => new
    {
        settings.PublicOrigin, settings.PublicResourceUri, settings.ApiBaseUrl, settings.McpDestination,
        settings.Instance, settings.AuthenticationMode, settings.AllowedOrigins, settings.Listener,
        settings.DelegationTimeoutSeconds, settings.AuthentikAuthority, settings.AuthentikAudience,
        settings.RequiredScopes, settings.RequiredGroups,
        outbound = settings.Outbound is null ? null : AgentClientConfigurationResolver.Redact(settings.Outbound),
        managed = "Running environments and read-only mounts require operator apply/recreation."
    };

    public static HttpMcpHostingExport Export(HttpMcpHostingSettings settings, string directory, bool overwrite = false, System.Text.Json.Nodes.JsonObject? composeOverride = null)
    {
        var failures = Validate(settings);
        if (failures.Count > 0) throw new AgentClientValidationException(string.Join(" ", failures));
        directory = Path.GetFullPath(directory);
        var paths = Names.Select(name => Path.Combine(directory, name)).ToArray();
        var revisions = paths.Select(SecureConfigurationFiles.Revision).ToArray();
        if (!overwrite && revisions.Any(revision => revision is not null)) throw new AgentClientValidationException("Hosting output already exists; review it and explicitly confirm replacement.");
        // Snapshot and validate every existing private file before changing any export member.
        var previous = new string?[paths.Length];
        for (var i = 0; i < paths.Length; i++)
        {
            if (revisions[i] is null) continue;
            previous[i] = SecureConfigurationFiles.ReadText(paths[i], requirePrivate: true);
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous[i]!))) != revisions[i])
                throw new AgentClientValidationException("Hosting output changed; reload it before replacing the export.");
        }
        var outbound = settings.AuthenticationMode == "gateway"
            ? AgentClientConfiguration.Empty with { ApiBaseUrl = settings.ApiBaseUrl, CredentialMode = "gateway" }
            : ContainerOutbound(settings.Outbound!);
        var compose = composeOverride ?? ComposeOverlay(settings);
        const string apply = "This export changes no running service. Preserve existing API database, bootstrap and Data Protection volumes.\n" +
            "Use the base source/release Compose file, its matching gateway/authentik MCP overlay, then compose.host.json last.\n" +
            "Set the overlay interpolation variables (public resource, allowed origin, config file, and external authority when used) to these reviewed values. Set RATELDESK_MCP_CONFIG_FILE to the host path of the exported mcp-outbound.json; a container's /output path is not a host path.\n" +
            "The configuration initializer copies the protected outbound file into the existing volume for UID 10001; normal serving mounts it read-only. External downstream secrets use this private-file fallback, so protect the export directory.\n" +
            "Review docker compose config without logging secrets, then recreate Web/API/MCP explicitly. No API keys or database are mounted into MCP.\n" +
            "Legacy overlays retain direct port 8223 publication. If changing the listener, explicitly replace or remove that optional direct port mapping in your operator overlay; the Web MCP destination and generated healthcheck use the reviewed listener.\n" +
            "Verify configuration, HTTPS reachability, authentication, MCP initialize and a permitted read-only tool separately.\n";
        var content = new[]
        {
            JsonSerializer.Serialize(settings with { Outbound = null }, Json) + "\n",
            JsonSerializer.Serialize(outbound, Json) + "\n",
            JsonSerializer.Serialize(compose, Json) + "\n",
            apply
        };
        if (content.Any(value => Encoding.UTF8.GetByteCount(value) > SecureConfigurationFiles.DefaultMaxBytes))
            throw new AgentClientValidationException("Hosting export exceeds the permitted file size.");
        var written = new string?[paths.Length];
        try
        {
            for (var i = 0; i < paths.Length; i++)
                written[i] = SecureConfigurationFiles.WriteAtomic(paths[i], content[i], revisions[i] ?? "");
        }
        catch
        {
            var incomplete = false;
            for (var i = paths.Length - 1; i >= 0; i--)
            {
                if (written[i] is not { } revision) continue;
                try
                {
                    // A later writer owns its revision; never erase or overwrite that work.
                    if (SecureConfigurationFiles.Revision(paths[i]) != revision) continue;
                    if (previous[i] is { } prior) SecureConfigurationFiles.WriteAtomic(paths[i], prior, revision);
                    else SecureConfigurationFiles.DeleteAtomic(paths[i], revision);
                }
                catch (AgentClientValidationException) { incomplete = true; }
            }
            if (incomplete)
                throw new AgentClientValidationException("Hosting export failed and some unchanged files could not be restored. Review the output directory before retrying.");
            throw;
        }
        return new(paths, apply);
    }

    /// <summary>Exact reviewed patch, shared by effective preflight and export.</summary>
    public static System.Text.Json.Nodes.JsonObject ComposeOverlay(HttpMcpHostingSettings settings)
    {
        var webEnvironment = new Dictionary<string, object?>
        {
            ["ApiBaseUrl"] = settings.ApiBaseUrl,
            ["WebGateway__PublicOrigin"] = settings.PublicOrigin.TrimEnd('/'),
            ["WebGateway__Mcp__Enabled"] = "true",
            ["WebGateway__Mcp__Destination"] = settings.McpDestination,
            ["WebGateway__Mcp__AuthenticationMode"] = settings.AuthenticationMode
        };
        var mcpEnvironment = new Dictionary<string, object?>
        {
            ["ASPNETCORE_URLS"] = settings.Listener,
            ["Helpdesk__Mcp__Instance"] = settings.Instance,
            ["Helpdesk__Mcp__ExpectedApiBaseUrl"] = settings.ApiBaseUrl,
            ["Helpdesk__Mcp__PublicResourceUri"] = settings.PublicResourceUri,
            ["Helpdesk__Mcp__AuthenticationMode"] = settings.AuthenticationMode,
            ["Helpdesk__Mcp__DelegationTimeoutSeconds"] = settings.DelegationTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        for (var i = 0; i < settings.AllowedOrigins.Length; i++) mcpEnvironment[$"Helpdesk__Mcp__AllowedOrigins__{i}"] = settings.AllowedOrigins[i];
        if (settings.AuthenticationMode == "authentik")
        {
            mcpEnvironment["Authentication__AuthentikMcp__Authority"] = settings.AuthentikAuthority;
            mcpEnvironment["Authentication__AuthentikMcp__Audience"] = settings.AuthentikAudience;
            for (var i = 0; i < settings.RequiredScopes.Length; i++) mcpEnvironment[$"Authentication__AuthentikMcp__RequiredScopes__{i}"] = settings.RequiredScopes[i];
            for (var i = 0; i < settings.RequiredGroups.Length; i++) mcpEnvironment[$"Authentication__AuthentikMcp__RequiredGroups__{i}"] = settings.RequiredGroups[i];
        }
        var compose = new
        {
            services = new Dictionary<string, object>
            {
                ["api"] = new { environment = new Dictionary<string, string> { ["IntegrationCredentials__Pairing__PublicOrigin"] = settings.PublicOrigin.TrimEnd('/') } },
                ["web"] = new { environment = webEnvironment },
                ["mcp-http"] = new
                {
                    environment = mcpEnvironment,
                    // This unauthenticated loopback liveness probe need not match a public-host certificate.
                    // Public/client and proxy TLS verification remains enabled separately.
                    healthcheck = new { test = new[] { "CMD-SHELL", $"wget {(settings.Listener.StartsWith("https:", StringComparison.Ordinal) ? "--no-check-certificate " : "")}-q -O /dev/null {settings.Listener[..settings.Listener.IndexOf(':')]}://127.0.0.1:{settings.Listener[(settings.Listener.LastIndexOf(':') + 1)..]}/health/live || exit 1" } }
                },
                ["mcp-config-init"] = new { volumes = new[] { new { type = "bind", source = "${RATELDESK_MCP_CONFIG_FILE:?Set the exported outbound file host path.}", target = "/input/config.json", read_only = true }, new { type = "volume", source = "mcp-http-config", target = "/run/rateldesk-mcp", read_only = false } } }
            }
        };
        return System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(compose, Json))!.AsObject();
    }

    public static AgentClientConfiguration ContainerOutbound(AgentClientConfiguration configuration)
    {
        // Mounted secret paths from the operator's machine do not exist inside MCP.
        // Materialize only the reviewed identity into the protected legacy file copied by init.
        var resolved = configuration.Resolve();
        return new(resolved.ApiBaseUrl.AbsoluteUri, resolved.AuthentikTokenUrl, resolved.AuthentikClientId,
            resolved.AuthentikUsername, resolved.CredentialMode == "authentik" ? resolved.AuthentikAppPassword : null,
            resolved.AuthentikScope, resolved.AgentUserEmail)
        {
            CredentialMode = resolved.CredentialMode,
            IntegrationCredential = resolved.IntegrationCredential,
            CredentialTarget = resolved.ApiBaseUrl.AbsoluteUri.TrimEnd('/')
        };
    }
    private static bool HasSecret(AgentClientConfiguration value) => !string.IsNullOrEmpty(value.IntegrationCredential) || !string.IsNullOrEmpty(value.AuthentikAppPassword)
        || !string.IsNullOrEmpty(value.IntegrationCredentialFile) || !string.IsNullOrEmpty(value.AuthentikAppPasswordFile)
        || !string.IsNullOrEmpty(value.AuthentikTokenUrl) || !string.IsNullOrEmpty(value.AuthentikClientId) || !string.IsNullOrEmpty(value.AuthentikUsername);
    private static bool Values(string[]? values) => values is { Length: > 0 } && values.All(value => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool HttpsUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private static bool Backend(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private static bool Origin(string? value, bool https) => Backend(value) && (!https || new Uri(value!).Scheme == "https");
}
