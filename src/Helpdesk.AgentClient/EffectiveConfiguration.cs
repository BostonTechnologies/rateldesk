using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Helpdesk.AgentClient;

public enum ConfigurationSurface { Cli, Mcp }

public sealed record EffectiveConfigurationSetting(string Key, string Source, bool Managed, bool IsSecret, string? Value);

public sealed record ConfigurationResolution(
    [property: JsonIgnore] AgentClientConfiguration Configuration,
    IReadOnlyList<EffectiveConfigurationSetting> Settings,
    string? ProfilePath,
    string? Revision,
    bool CanPersist)
{
    public ResolvedAgentClientConfiguration Validate()
    {
        if (string.IsNullOrWhiteSpace(Configuration.ApiBaseUrl))
            throw new AgentClientValidationException("apiBaseUrl must be explicitly configured for the selected profile or MCP surface.");
        return Configuration.Resolve();
    }
    public object Redacted() => new { configuration = AgentClientConfigurationResolver.Redact(Configuration), settings = Settings, profilePath = ProfilePath, revision = Revision, canPersist = CanPersist };
    public override string ToString() => "ConfigurationResolution { secrets = [REDACTED] }";
}

/// <summary>Headless outbound settings shared by runtime and setup. Ingress remains transport-owned.</summary>
public sealed class AgentClientOutboundOptions
{
    public const string McpSectionName = "Helpdesk:Mcp:Outbound";
    public string? ApiBaseUrl { get; set; }
    public string? CredentialMode { get; set; }
    public string? IntegrationCredential { get; set; }
    public string? IntegrationCredentialFile { get; set; }
    public string? AuthentikTokenUrl { get; set; }
    public string? AuthentikClientId { get; set; }
    public string? AuthentikUsername { get; set; }
    public string? AuthentikAppPassword { get; set; }
    public string? AuthentikAppPasswordFile { get; set; }
    public string? AuthentikScope { get; set; }
    public string? AgentUserEmail { get; set; }
    public string? CredentialTarget { get; set; }
    public AgentClientConfiguration ToConfiguration() => new(ApiBaseUrl, AuthentikTokenUrl, AuthentikClientId, AuthentikUsername, AuthentikAppPassword, AuthentikScope, AgentUserEmail)
    { CredentialMode = CredentialMode, IntegrationCredential = IntegrationCredential, IntegrationCredentialFile = IntegrationCredentialFile, AuthentikAppPasswordFile = AuthentikAppPasswordFile, CredentialTarget = CredentialTarget };
    public override string ToString() => "AgentClientOutboundOptions { secrets = [REDACTED] }";
}

public sealed class AgentClientOutboundOptionsValidator : IValidateOptions<AgentClientOutboundOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentClientOutboundOptions options)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(options.ApiBaseUrl)) throw new AgentClientValidationException("Outbound apiBaseUrl must be explicitly configured.");
            options.ToConfiguration().Resolve(); return ValidateOptionsResult.Success;
        }
        catch (AgentClientValidationException exception) { return ValidateOptionsResult.Fail(exception.Message); }
    }
}

