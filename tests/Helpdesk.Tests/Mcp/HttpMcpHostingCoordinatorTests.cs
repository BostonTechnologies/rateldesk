using System.Text.Json;
using Helpdesk.AgentClient;

namespace Helpdesk.Tests.Mcp;

public sealed class HttpMcpHostingCoordinatorTests
{
    private static HttpMcpHostingSettings Gateway => new()
    {
        PublicOrigin = "https://desk.example.test",
        AllowedOrigins = ["https://desk.example.test"],
        Instance = "customer-west"
    };

    [Fact]
    public void Gateway_retains_private_target_and_arbitrary_named_instance_without_secret()
    {
        Assert.Empty(HttpMcpHostingCoordinator.Validate(Gateway));
        Assert.Equal("https://desk.example.test/mcp", Gateway.PublicResourceUri);
        Assert.NotEmpty(HttpMcpHostingCoordinator.Validate(Gateway with
        {
            Outbound = AgentClientConfiguration.Empty with { CredentialMode = "integration", IntegrationCredential = "private-material" }
        }));
    }

    [Theory]
    [InlineData("http://desk.example.test")]
    [InlineData("https://desk.example.test/path")]
    [InlineData("https://user:password@desk.example.test")]
    [InlineData("https://desk.example.test/?next=https://other.example.test")]
    public void Public_origin_is_deployment_owned_complete_https_origin(string origin)
        => Assert.NotEmpty(HttpMcpHostingCoordinator.Validate(Gateway with { PublicOrigin = origin }));

    [Fact]
    public void External_mode_requires_separate_matching_downstream_identity_and_redacts_preview()
    {
        var external = Gateway with
        {
            AuthenticationMode = "authentik", AuthentikAuthority = "https://auth.example.test/",
            AuthentikAudience = Gateway.PublicResourceUri,
            RequiredScopes = ["helpdesk.mcp"], RequiredGroups = ["ai-assistant"],
            Outbound = AgentClientConfiguration.Empty with
            {
                ApiBaseUrl = Gateway.ApiBaseUrl, CredentialMode = "integration", IntegrationCredential = "private-material"
            }
        };
        Assert.Empty(HttpMcpHostingCoordinator.Validate(external));
        Assert.DoesNotContain("private-material", JsonSerializer.Serialize(HttpMcpHostingCoordinator.Preview(external)));
        Assert.NotEmpty(HttpMcpHostingCoordinator.Validate(external with { Outbound = external.Outbound! with { ApiBaseUrl = "https://other.example.test/" } }));
        Assert.NotEmpty(HttpMcpHostingCoordinator.Validate(external with { Outbound = external.Outbound! with { ApiBaseUrl = null } }));
        Assert.NotEmpty(HttpMcpHostingCoordinator.Validate(external with { Outbound = external.Outbound! with { ApiBaseUrl = Gateway.ApiBaseUrl + "sub/" } }));
    }

