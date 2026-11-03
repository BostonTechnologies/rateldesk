using System.Text.Json;
using Helpdesk.AgentClient;

namespace Helpdesk.Tests.Mcp;

public sealed class ConfigurationJsonMetadataTests
{
    [Fact]
    public void Generated_metadata_preserves_existing_profile_bytes_and_every_field()
    {
        var configuration = new AgentClientConfiguration(
            "https://api.example.test", "https://identity.example.test/token", "client",
            "synthetic-user", "synthetic-password", "openid profile email", "user@example.test")
        {
            CredentialMode = "integration",
            IntegrationCredential = "rdk_synthetic_metadata_test",
            IntegrationCredentialFile = "/synthetic/credential",
            AuthentikAppPasswordFile = "/synthetic/password",
            CredentialTarget = "https://api.example.test",
            ClientTransport = "http",
            McpResourceUri = "https://api.example.test/mcp",
            HttpClientAuthentication = "oauth",
            OAuthAuthorizationServers = ["https://identity.example.test/issuer"],
            OAuthClientId = "public-client",
            CredentialPurpose = "mcp",
            CredentialOrganizationId = "synthetic-organization",
            CredentialPermissions = ["Incident.Read", "Request.Read"],
            CredentialExpiresAtUtc = DateTimeOffset.Parse("2030-02-03T04:05:06+02:00")
        };
        var legacy = JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions);
        var generated = JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonTypeInfo);
        Assert.Equal(legacy, generated);
        var reloaded = JsonSerializer.Deserialize(generated, AgentClientConfiguration.JsonTypeInfo);
        Assert.NotNull(reloaded);
        Assert.Equal(legacy, JsonSerializer.Serialize(reloaded, AgentClientConfiguration.JsonTypeInfo));
        Assert.Equal(configuration.CredentialPermissions, reloaded.CredentialPermissions);
        Assert.Equal(configuration.OAuthAuthorizationServers, reloaded.OAuthAuthorizationServers);
        Assert.Equal(configuration.CredentialExpiresAtUtc, reloaded.CredentialExpiresAtUtc);
        Assert.Equal("{}", JsonSerializer.Serialize(AgentClientConfiguration.Empty, AgentClientConfiguration.JsonTypeInfo));
    }

    [Fact]
    public void Existing_case_insensitive_profile_names_remain_supported()
    {
        const string json = "{\"APIBASEURL\":\"https://api.example.test\",\"credentialMODE\":\"integration\",\"CredentialPermissions\":[\"Incident.Read\"]}";
        var configuration = JsonSerializer.Deserialize(json, AgentClientConfiguration.JsonTypeInfo);
        Assert.NotNull(configuration);
        Assert.Equal("https://api.example.test", configuration.ApiBaseUrl);
        Assert.Equal("integration", configuration.CredentialMode);
        Assert.NotNull(configuration.CredentialPermissions);
        Assert.Equal(["Incident.Read"], configuration.CredentialPermissions);
        Assert.Null(configuration.IntegrationCredential);
    }

    [Theory]
    [InlineData("{\"apiBaseUrl\":\"https://api.example.test\",\"unsupportedSetting\":true}")]
    [InlineData("{\"credentialPermissions\":\"Incident.Read\"}")]
    [InlineData("{\"credentialExpiresAtUtc\":\"invalid-date\"}")]
    public void Unsupported_settings_and_invalid_types_remain_errors(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, AgentClientConfiguration.JsonTypeInfo));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentClientConfiguration>(json, AgentClientConfiguration.JsonOptions));
    }
}