public static class AgentClientConfigurationValidator
{
    public static string IntegrationSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new AgentClientValidationException("integrationCredential is required.");
        if (value.Length > 8192 || value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new AgentClientValidationException("Integration credentials must be a bounded single-line value without control characters.");
        return value;
    }
    public static Uri ApiUri(string? value, string key = "apiBaseUrl")
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new AgentClientValidationException($"{key} must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        return uri;
    }
    public static bool SameTarget(Uri left, Uri right) => string.Equals(left.AbsoluteUri.TrimEnd('/'), right.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);
    public static bool SameTargetOrEmpty(string? left, string? right) => string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) || SameTarget(ApiUri(left), ApiUri(right));
    public static void ValidateChoice(AgentClientConfiguration value)
    {
        static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
        if (Present(value.IntegrationCredential) && Present(value.IntegrationCredentialFile)) throw new AgentClientValidationException("Choose one integration credential source: inline or file.");
        if (Present(value.AuthentikAppPassword) && Present(value.AuthentikAppPasswordFile)) throw new AgentClientValidationException("Choose one Authentik password source: inline or file.");
        var mode = (value.CredentialMode ?? "authentik").Trim().ToLowerInvariant();
        if (mode is not ("integration" or "authentik" or "gateway")) throw new AgentClientValidationException("credentialMode must be authentik, integration, or gateway.");
        var integration = Present(value.IntegrationCredential) || Present(value.IntegrationCredentialFile);
        if (Present(value.IntegrationCredential)) _ = IntegrationSecret(value.IntegrationCredential);
        var externalIdentity = Present(value.AuthentikTokenUrl) || Present(value.AuthentikClientId) || Present(value.AuthentikUsername) || Present(value.AuthentikAppPassword) || Present(value.AuthentikAppPasswordFile);
        if ((mode != "integration" && integration) || (mode != "authentik" && externalIdentity))
            throw new AgentClientValidationException("Configured credentials conflict with the selected credentialMode.");
    }

    /// <summary>HTTP client authentication belongs to its MCP client, separate from server outbound identities.</summary>
    public static void ValidateHttpClientProfile(AgentClientConfiguration value, bool requireCredential = true)
    {
        if (value.ClientTransport != "http" || value.CredentialPurpose != "mcp")
            throw new AgentClientValidationException("HTTP client profiles require transport http and credentialPurpose mcp.");
        var origin = ApiUri(value.ApiBaseUrl);
        var resource = ApiUri(value.McpResourceUri, "mcpResourceUri");
        if (origin.Scheme != "https" || origin.AbsolutePath != "/" || resource.Scheme != "https" || resource.AbsolutePath.TrimEnd('/') != "/mcp" ||
            origin.GetLeftPart(UriPartial.Authority) != resource.GetLeftPart(UriPartial.Authority))
            throw new AgentClientValidationException("HTTP client profiles require the reviewed HTTPS origin and its canonical /mcp resource.");
        if (!string.IsNullOrWhiteSpace(value.CredentialTarget) && !SameTarget(origin, ApiUri(value.CredentialTarget)))
            throw new AgentClientValidationException("The HTTP client profile differs from its approved target.");
        var mode = value.HttpClientAuthentication ?? "integration";
        if (mode is not ("integration" or "oauth")) throw new AgentClientValidationException("HTTP client authentication must be integration or oauth.");
        if (mode == "integration")
        {
            var hasCredential = !string.IsNullOrWhiteSpace(value.IntegrationCredential) || !string.IsNullOrWhiteSpace(value.IntegrationCredentialFile);
            if (!hasCredential && requireCredential)
                throw new AgentClientValidationException("An MCP-purpose HTTP client credential is required.");
            if (hasCredential)
                _ = IntegrationSecret(value.IntegrationCredential ?? SecureConfigurationFiles.ReadText(value.IntegrationCredentialFile!, 16384, true).TrimEnd('\r', '\n'));
            if (value.OAuthAuthorizationServers is { Length: > 0 } || !string.IsNullOrWhiteSpace(value.OAuthClientId))
                throw new AgentClientValidationException("OAuth client metadata conflicts with integration authentication.");
            return;
        }
        if (new[] { value.IntegrationCredential, value.IntegrationCredentialFile, value.AuthentikAppPassword, value.AuthentikAppPasswordFile,
            value.AuthentikTokenUrl, value.AuthentikClientId, value.AuthentikUsername }.Any(item => !string.IsNullOrWhiteSpace(item)))
            throw new AgentClientValidationException("External OAuth client profiles cannot contain integration or API downstream identities or secrets.");
        if (value.OAuthAuthorizationServers is not { Length: > 0 and <= 8 } || value.OAuthAuthorizationServers.Any(server =>
            string.IsNullOrWhiteSpace(server) || server.Length > 2048 || ApiUri(server, "oauthAuthorizationServers").Scheme != "https"))
            throw new AgentClientValidationException("OAuth client profiles require one to eight HTTPS authorization server URIs.");
        if (value.OAuthAuthorizationServers.Distinct(StringComparer.Ordinal).Count() != value.OAuthAuthorizationServers.Length ||
            value.OAuthClientId is { Length: > 256 } || value.OAuthClientId?.Any(char.IsControl) == true)
            throw new AgentClientValidationException("OAuth client metadata is invalid.");
    }
}