    [Fact]
    public void Current_host_inspection_identifies_managed_sources_without_cli_identity_import()
    {
        var values = new Dictionary<string, string>
        {
            ["Helpdesk__Mcp__PublicResourceUri"] = "https://desk.example.test/mcp",
            ["Helpdesk__Mcp__Instance"] = "west-site",
            ["Helpdesk__Mcp__AuthenticationMode"] = "gateway",
            ["Helpdesk__Mcp__ExpectedApiBaseUrl"] = "http://api:8222/",
            ["Helpdesk__Mcp__Outbound__ApiBaseUrl"] = "http://api:8222/",
            ["Helpdesk__Mcp__Outbound__CredentialMode"] = "gateway",
            ["Helpdesk__Mcp__AllowedOrigins__0"] = "https://client.example.test",
            ["RATELDESK_INTEGRATION_CREDENTIAL"] = "unrelated-cli-secret",
            ["Helpdesk__Mcp__DelegationTimeoutSeconds"] = "35"
        };
        var inspected = HttpMcpHostingCoordinator.Inspect(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), values.GetValueOrDefault);
        Assert.Equal("https://desk.example.test", inspected.Settings.PublicOrigin);
        Assert.Equal("west-site", inspected.Settings.Instance);
        Assert.Equal(35, inspected.Settings.DelegationTimeoutSeconds);
        Assert.Equal("gateway", inspected.Settings.Outbound!.CredentialMode);
        Assert.Null(inspected.Settings.Outbound.IntegrationCredential);
        Assert.Contains("PublicOrigin", inspected.ManagedFields.Keys);
        Assert.Contains("Outbound", inspected.ManagedFields.Keys);
        Assert.Empty(HttpMcpHostingCoordinator.Validate(inspected.Settings));
    }

    [Theory]
    [InlineData("http://localhost:8223")]
    [InlineData("http://127.0.0.1:8223")]
    [InlineData("http://127.12.3.4:8223")]
    [InlineData("http://[::1]:8223")]
    public void Shared_network_destination_rejects_container_loopback_listeners(string listener)
        => Assert.NotEmpty(HttpMcpHostingCoordinator.Validate(Gateway with { Listener = listener }));

    [Theory]
    [InlineData("http://+:8223")]
    [InlineData("http://*:8223")]
    [InlineData("http://0.0.0.0:8223")]
    [InlineData("http://[::]:8223")]
    [InlineData("http://mcp-http:8223")]
    public void Shared_network_destination_accepts_accessible_listeners(string listener)
        => Assert.Empty(HttpMcpHostingCoordinator.Validate(Gateway with { Listener = listener }));

    [Fact]
    public void Explicit_split_topology_retains_its_reviewed_backend_and_local_listener()
        => Assert.Empty(HttpMcpHostingCoordinator.Validate(Gateway with
        { McpDestination = "https://mcp-backend.example.test:9443/", Listener = "http://127.0.0.1:8223" }));

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public void Explicit_initializer_alternative_exports_a_matching_custom_listener_healthcheck(string scheme)
    {
        Assert.NotEmpty(HttpMcpHostingCoordinator.Validate(Gateway with { Listener = "http://+:8333" }));
        var directory = Path.Combine(Path.GetTempPath(), "rateldesk-listener-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Gateway with { Listener = $"{scheme}://+:8333", McpDestination = $"{scheme}://mcp-http:8333/" };
            var export = HttpMcpHostingCoordinator.Export(settings, directory,
                composeOverride: HttpMcpHostingCoordinator.ComposeOverlay(settings, legacyInitializer: true));
            using var compose = JsonDocument.Parse(File.ReadAllText(export.Files[2]));
            Assert.Contains("127.0.0.1:8333/health/live", compose.RootElement.GetProperty("services").GetProperty("mcp-http").GetProperty("healthcheck").GetProperty("test")[1].GetString());
            Assert.Equal(scheme == "https", compose.RootElement.GetProperty("services").GetProperty("mcp-http").GetProperty("healthcheck").GetProperty("test")[1].GetString()!.Contains("--no-check-certificate", StringComparison.Ordinal));
            Assert.Contains("RATELDESK_MCP_CONFIG_FILE", compose.RootElement.GetProperty("services").GetProperty("mcp-config-init").GetProperty("volumes")[0].GetProperty("source").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Default_gateway_overlay_contains_only_configuration_changes_without_new_services_or_serving_mounts()
    {
        var overlay = HttpMcpHostingCoordinator.ComposeOverlay(Gateway);
        var services = overlay["services"]!.AsObject();
        Assert.Equal(new[] { "api", "mcp-http", "web" }, services.Select(service => service.Key).Order(StringComparer.Ordinal));
        Assert.Null(overlay["volumes"]);
        Assert.All(services, service =>
        {
            var value = service.Value!.AsObject();
            Assert.Single(value);
            Assert.NotNull(value["environment"]);
            Assert.Null(value["healthcheck"]);
            Assert.Null(value["volumes"]);
            Assert.Null(value["depends_on"]);
            Assert.Null(value["command"]);
        });
        var environment = services["mcp-http"]!["environment"]!.AsObject();
        Assert.Equal("gateway", environment["Helpdesk__Mcp__Outbound__CredentialMode"]!.GetValue<string>());
        Assert.Equal(Gateway.ApiBaseUrl, environment["Helpdesk__Mcp__Outbound__ApiBaseUrl"]!.GetValue<string>());
        Assert.Null(environment["Helpdesk__Mcp__ConfigurationPath"]);
        Assert.Null(environment["RATELDESK_MCP_CONFIG"]);
        Assert.Null(environment["RATELDESK_MCP_HOST_DIRECTORY"]);
        Assert.Null(environment["RATELDESK_MCP_CLIENT_DIRECTORY"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Late_export_failure_restores_previous_files_or_removes_only_new_files(bool replaceExisting)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rateldesk-export-rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var names = new[] { "host-settings.json", "mcp-outbound.json", "compose.host.json", "APPLY.txt" };
            var before = new Dictionary<string, byte[]>();
            if (replaceExisting)
            {
                var exported = HttpMcpHostingCoordinator.Export(Gateway, directory);
                foreach (var file in exported.Files) before.Add(Path.GetFileName(file), File.ReadAllBytes(file));
                File.Delete(Path.Combine(directory, "APPLY.txt.lock"));
            }
            // A directory at the final lock path fails the last atomic write on every platform.
            // Target-file snapshots are valid, so this exercises recovery after earlier writes.
            Directory.CreateDirectory(Path.Combine(directory, "APPLY.txt.lock"));
            Assert.Throws<AgentClientValidationException>(() => HttpMcpHostingCoordinator.Export(
                Gateway with { Instance = "replacement-site" }, directory, overwrite: replaceExisting));
            foreach (var name in names)
            {
                var path = Path.Combine(directory, name);
                if (replaceExisting) Assert.Equal(before[name], File.ReadAllBytes(path));
                else Assert.False(File.Exists(path));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Export_prevalidates_private_existing_outputs_before_any_replacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rateldesk-export-prevalidate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exported = HttpMcpHostingCoordinator.Export(Gateway, directory);
            var before = exported.Files.Select(File.ReadAllBytes).ToArray();
            if (OperatingSystem.IsWindows())
            {
                var security = System.IO.FileSystemAclExtensions.GetAccessControl(new FileInfo(exported.Files[3]));
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                    System.Security.AccessControl.FileSystemRights.ReadData,
                    System.Security.AccessControl.AccessControlType.Allow));
                System.IO.FileSystemAclExtensions.SetAccessControl(new FileInfo(exported.Files[3]), security);
            }
            else File.SetUnixFileMode(exported.Files[3], UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<AgentClientValidationException>(() => HttpMcpHostingCoordinator.Export(
                Gateway with { Instance = "replacement-site" }, directory, overwrite: true));
            for (var i = 0; i < 3; i++) Assert.Equal(before[i], File.ReadAllBytes(exported.Files[i]));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Export_is_private_preserves_previous_output_until_explicit_replacement_and_reloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rateldesk-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var export = HttpMcpHostingCoordinator.Export(Gateway, directory);
            Assert.Equal(4, export.Files.Count);
            Assert.All(export.Files, file => Assert.True(File.Exists(file)));
            Assert.Equal(Gateway.Instance, HttpMcpHostingCoordinator.Load(directory).Instance);
            Assert.Equal("gateway", HttpMcpHostingCoordinator.Load(directory).Outbound!.CredentialMode);
            var before = File.ReadAllBytes(export.Files[2]);
            Assert.Throws<AgentClientValidationException>(() => HttpMcpHostingCoordinator.Export(Gateway with { Instance = "other" }, directory));
            Assert.Equal(before, File.ReadAllBytes(export.Files[2]));
            if (!OperatingSystem.IsWindows())
                foreach (var file in export.Files)
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            using var compose = JsonDocument.Parse(File.ReadAllText(export.Files[2]));
            var services = compose.RootElement.GetProperty("services");
            Assert.Equal("true", services.GetProperty("web").GetProperty("environment").GetProperty("WebGateway__Mcp__Enabled").GetString());
            Assert.False(services.GetProperty("mcp-http").GetProperty("environment").TryGetProperty("RATELDESK_INTEGRATION_CREDENTIAL", out _));
            HttpMcpHostingCoordinator.Export(Gateway with { Instance = "other" }, directory, overwrite: true);
            Assert.Equal("other", HttpMcpHostingCoordinator.Load(directory).Instance);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
