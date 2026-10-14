using System.Text.Json;
using System.Text.Json.Serialization;
using Helpdesk.AgentClient;

internal sealed record CliConfig(
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
    public string? CredentialTarget { get; init; }
    public string? ClientTransport { get; init; }
    public string? CredentialPurpose { get; init; }
    public string? McpResourceUri { get; init; }
    public string? HttpClientAuthentication { get; init; }
    public string[]? OAuthAuthorizationServers { get; init; }
    public string? OAuthClientId { get; init; }
    public string? CredentialOrganizationId { get; init; }
    public string[]? CredentialPermissions { get; init; }
    public DateTimeOffset? CredentialExpiresAtUtc { get; init; }
    public const string DefaultApiBaseUrl = "https://api.helpdesk.example.com";
    public const string DefaultScope = "openid profile email";
    public const string DefaultAgentUserEmail = "agent@example.com";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static CliConfig Empty { get; } = new(null, null, null, null, null, null, null);

    public CliConfig Merge(CliConfig next) => this with
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
        CredentialPurpose = Pick(next.CredentialPurpose, CredentialPurpose),
        McpResourceUri = Pick(next.McpResourceUri, McpResourceUri),
        HttpClientAuthentication = Pick(next.HttpClientAuthentication, HttpClientAuthentication),
        OAuthAuthorizationServers = next.OAuthAuthorizationServers ?? OAuthAuthorizationServers,
        OAuthClientId = Pick(next.OAuthClientId, OAuthClientId),
        CredentialOrganizationId = Pick(next.CredentialOrganizationId, CredentialOrganizationId),
        CredentialPermissions = next.CredentialPermissions ?? CredentialPermissions,
        CredentialExpiresAtUtc = next.CredentialExpiresAtUtc ?? CredentialExpiresAtUtc
    };

    public ResolvedCliConfig Resolve()
    {
        try
        {
            var resolved = ToAgentConfiguration().Resolve();
            if (resolved.CredentialMode == "gateway") throw new CliValidationException("Gateway mode is reserved for the HTTP MCP host; CLI clients require an account credential.");
            return new(resolved.ApiBaseUrl, resolved.AuthentikTokenUrl, resolved.AuthentikClientId, resolved.AuthentikUsername, resolved.AuthentikAppPassword, resolved.AuthentikScope,
                resolved.CredentialMode == "authentik" && string.IsNullOrWhiteSpace(resolved.AgentUserEmail) ? DefaultAgentUserEmail : resolved.AgentUserEmail, resolved.CredentialMode, resolved.IntegrationCredential);
        }
        catch (AgentClientValidationException exception) { throw new CliValidationException(exception.Message); }
    }

    public AgentClientConfiguration ToAgentConfiguration() => new(ApiBaseUrl, AuthentikTokenUrl, AuthentikClientId, AuthentikUsername, AuthentikAppPassword, AuthentikScope, AgentUserEmail)
    { CredentialMode = CredentialMode, IntegrationCredential = IntegrationCredential, IntegrationCredentialFile = IntegrationCredentialFile, AuthentikAppPasswordFile = AuthentikAppPasswordFile, CredentialTarget = CredentialTarget, ClientTransport = ClientTransport, CredentialPurpose = CredentialPurpose, McpResourceUri = McpResourceUri, CredentialOrganizationId = CredentialOrganizationId, CredentialPermissions = CredentialPermissions, CredentialExpiresAtUtc = CredentialExpiresAtUtc, HttpClientAuthentication = HttpClientAuthentication, OAuthAuthorizationServers = OAuthAuthorizationServers, OAuthClientId = OAuthClientId };
    public static CliConfig FromAgentConfiguration(AgentClientConfiguration value) => new(value.ApiBaseUrl, value.AuthentikTokenUrl, value.AuthentikClientId, value.AuthentikUsername, value.AuthentikAppPassword, value.AuthentikScope, value.AgentUserEmail)
    { CredentialMode = value.CredentialMode, IntegrationCredential = value.IntegrationCredential, IntegrationCredentialFile = value.IntegrationCredentialFile, AuthentikAppPasswordFile = value.AuthentikAppPasswordFile, CredentialTarget = value.CredentialTarget, ClientTransport = value.ClientTransport, CredentialPurpose = value.CredentialPurpose, McpResourceUri = value.McpResourceUri, CredentialOrganizationId = value.CredentialOrganizationId, CredentialPermissions = value.CredentialPermissions, CredentialExpiresAtUtc = value.CredentialExpiresAtUtc, HttpClientAuthentication = value.HttpClientAuthentication, OAuthAuthorizationServers = value.OAuthAuthorizationServers, OAuthClientId = value.OAuthClientId };
    public override string ToString() => "CliConfig { secrets = [REDACTED] }";

    private static string? Pick(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}

internal sealed record ResolvedCliConfig(
    Uri ApiBaseUrl,
    string? AuthentikTokenUrl,
    string? AuthentikClientId,
    string? AuthentikUsername,
    string? AuthentikAppPassword,
    string? AuthentikScope,
    string? AgentUserEmail,
    string CredentialMode,
    string? IntegrationCredential)
{
    public override string ToString() => $"ResolvedCliConfig {{ credentialMode = {CredentialMode}, secrets = [REDACTED] }}";
}
