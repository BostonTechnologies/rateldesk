using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.AgentClient;

public sealed class AgentClientValidationException(string message) : Exception(message);
public sealed class AgentClientRemoteException(string code, string message, int statusCode, string? responseBody) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string? ResponseBody { get; } = responseBody;
}

public sealed record AgentClientConfiguration(
    string? ApiBaseUrl,
    string? AuthentikTokenUrl,
    string? AuthentikClientId,
    string? AuthentikUsername,
    string? AuthentikAppPassword,
    string? AuthentikScope,
    string? AgentUserEmail)
{
    public string? CredentialMode { get; init; }
    public string? IntegrationCredential { get; init; }
    public string? IntegrationCredentialFile { get; init; }
    public string? AuthentikAppPasswordFile { get; init; }
    /// <summary>The API target approved when a persistent credential was obtained.</summary>
    public string? CredentialTarget { get; init; }
    public string? ClientTransport { get; init; }
    public string? McpResourceUri { get; init; }
    public string? HttpClientAuthentication { get; init; }
    public string[]? OAuthAuthorizationServers { get; init; }
    public string? OAuthClientId { get; init; }
    public string? CredentialPurpose { get; init; }
    public string? CredentialOrganizationId { get; init; }
    public string[]? CredentialPermissions { get; init; }
    public DateTimeOffset? CredentialExpiresAtUtc { get; init; }
    public const string DefaultApiBaseUrl = "https://api.helpdesk.example.com";
    public const string DefaultScope = "openid profile email";
    public static AgentClientConfiguration Empty { get; } = new(null, null, null, null, null, null, null);
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public AgentClientConfiguration Merge(AgentClientConfiguration next) => this with
    {
        ApiBaseUrl = Pick(next.ApiBaseUrl, ApiBaseUrl),
        AuthentikTokenUrl = Pick(next.AuthentikTokenUrl, AuthentikTokenUrl),
        AuthentikClientId = Pick(next.AuthentikClientId, AuthentikClientId),
        AuthentikUsername = Pick(next.AuthentikUsername, AuthentikUsername),
        AuthentikAppPassword = Pick(next.AuthentikAppPassword, AuthentikAppPassword),
        AuthentikScope = Pick(next.AuthentikScope, AuthentikScope),
        AgentUserEmail = Pick(next.AgentUserEmail, AgentUserEmail),
        CredentialMode = Pick(next.CredentialMode, CredentialMode),
        IntegrationCredential = Pick(next.IntegrationCredential, IntegrationCredential),
        IntegrationCredentialFile = Pick(next.IntegrationCredentialFile, IntegrationCredentialFile),
        AuthentikAppPasswordFile = Pick(next.AuthentikAppPasswordFile, AuthentikAppPasswordFile),
        CredentialTarget = Pick(next.CredentialTarget, CredentialTarget),
        ClientTransport = Pick(next.ClientTransport, ClientTransport),
        McpResourceUri = Pick(next.McpResourceUri, McpResourceUri),
        HttpClientAuthentication = Pick(next.HttpClientAuthentication, HttpClientAuthentication),
        OAuthAuthorizationServers = next.OAuthAuthorizationServers ?? OAuthAuthorizationServers,
        OAuthClientId = Pick(next.OAuthClientId, OAuthClientId),
        CredentialPurpose = Pick(next.CredentialPurpose, CredentialPurpose),
        CredentialOrganizationId = next.CredentialOrganizationId ?? CredentialOrganizationId,
        CredentialPermissions = next.CredentialPermissions ?? CredentialPermissions,
        CredentialExpiresAtUtc = next.CredentialExpiresAtUtc ?? CredentialExpiresAtUtc
    };

    public ResolvedAgentClientConfiguration Resolve()
    {
        static string Required(string? value, string key) => string.IsNullOrWhiteSpace(value) ? throw new AgentClientValidationException($"{key} is required.") : value;
        AgentClientConfigurationValidator.ValidateChoice(this);
        if (string.Equals(CredentialPurpose, "mcp", StringComparison.OrdinalIgnoreCase))
            throw new AgentClientValidationException("An MCP-purpose credential cannot authenticate direct API operations. Select an API-purpose CLI or stdio profile.");
        var apiUri = AgentClientConfigurationValidator.ApiUri(string.IsNullOrWhiteSpace(ApiBaseUrl) ? DefaultApiBaseUrl : ApiBaseUrl);
        if (!string.IsNullOrWhiteSpace(CredentialTarget) && !AgentClientConfigurationValidator.SameTarget(apiUri, AgentClientConfigurationValidator.ApiUri(CredentialTarget)))
            throw new AgentClientValidationException("The API target differs from the credential's approved target. Reauthorize or explicitly rebind the profile before use.");
        var mode = string.IsNullOrWhiteSpace(CredentialMode) ? "authentik" : CredentialMode.Trim().ToLowerInvariant();
        if (mode == "integration")
        {
            var credential = AgentClientConfigurationValidator.IntegrationSecret(ReadSecret(IntegrationCredential, IntegrationCredentialFile));
            return new(apiUri, null, null, null, null, null, AgentUserEmail, mode, credential);
        }
        if (mode == "gateway")
            return new(AgentClientConfigurationValidator.ApiUri(Required(ApiBaseUrl, "apiBaseUrl")), null, null, null, null, null, AgentUserEmail, mode, null);
        if (mode != "authentik") throw new AgentClientValidationException("credentialMode must be authentik, integration, or gateway.");
        var tokenUrl = Required(AuthentikTokenUrl, "authentikTokenUrl");
        _ = AgentClientConfigurationValidator.ApiUri(tokenUrl, "authentikTokenUrl");
        return new(apiUri, tokenUrl, Required(AuthentikClientId, "authentikClientId"), Required(AuthentikUsername, "authentikUsername"), Required(ReadSecret(AuthentikAppPassword, AuthentikAppPasswordFile), "authentikAppPassword"), string.IsNullOrWhiteSpace(AuthentikScope) ? DefaultScope : AuthentikScope, AgentUserEmail, mode, null);
    }

    private static string? ReadSecret(string? inline, string? path) => string.IsNullOrWhiteSpace(path)
        ? inline : SecureConfigurationFiles.ReadText(path, 16384, requirePrivate: true).TrimEnd('\r', '\n');
    public override string ToString() => $"AgentClientConfiguration {{ credentialMode = {CredentialMode ?? "authentik"}, secrets = [REDACTED] }}";

    private static string? Pick(string? primary, string? fallback) => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}

