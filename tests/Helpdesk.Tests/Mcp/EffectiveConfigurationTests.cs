using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class EffectiveConfigurationTests
{
    [Fact]
    public void Mcp_environment_only_gateway_configuration_never_imports_cli_credentials()
    {
        var env = Env(("RATELDESK_API_BASE_URL", "https://wrong.example"), ("RATELDESK_INTEGRATION_CREDENTIAL", "cli-secret"),
            ("RATELDESK_MCP_API_BASE_URL", "http://api:8222/"), ("RATELDESK_MCP_CREDENTIAL_MODE", "gateway"));
        var result = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, environment: env, validate: true);
        Assert.Equal("http://api:8222/", result.Configuration.ApiBaseUrl);
        Assert.Equal("gateway", result.Validate().CredentialMode);
        Assert.Null(result.Configuration.IntegrationCredential);
        Assert.Null(result.ProfilePath);
        Assert.False(result.CanPersist);
        Assert.True(result.Settings.Single(setting => setting.Key == "apiBaseUrl").Managed);
    }

    [Fact]
    public void Canonical_mcp_environment_beats_aliases_and_command_values_beat_environment()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Helpdesk:Mcp:Outbound:ApiBaseUrl"] = "http://canonical:8222", ["Helpdesk:Mcp:Outbound:CredentialMode"] = "gateway" }).Build();
        var canonical = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, configuration: config,
            environment: Env(("RATELDESK_MCP_API_BASE_URL", "http://alias:8222"), ("Helpdesk__Mcp__Outbound__ApiBaseUrl", "http://canonical-env:8222")), validate: true);
        Assert.Equal("http://canonical-env:8222", canonical.Configuration.ApiBaseUrl);
        var result = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp,
            commandOverrides: AgentClientConfiguration.Empty with { ApiBaseUrl = "http://command:8222" }, configuration: config,
            environment: Env(("RATELDESK_MCP_API_BASE_URL", "http://alias:8222"), ("Helpdesk__Mcp__Outbound__CredentialMode", "gateway"), ("Helpdesk__Mcp__Outbound__ApiBaseUrl", "http://canonical-env:8222")), validate: true);
        Assert.Equal("http://command:8222", result.Configuration.ApiBaseUrl);
        Assert.Equal("command", result.Settings.Single(setting => setting.Key == "apiBaseUrl").Source);
        Assert.Equal("environment", result.Settings.Single(setting => setting.Key == "credentialMode").Source);
    }

    [Fact]
    public void Dedicated_environment_beats_canonical_file_configuration()
    {
        var file = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Helpdesk:Mcp:Outbound:ApiBaseUrl"] = "http://file:8222", ["Helpdesk:Mcp:Outbound:CredentialMode"] = "gateway" }).Build();
        var result = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, configuration: file,
            environment: Env(("RATELDESK_MCP_API_BASE_URL", "http://env:8222")), validate: true);
        Assert.Equal("http://env:8222", result.Configuration.ApiBaseUrl);
        Assert.Equal("environment", result.Settings.Single(setting => setting.Key == "apiBaseUrl").Source);
    }

    [Fact]
    public void Explicit_empty_secret_clears_lower_source_instead_of_reusing_it()
    {
        using var files = new Files();
        var path = files.PathFor("profile.json");
        new AgentClientConfigurationStore(path).Save(Integration("https://api.example", "rdk_original"));
        var result = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Cli, path,
            environment: Env(("RATELDESK_INTEGRATION_CREDENTIAL", "")));
        Assert.Equal("", result.Configuration.IntegrationCredential);
        Assert.Throws<AgentClientValidationException>(() => result.Validate());
    }

    [Fact]
    public void Mode_change_replaces_lower_identity_and_gateway_requires_no_persistent_secret()
    {
        using var files = new Files();
        var path = files.PathFor("profile.json");
        new AgentClientConfigurationStore(path).Save(Integration("http://api:8222", "rdk_old"));
        var result = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, path,
            environment: Env(("RATELDESK_MCP_CREDENTIAL_MODE", "gateway")), validate: true);
        Assert.Null(result.Configuration.IntegrationCredential);
        Assert.Null(result.Configuration.CredentialTarget);
        Assert.Equal("gateway", result.Validate().CredentialMode);
    }

    [Fact]
    public void Conflicting_mode_credentials_and_secret_alternatives_are_rejected_without_values_in_errors()
    {
        var exception = Assert.Throws<AgentClientValidationException>(() => AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp,
            environment: Env(("RATELDESK_MCP_CREDENTIAL_MODE", "gateway"), ("RATELDESK_MCP_INTEGRATION_CREDENTIAL", "private-secret"))));
        Assert.DoesNotContain("private-secret", exception.ToString());
        Assert.Throws<AgentClientValidationException>(() => AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp,
            environment: Env(("RATELDESK_MCP_CREDENTIAL_MODE", "integration"), ("RATELDESK_MCP_INTEGRATION_CREDENTIAL", "private-secret"), ("RATELDESK_MCP_INTEGRATION_CREDENTIAL_FILE", "/private-secret-path"))));
    }

    [Fact]
    public void Higher_secret_file_replaces_lower_inline_and_resolves_only_one_secret_source()
    {
        using var files = new Files();
        var path = files.PathFor("profile.json");
        var secretPath = files.PathFor("replacement.secret");
        SecureConfigurationFiles.WriteAtomic(secretPath, "rdk_new\n", "");
        new AgentClientConfigurationStore(path).Save(Integration("https://api.example", "rdk_old"));
        var result = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Cli, path,
            environment: Env(("RATELDESK_INTEGRATION_CREDENTIAL_FILE", secretPath)), validate: true);
        Assert.Null(result.Configuration.IntegrationCredential);
        Assert.Equal("rdk_new", result.Validate().IntegrationCredential);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_control_characters_in_inline_and_file_credentials_fail_without_echoing_the_secret(bool file)
    {
        using var files = new Files();
        const string secret = "rdk_sensitive_value\r\nheader-injection";
        var configuration = Integration("https://api.example", secret);
        if (file)
        {
            var path = files.PathFor("invalid.secret");
            SecureConfigurationFiles.WriteAtomic(path, secret + "\n", "");
            configuration = configuration with { IntegrationCredential = null, IntegrationCredentialFile = path };
        }
        var error = Assert.Throws<AgentClientValidationException>(() => configuration.Resolve());
        Assert.DoesNotContain("rdk_sensitive_value", error.ToString());
        Assert.DoesNotContain("header-injection", error.ToString());
    }

    [Fact]
    public void Integration_secret_length_is_bounded_without_restricting_legacy_synthetic_values()
    {
        var valid = Integration("https://api.example", "legacy-test-value");
        Assert.Equal("legacy-test-value", valid.Resolve().IntegrationCredential);
        var error = Assert.Throws<AgentClientValidationException>(() => (valid with { IntegrationCredential = new string('x', 8193) }).Resolve());
        Assert.DoesNotContain(new string('x', 100), error.ToString());
    }

    [Fact]
    public void Resolved_integration_credentials_do_not_populate_external_authentication_fields()
    {
        var result = Integration("https://api.example", "rdk_fixture").Resolve();
        Assert.Equal("rdk_fixture", result.IntegrationCredential);
        Assert.Null(result.AuthentikAppPassword);
        Assert.Null(result.AuthentikTokenUrl);
        Assert.Null(result.AuthentikClientId);
        Assert.Null(result.AuthentikUsername);
    }

    [Fact]
    public void Read_only_selected_file_fields_have_managed_provenance()
    {
        using var files = new Files();
        var path = files.PathFor("mounted.json");
        new AgentClientConfigurationStore(path).Save(Integration("https://api.example", "rdk_existing"));
        var original = File.GetAttributes(path);
        File.SetAttributes(path, original | FileAttributes.ReadOnly);
        try
        {
            var resolution = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, path, environment: Env());
            Assert.False(resolution.CanPersist);
            Assert.All(resolution.Settings.Where(setting => setting.Source == "file"), setting => Assert.True(setting.Managed));
            Assert.Equal("https://api.example", resolution.Configuration.ApiBaseUrl);
        }
        finally { File.SetAttributes(path, original); }
    }

    [Fact]
    public void Explicit_missing_or_invalid_file_never_falls_back_to_env_or_example_target()
    {
        using var files = new Files();
        var missing = files.PathFor("missing.json");
        Assert.Throws<AgentClientValidationException>(() => AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, missing,
            environment: Env(("RATELDESK_MCP_API_BASE_URL", "http://api:8222"), ("RATELDESK_MCP_CREDENTIAL_MODE", "gateway"))));
        SecureConfigurationFiles.WriteAtomic(missing, "{\"unknown-secret-key\":\"secret\"}");
        var exception = Assert.Throws<AgentClientValidationException>(() => AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Cli, missing, environment: Env()));
        Assert.DoesNotContain("unknown-secret-key", exception.ToString());
        Assert.DoesNotContain("\"secret\"", exception.ToString());
    }

    [Fact]
    public void Explicit_creation_is_allowed_only_when_setup_requests_it()
    {
        using var files = new Files();
        var missing = files.PathFor("new.json");
        var result = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, missing, environment: Env(), allowMissingFile: true);
        Assert.Equal(missing, result.ProfilePath);
        Assert.Null(result.Revision);
        Assert.Null(result.Configuration.ApiBaseUrl);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task Selected_file_without_target_never_uses_the_example_default_in_doctor()
    {
        using var files = new Files();
        var path = files.PathFor("untargeted.json");
        new AgentClientConfigurationStore(path).Save(AgentClientConfiguration.Empty with { CredentialMode = "integration", IntegrationCredential = "private-secret" });
        var resolution = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Cli, path, environment: Env());
        Assert.Throws<AgentClientValidationException>(() => resolution.Validate());
        var report = await ConfigurationDoctor.CheckAsync(resolution);
        Assert.False(report.ConfigurationValid);
    }

    [Fact]
    public void Credential_target_change_fails_before_secret_is_sent()
    {
        using var files = new Files();
        var path = files.PathFor("legacy.json");
        new AgentClientConfigurationStore(path).Save(Integration("https://original.example", "rdk_old"));
        Assert.Throws<AgentClientValidationException>(() => AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Cli, path,
            environment: Env(("RATELDESK_API_BASE_URL", "https://other.example")), validate: true));
        Assert.Equal("https://original.example", new AgentClientConfigurationStore(path).Load().ApiBaseUrl);
    }

    [Fact]
    public void Invalid_endpoints_do_not_echo_embedded_secrets()
    {
        var exception = Assert.Throws<AgentClientValidationException>(() => AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp,
            environment: Env(("RATELDESK_MCP_API_BASE_URL", "https://user:private-secret@api.example"))));
        Assert.DoesNotContain("private-secret", exception.ToString());
    }

    [Fact]
    public void Redacted_json_and_record_strings_do_not_include_secrets_or_file_names()
    {
        var result = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp,
            environment: Env(("RATELDESK_MCP_API_BASE_URL", "https://api.example"), ("RATELDESK_MCP_CREDENTIAL_MODE", "integration"), ("RATELDESK_MCP_INTEGRATION_CREDENTIAL", "private-secret")));
        Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(result.Redacted()));
        Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private-secret", result.Configuration.ToString());
        Assert.DoesNotContain("private-secret", result.Validate().ToString());
        Assert.Throws<AgentClientValidationException>(() => AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp,
            commandOverrides: Integration("https://api.example", "private-secret"), environment: Env()));
    }

    [Fact]
    public async Task Doctor_configuration_only_uses_no_network_and_reports_missing_settings()
    {
        var valid = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp,
            environment: Env(("RATELDESK_MCP_API_BASE_URL", "http://unreachable.invalid:8222"), ("RATELDESK_MCP_CREDENTIAL_MODE", "gateway")));
        var report = await ConfigurationDoctor.CheckAsync(valid);
        Assert.True(report.ConfigurationValid);
        Assert.Single(report.Checks);
        Assert.Equal("configuration", report.Checks[0].Name);
        var invalid = await ConfigurationDoctor.CheckAsync(AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, environment: Env()));
        Assert.False(invalid.ConfigurationValid);
    }

    [Fact]
    public void Profile_save_uses_secret_reference_binding_atomic_revision_and_cancellation()
    {
        using var files = new Files();
        var profiles = new ConfigurationProfiles(files.Root);
        var initial = profiles.Save("desk", Integration("https://api.example", "private-secret"), "");
        Assert.Null(initial.Configuration.IntegrationCredential);
        Assert.NotNull(initial.Configuration.IntegrationCredentialFile);
        Assert.Equal("https://api.example", initial.Configuration.CredentialTarget);
        Assert.DoesNotContain("private-secret", File.ReadAllText(initial.Path));
        Assert.Equal("private-secret", profiles.Load("desk").Resolve().IntegrationCredential);
        Assert.Equal(["desk"], profiles.List());
        var before = File.ReadAllText(initial.Path);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => profiles.Save("desk", Integration("https://api.example", "new-secret"), initial.Revision, cancel.Token));
        Assert.Throws<AgentClientValidationException>(() => profiles.Save("desk", Integration("https://api.example", "new-secret"), "incorrect"));
        Assert.Equal(before, File.ReadAllText(initial.Path));
    }

    [Fact]
    public void Profiles_are_surface_isolated_and_cannot_rebind_saved_identity()
    {
        using var files = new Files();
        var cli = new ConfigurationProfiles(files.Root, ConfigurationSurface.Cli);
        var mcp = new ConfigurationProfiles(files.Root, ConfigurationSurface.Mcp);
        var saved = cli.Save("desk", Integration("https://api.example", "private-secret"), "");
        Assert.Empty(mcp.List());
        Assert.Throws<AgentClientValidationException>(() => mcp.Load("desk"));
        Assert.Throws<AgentClientValidationException>(() => cli.Save("desk", saved.Configuration with { ApiBaseUrl = "https://other.example" }, saved.Revision));
        Assert.Throws<AgentClientValidationException>(() => cli.PathFor("../desk"));
    }

    [Fact]
    public void Unacknowledged_save_can_restore_old_profile_without_overwriting_a_later_writer()
    {
        using var files = new Files();
        var profiles = new ConfigurationProfiles(files.Root);
        var original = profiles.Save("desk", Integration("https://api.example", "older-working"), "");
        var pending = profiles.Save("desk", Integration("https://api.example", "pending-credential"), original.Revision);
        ConfigurationProfiles.RestoreFile(pending.Path, original.Configuration, pending.Revision);
        Assert.Equal("older-working", profiles.Load("desk").Resolve().IntegrationCredential);
        var later = profiles.Save("desk", Integration("https://api.example", "later-working"));
        Assert.Throws<AgentClientValidationException>(() => ConfigurationProfiles.RestoreFile(later.Path, original.Configuration, pending.Revision));
        Assert.Equal("later-working", profiles.Load("desk").Resolve().IntegrationCredential);
        var fresh = profiles.Save("new", Integration("https://api.example", "unacknowledged"), "");
        Assert.Null(ConfigurationProfiles.RestoreFile(fresh.Path, null, fresh.Revision));
        Assert.False(File.Exists(fresh.Path));
    }

    [Fact]
    public void Mcp_purpose_client_profile_is_not_direct_api_authority()
    {
        using var files = new Files();
        var profiles = new ConfigurationProfiles(files.Root, ConfigurationSurface.Mcp);
        var saved = profiles.Save("http", Integration("https://desk.example", "mcp-secret") with
        { ClientTransport = "http", CredentialPurpose = "mcp", McpResourceUri = "https://desk.example/mcp", CredentialOrganizationId = "org-alpha", CredentialPermissions = ["incidents.read"] }, "");
        Assert.Equal("org-alpha", profiles.Load("http").CredentialOrganizationId);
        Assert.Throws<AgentClientValidationException>(() => profiles.Load("http").Resolve());
        Assert.DoesNotContain("mcp-secret", JsonSerializer.Serialize(saved));
    }

    [Fact]
    public async Task OAuth_http_client_metadata_is_persisted_without_a_server_identity_or_secret()
    {
        using var files = new Files();
        var profiles = new ConfigurationProfiles(files.Root, ConfigurationSurface.Mcp);
        var configuration = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://desk.example", ClientTransport = "http", CredentialPurpose = "mcp",
            HttpClientAuthentication = "oauth", McpResourceUri = "https://desk.example/mcp",
            OAuthAuthorizationServers = ["https://id.example/application/o/desk/"], OAuthClientId = "public-client"
        };
        var saved = profiles.Save("oauth", configuration, "");
        Assert.False(Directory.Exists(Path.Combine(profiles.RootPath, "secrets")));
        var resolved = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, saved.Path, environment: Env());
        Assert.Equal("oauth", resolved.Configuration.HttpClientAuthentication);
        Assert.Equal(configuration.OAuthAuthorizationServers, resolved.Configuration.OAuthAuthorizationServers);
        Assert.Equal("public-client", resolved.Configuration.OAuthClientId);
        Assert.Throws<AgentClientValidationException>(() => resolved.Validate());
        var report = await ConfigurationDoctor.CheckAsync(resolved, authenticate: true);
        Assert.True(report.ConfigurationValid);
        var authentication = Assert.Single(report.Checks, check => check.Name == "mcp-authentication");
        Assert.False(authentication.Ok);
        Assert.Contains("unverified", authentication.Message);
    }

    [Fact]
    public async Task Managed_http_client_profile_can_save_nonsecret_metadata_but_runtime_requires_the_external_credential()
    {
        using var files = new Files();
        var profiles = new ConfigurationProfiles(files.Root, ConfigurationSurface.Mcp);
        var configuration = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://desk.example", CredentialMode = "integration", ClientTransport = "http", CredentialPurpose = "mcp",
            HttpClientAuthentication = "integration", McpResourceUri = "https://desk.example/mcp"
        };
        var saved = profiles.Save("managed", configuration, "");
        Assert.Null(saved.Configuration.IntegrationCredential);
        Assert.Null(saved.Configuration.IntegrationCredentialFile);
        var missing = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, saved.Path, environment: Env());
        Assert.False((await ConfigurationDoctor.CheckAsync(missing)).ConfigurationValid);
        var effective = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, saved.Path,
            environment: Env(("RATELDESK_MCP_INTEGRATION_CREDENTIAL", "rdk_external_client")));
        Assert.True((await ConfigurationDoctor.CheckAsync(effective)).ConfigurationValid);
        Assert.DoesNotContain("rdk_external_client", File.ReadAllText(saved.Path));
    }

    [Fact]
    public void OAuth_http_client_profile_rejects_downstream_secrets_insecure_servers_and_retargeting()
    {
        using var files = new Files();
        var profiles = new ConfigurationProfiles(files.Root, ConfigurationSurface.Mcp);
        var configuration = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://desk.example", ClientTransport = "http", CredentialPurpose = "mcp", HttpClientAuthentication = "oauth",
            McpResourceUri = "https://desk.example/mcp", OAuthAuthorizationServers = ["https://id.example"]
        };
        Assert.Throws<AgentClientValidationException>(() => profiles.Save("oauth", configuration with { IntegrationCredential = "private-secret" }, ""));
        Assert.Throws<AgentClientValidationException>(() => profiles.Save("oauth", configuration with { OAuthAuthorizationServers = ["http://id.example"] }, ""));
        Assert.Throws<AgentClientValidationException>(() => profiles.Save("oauth", configuration with { McpResourceUri = "https://other.example/mcp" }, ""));
        Assert.Empty(profiles.List());
    }

    [Fact]
    public void Store_conflict_does_not_overwrite_another_writer()
    {
        using var files = new Files();
        var path = files.PathFor("config.json");
        new AgentClientConfigurationStore(path).Save(Integration("https://api.example", "initial"));
        var first = new AgentClientConfigurationStore(path); var second = new AgentClientConfigurationStore(path);
        var original = first.Load(); second.Load();
        second.Save(original with { IntegrationCredential = "second" });
        Assert.Throws<AgentClientValidationException>(() => first.Save(original with { IntegrationCredential = "first" }));
        Assert.Equal("second", new AgentClientConfigurationStore(path).Load().IntegrationCredential);
    }

    private static AgentClientConfiguration Integration(string target, string credential)
        => AgentClientConfiguration.Empty with { ApiBaseUrl = target, CredentialMode = "integration", IntegrationCredential = credential };
    private static Func<string, string?> Env(params (string Key, string Value)[] entries)
    {
        var values = entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return key => values.GetValueOrDefault(key);
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("rateldesk-config-").FullName;
        public string PathFor(string name) => Path.Combine(Root, name);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