public static partial class AgentClientConfigurationResolver
{
    private static readonly (string Key, string Suffix)[] Keys =
    [
        ("apiBaseUrl", "API_BASE_URL"), ("credentialMode", "CREDENTIAL_MODE"),
        ("integrationCredential", "INTEGRATION_CREDENTIAL"), ("integrationCredentialFile", "INTEGRATION_CREDENTIAL_FILE"),
        ("authentikTokenUrl", "AUTHENTIK_TOKEN_URL"), ("authentikClientId", "AUTHENTIK_CLIENT_ID"),
        ("authentikUsername", "AUTHENTIK_USERNAME"), ("authentikAppPassword", "AUTHENTIK_APP_PASSWORD"),
        ("authentikAppPasswordFile", "AUTHENTIK_APP_PASSWORD_FILE"), ("authentikScope", "AUTHENTIK_SCOPE"),
        ("agentUserEmail", "AGENT_USER_EMAIL"), ("credentialTarget", "CREDENTIAL_TARGET")
    ];
    private static readonly string[] AuthentikKeys = ["authentikTokenUrl", "authentikClientId", "authentikUsername", "authentikAppPassword", "authentikAppPasswordFile", "authentikScope"];
    private static bool Secret(string key) => key is "integrationCredential" or "authentikAppPassword" or "integrationCredentialFile" or "authentikAppPasswordFile";

    public static AgentClientConfiguration LoadMcp(IConfiguration? configuration = null, string? path = null)
        => ResolveEffective(ConfigurationSurface.Mcp, path, configuration: configuration).Configuration;

    public static ConfigurationResolution ResolveEffective(ConfigurationSurface surface, string? path = null,
        AgentClientConfiguration? commandOverrides = null, IConfiguration? configuration = null,
        Func<string, string?>? environment = null, bool validate = false, bool allowMissingFile = false, bool allowLegacySecretCommandOptions = false)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var provenance = new Dictionary<string, (string Source, bool Managed)>(StringComparer.OrdinalIgnoreCase);
        void Apply(Dictionary<string, string?> layer, string source, bool managed)
        {
            // A mode selected in a higher layer replaces the lower layer's whole identity.
            if (layer.TryGetValue("credentialMode", out var chosen) && !string.IsNullOrWhiteSpace(chosen) &&
                values.TryGetValue("credentialMode", out var previous) && !string.Equals(previous ?? "authentik", chosen, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var key in AuthentikKeys.Concat(["integrationCredential", "integrationCredentialFile", "credentialTarget"]))
                { values.Remove(key); provenance.Remove(key); }
            }
            foreach (var pair in new[] { ("integrationCredential", "integrationCredentialFile"), ("authentikAppPassword", "authentikAppPasswordFile") })
            {
                if (layer.TryGetValue(pair.Item1, out var inline) && !string.IsNullOrWhiteSpace(inline) && layer.TryGetValue(pair.Item2, out var file) && !string.IsNullOrWhiteSpace(file))
                    throw new AgentClientValidationException("Choose one secret source at each configuration level: inline or file.");
                if (layer.ContainsKey(pair.Item1) && !layer.ContainsKey(pair.Item2)) { values.Remove(pair.Item2); provenance.Remove(pair.Item2); }
                if (layer.ContainsKey(pair.Item2) && !layer.ContainsKey(pair.Item1)) { values.Remove(pair.Item1); provenance.Remove(pair.Item1); }
            }
            foreach (var (key, value) in layer) { values[key] = value; provenance[key] = (source, managed); }
        }
        Apply(new() { ["credentialMode"] = "authentik", ["authentikScope"] = AgentClientConfiguration.DefaultScope }, "default", false);
        if (surface == ConfigurationSurface.Cli) Apply(new() { ["apiBaseUrl"] = AgentClientConfiguration.DefaultApiBaseUrl }, "default", false);