public sealed record ResolvedAgentClientConfiguration(Uri ApiBaseUrl, string? AuthentikTokenUrl, string? AuthentikClientId, string? AuthentikUsername, string? AuthentikAppPassword, string? AuthentikScope, string? AgentUserEmail, string CredentialMode, string? IntegrationCredential)
{
    public override string ToString() => $"ResolvedAgentClientConfiguration {{ credentialMode = {CredentialMode}, secrets = [REDACTED] }}";
}

public sealed class AgentClientConfigurationStore(string? path = null)
{
    private string? loadedRevision;
    private bool hasLoaded;
    public string Path { get; } = Expand(path) ?? DefaultPath;
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "helpdesk", "cli.json");
    public string? Revision => SecureConfigurationFiles.Revision(Path);
    public ConfigurationProfileSaveResult SaveSecure(AgentClientConfiguration configuration, string? expectedRevision = null, CancellationToken cancellationToken = default)
        => ConfigurationProfiles.SaveFile(Path, configuration, expectedRevision, cancellationToken);
    public AgentClientConfiguration Load()
    {
        loadedRevision = Revision;
        hasLoaded = true;
        if (loadedRevision is null)
        {
            if (!string.IsNullOrWhiteSpace(path)) throw new AgentClientValidationException("The explicitly selected configuration file does not exist.");
            return AgentClientConfiguration.Empty;
        }
        try
        {
            var json = SecureConfigurationFiles.ReadText(Path);
            var loaded = JsonSerializer.Deserialize<AgentClientConfiguration>(json, AgentClientConfiguration.JsonOptions) ?? throw new AgentClientValidationException("The selected configuration must contain an object.");
            string? Reference(string? value) => string.IsNullOrWhiteSpace(value) || System.IO.Path.IsPathRooted(value)
                ? value : System.IO.Path.GetFullPath(value, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            return loaded with { IntegrationCredentialFile = Reference(loaded.IntegrationCredentialFile), AuthentikAppPasswordFile = Reference(loaded.AuthentikAppPasswordFile) };
        }
        catch (JsonException) { throw new AgentClientValidationException("The selected configuration contains invalid JSON or unsupported settings."); }
    }
    public void Save(AgentClientConfiguration configuration)
    {
        using var operation = SecureConfigurationFiles.AcquireOperationLease(Path);
        ConfigurationProfiles.ValidateSecretReferences(configuration);
        if (hasLoaded && !AgentClientConfigurationValidator.SameTargetOrEmpty(LoadExistingTarget(), configuration.ApiBaseUrl) && HasCredential(configuration))
            throw new AgentClientValidationException("Changing the target of a credential-bearing profile requires reauthorization.");
        loadedRevision = SecureConfigurationFiles.WriteAtomic(Path, JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions) + Environment.NewLine, hasLoaded ? loadedRevision ?? string.Empty : null);
        hasLoaded = true;
    }
    private string? LoadExistingTarget()
    {
        if (!File.Exists(Path)) return null;
        try { return JsonSerializer.Deserialize<AgentClientConfiguration>(SecureConfigurationFiles.ReadText(Path), AgentClientConfiguration.JsonOptions)?.ApiBaseUrl; }
        catch (JsonException) { throw new AgentClientValidationException("The existing configuration contains invalid JSON."); }
    }
    private static bool HasCredential(AgentClientConfiguration value) => !string.IsNullOrEmpty(value.IntegrationCredential) || !string.IsNullOrEmpty(value.IntegrationCredentialFile) || !string.IsNullOrEmpty(value.AuthentikAppPassword) || !string.IsNullOrEmpty(value.AuthentikAppPasswordFile);
    private static string? Expand(string? path) => string.IsNullOrWhiteSpace(path) ? null : path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : path.StartsWith("~/", StringComparison.Ordinal) ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;
}