        path ??= surface == ConfigurationSurface.Mcp
            ? configuration?["Helpdesk:Mcp:ConfigurationPath"] ?? environment("RATELDESK_MCP_CONFIG")
            : environment("RATELDESK_CONFIG");
        AgentClientConfigurationStore? store = null;
        var metadata = AgentClientConfiguration.Empty;
        if (surface == ConfigurationSurface.Cli || !string.IsNullOrWhiteSpace(path))
        {
            store = new AgentClientConfigurationStore(path);
            var persisted = allowMissingFile && store.Revision is null ? AgentClientConfiguration.Empty : store.Load();
            var fileManaged = File.Exists(store.Path) && new FileInfo(store.Path).IsReadOnly;
            metadata = persisted;
            var fileLayer = ToDictionary(persisted);
            if (!string.IsNullOrWhiteSpace(path) && !fileLayer.ContainsKey("apiBaseUrl"))
            { values.Remove("apiBaseUrl"); provenance.Remove("apiBaseUrl"); }
            Apply(fileLayer, "file", fileManaged);
            // Legacy files gain binding for this resolution without changing their bytes.
            if (HasPersistentSecret(persisted) && !string.IsNullOrWhiteSpace(persisted.ApiBaseUrl) && string.IsNullOrWhiteSpace(persisted.CredentialTarget))
                Apply(new() { ["credentialTarget"] = persisted.ApiBaseUrl }, "file", fileManaged);
        }
        Dictionary<string, string?> Canonical(IConfigurationProvider? provider = null)
        {
            var layer = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, _) in Keys)
            {
                var name = char.ToUpperInvariant(key[0]) + key[1..];
                var canonicalKey = $"{AgentClientOutboundOptions.McpSectionName}:{name}";
                var value = provider is null ? configuration?[canonicalKey] : provider.TryGet(canonicalKey, out var provided) ? provided : null;
                if (value is not null) layer[key] = value.Trim();
            }
            return layer;
        }
        static bool IsEnvironment(IConfigurationProvider provider) => provider.GetType().Name == "EnvironmentVariablesConfigurationProvider";
        static bool IsCommandLine(IConfigurationProvider provider) => provider.GetType().Name == "CommandLineConfigurationProvider";
        var configurationProviders = (configuration as IConfigurationRoot)?.Providers.ToArray();
        if (surface == ConfigurationSurface.Mcp)
        {
            var section = configuration?.GetSection(AgentClientOutboundOptions.McpSectionName);
            foreach (var child in section?.GetChildren() ?? [])
                if (!Keys.Any(item => string.Equals(item.Key, child.Key, StringComparison.OrdinalIgnoreCase)))
                    throw new AgentClientValidationException("The MCP outbound section contains an unsupported setting.");
            if (configurationProviders is null) Apply(Canonical(), "host-file", true);
            else foreach (var provider in configurationProviders.Where(provider => !IsEnvironment(provider) && !IsCommandLine(provider)))
                Apply(Canonical(provider), "host-file", true);
        }
        var prefix = surface == ConfigurationSurface.Cli ? "RATELDESK_" : "RATELDESK_MCP_";
        var aliases = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, suffix) in Keys)
            if (environment(prefix + suffix) is { } value) aliases[key] = value.Trim();
        Apply(aliases, "environment", true);
        if (surface == ConfigurationSurface.Mcp)
        {
            if (configurationProviders is not null)
                foreach (var provider in configurationProviders.Where(IsEnvironment)) Apply(Canonical(provider), "environment", true);
            var canonicalEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, _) in Keys)
            {
                var name = char.ToUpperInvariant(key[0]) + key[1..];
                var current = environment($"Helpdesk__Mcp__Outbound__{name}");
                if (current is not null) canonicalEnvironment[key] = current.Trim();
            }
            Apply(canonicalEnvironment, "environment", true);
            if (configurationProviders is not null)
                foreach (var provider in configurationProviders.Where(IsCommandLine))
                {
                    var command = Canonical(provider);
                    if (command.Keys.Any(Secret)) throw new AgentClientValidationException("Secret command options are not accepted. Use a private file or dedicated environment input.");
                    Apply(command, "command", false);
                }
        }
        if (commandOverrides is not null)
        {
            var overrides = ToDictionary(commandOverrides);
            if (!allowLegacySecretCommandOptions && overrides.Keys.Any(Secret)) throw new AgentClientValidationException("Secret command options are not accepted. Use a private file or dedicated environment input.");
            Apply(overrides, "command", false);
        }
        var effective = FromDictionary(values) with { ClientTransport = metadata.ClientTransport, McpResourceUri = metadata.McpResourceUri, CredentialPurpose = metadata.CredentialPurpose, CredentialOrganizationId = metadata.CredentialOrganizationId, CredentialPermissions = metadata.CredentialPermissions, CredentialExpiresAtUtc = metadata.CredentialExpiresAtUtc, HttpClientAuthentication = metadata.HttpClientAuthentication, OAuthAuthorizationServers = metadata.OAuthAuthorizationServers, OAuthClientId = metadata.OAuthClientId };
        AgentClientConfigurationValidator.ValidateChoice(effective);
        if (!string.IsNullOrWhiteSpace(effective.ApiBaseUrl)) _ = AgentClientConfigurationValidator.ApiUri(effective.ApiBaseUrl);
        if (!string.IsNullOrWhiteSpace(effective.AuthentikTokenUrl)) _ = AgentClientConfigurationValidator.ApiUri(effective.AuthentikTokenUrl, "authentikTokenUrl");
        var settings = Keys.Select(item => new EffectiveConfigurationSetting(item.Key,
            provenance.GetValueOrDefault(item.Key, ("unset", false)).Item1,
            provenance.GetValueOrDefault(item.Key).Managed, Secret(item.Key),
            values.TryGetValue(item.Key, out var value) ? Secret(item.Key) && !string.IsNullOrEmpty(value) ? "***REDACTED***" : value : null)).ToArray();
        var writable = store is not null && !settings.Any(setting => setting.Managed) && (!File.Exists(store.Path) || !new FileInfo(store.Path).IsReadOnly);
        var resolution = new ConfigurationResolution(effective, settings, store?.Path, store?.Revision, writable);
        if (validate)
        {
            if (string.IsNullOrWhiteSpace(effective.ApiBaseUrl)) throw new AgentClientValidationException("apiBaseUrl must be explicitly configured for the selected profile or MCP surface.");
            resolution.Validate();
        }
        return resolution;
    }

    private static bool HasPersistentSecret(AgentClientConfiguration value) => !string.IsNullOrWhiteSpace(value.IntegrationCredential) || !string.IsNullOrWhiteSpace(value.IntegrationCredentialFile) || !string.IsNullOrWhiteSpace(value.AuthentikAppPassword) || !string.IsNullOrWhiteSpace(value.AuthentikAppPasswordFile);
    private static Dictionary<string, string?> ToDictionary(AgentClientConfiguration value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, AgentClientConfiguration.JsonOptions));
        return document.RootElement.EnumerateObject().Where(property => Keys.Any(item => string.Equals(item.Key, property.Name, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.OrdinalIgnoreCase);
    }
    private static AgentClientConfiguration FromDictionary(Dictionary<string, string?> value)
        => JsonSerializer.Deserialize<AgentClientConfiguration>(JsonSerializer.Serialize(value), AgentClientConfiguration.JsonOptions)!;
}