public static partial class AgentClientConfigurationResolver
{
    /// <summary>
    /// Loads the deployment-owned configuration for an isolated MCP process.
    /// Unlike <see cref="Load"/>, this deliberately ignores ambient deployment values
    /// so a second instance's container environment cannot override credentials.
    /// </summary>
    public static AgentClientConfiguration LoadIsolated(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AgentClientValidationException("RATELDESK_MCP_CONFIG is required for isolated MCP configuration.");

        return new AgentClientConfigurationStore(path).Load();
    }

    public static AgentClientConfiguration Load(string? path = null)
    {
        return ResolveEffective(ConfigurationSurface.Cli, path).Configuration;
    }
    public static object Redact(AgentClientConfiguration config) => new { apiBaseUrl = SafeEndpoint(config.ApiBaseUrl), config.CredentialMode, authentikTokenUrl = SafeEndpoint(config.AuthentikTokenUrl), config.AuthentikClientId, config.AuthentikUsername, authentikAppPassword = string.IsNullOrWhiteSpace(config.AuthentikAppPassword) && string.IsNullOrWhiteSpace(config.AuthentikAppPasswordFile) ? null : "***REDACTED***", integrationCredential = string.IsNullOrWhiteSpace(config.IntegrationCredential) && string.IsNullOrWhiteSpace(config.IntegrationCredentialFile) ? null : "***REDACTED***", authentikAppPasswordFile = string.IsNullOrWhiteSpace(config.AuthentikAppPasswordFile) ? null : "[secret reference]", integrationCredentialFile = string.IsNullOrWhiteSpace(config.IntegrationCredentialFile) ? null : "[secret reference]", credentialTarget = SafeEndpoint(config.CredentialTarget), config.AuthentikScope, config.AgentUserEmail, config.ClientTransport, config.CredentialPurpose, config.CredentialOrganizationId, config.CredentialPermissions, config.CredentialExpiresAtUtc, mcpResourceUri = SafeEndpoint(config.McpResourceUri), config.HttpClientAuthentication, oauthAuthorizationServers = config.OAuthAuthorizationServers?.Select(SafeEndpoint), config.OAuthClientId };
    private static string? SafeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? value : "[invalid endpoint]";
    }
}
