using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Setup;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Tests.Setup;

public sealed class HttpMcpEffectiveDeploymentTests
{
    internal static readonly HttpMcpHostingSettings Settings = new()
    {
        PublicOrigin = "https://desk.example.test:8443",
        ApiBaseUrl = "http://api.private:8222/",
        McpDestination = "http://mcp-http:8223/",
        Instance = "customer-west",
        AllowedOrigins = ["https://desk.example.test:8443"]
    };

    [Fact]
    public void Selected_legacy_config_is_removed_from_the_complete_reviewed_document_and_shared_definition_is_retained()
    {
        using var fixture = new Fixture();
        var source = Legacy();
        var before = source.ToJsonString();
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.Empty(proposal.CompleteDeployment["services"]!["mcp-http"]!["configs"]!.AsArray());
        Assert.NotNull(proposal.CompleteDeployment["configs"]!["legacy-outbound"]);
        Assert.Single(proposal.CompleteDeployment["services"]!["api"]!["configs"]!.AsArray());
        Assert.Single(proposal.Overlay["x-rateldesk-mount-migrations"]!.AsArray());
        Assert.Contains(proposal.Changes, change => change.Contains("legacy config-file mapping", StringComparison.Ordinal));
        Assert.Equal(before, source.ToJsonString());
        Assert.Empty(HttpMcpEffectiveDeployment.CheckApprovedEffective(proposal, proposal.CompleteDeployment));
        Assert.Empty(HttpMcpEffectiveDeployment.Validate(proposal.CompleteDeployment, Settings));
        Assert.Contains("chmod 0400", proposal.CompleteDeployment["services"]!["mcp-config-init"]!["command"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_utf8_BOM_source_preserves_raw_bytes_and_requires_its_exact_raw_provider_digest(bool tamperProviderHash)
    {
        using var fixture = new Fixture(); var source = Legacy();
        var content = Files(source, "mcp-http")[0]!["content"]!.GetValue<string>();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Source)!, "legacy-outbound.json");
        SecureConfigurationFiles.WritePrivateDocument(path, "\uFEFF" + content);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var rawHash = Convert.ToHexString(SHA256.HashData(bytes));
        // Match the launcher's utf-8-sig decoding and independent raw-file hash.
        var captured = Provider("/run/rateldesk-mcp/config.json", File.ReadAllText(path));
        Assert.Equal(content, captured["content"]!.GetValue<string>());
        captured["sha256"] = tamperProviderHash ? new string('f', 64) : rawHash;
        Files(source, "mcp-http")[0] = captured;
        source["configs"]!["legacy-outbound"]!["file"] = path;
        source["x-rateldesk-source-files"] = new JsonArray(new JsonObject { ["path"] = path, ["sha256"] = rawHash });
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(HttpMcpEffectiveDeployment.Adopt(Settings));
        Assert.Equal(!tamperProviderHash, proposal.Valid);
        Assert.Equal(tamperProviderHash, proposal.Diagnostics.Any(item => item.Code == "HOST_LEGACY_CONFIG_SOURCE"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(rawHash, proposal.CompleteDeployment["x-rateldesk-source-files"]![0]!["sha256"]!.GetValue<string>());
        if (!tamperProviderHash)
        {
            Assert.Empty(proposal.CompleteDeployment["services"]!["mcp-http"]!["configs"]!.AsArray());
            Assert.Empty(HttpMcpEffectiveDeployment.CheckApprovedEffective(proposal, proposal.CompleteDeployment));
        }
    }

    [Theory]
    [InlineData("/run/rateldesk-mcp/./config.json")]
    [InlineData("/run/rateldesk-mcp/sub/../config.json")]
    public void Equivalent_selected_provider_paths_adopt_and_migrate_the_normalized_captured_source(string selected)
    {
        using var fixture = new Fixture(); var source = Legacy();
        Env(source, "mcp-http")["Helpdesk__Mcp__ConfigurationPath"] = selected;
        Env(source, "mcp-http")["RATELDESK_MCP_CONFIG"] = selected;
        source["services"]!["mcp-http"]!["configs"]![0]!["target"] = selected;
        fixture.WriteSource(source);
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings);
        Assert.Equal("gateway", adopted.Outbound!.CredentialMode);
        var proposal = HttpMcpEffectiveDeployment.Create(adopted);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.Empty(proposal.CompleteDeployment["services"]!["mcp-http"]!["configs"]!.AsArray());
        Assert.Empty(HttpMcpEffectiveDeployment.CheckApprovedEffective(proposal, proposal.CompleteDeployment));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("changed-hash")]
    [InlineData("unselected")]
    public void Legacy_migration_requires_one_valid_captured_selected_protected_provider(string failure)
    {
        using var fixture = new Fixture();
        var source = Legacy();
        if (failure == "missing") Files(source, "mcp-http").Clear();
        if (failure == "malformed") Files(source, "mcp-http")[0] = Provider("/run/rateldesk-mcp/config.json", "{private-invalid-json");
        if (failure == "duplicate") Files(source, "mcp-http").Add(Files(source, "mcp-http")[0]!.DeepClone());
        if (failure == "changed-hash") Files(source, "mcp-http")[0]!["sha256"] = new string('f', 64);
        if (failure == "unselected") Env(source, "mcp-http")["Helpdesk__Mcp__ConfigurationPath"] = "/other/config.json";
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.False(proposal.Valid);
        Assert.Contains(proposal.Diagnostics, item => item.Code == "HOST_LEGACY_CONFIG_SOURCE");
        Assert.Null(proposal.Overlay["x-rateldesk-mount-migrations"]);
        Assert.DoesNotContain("private-invalid-json", JsonSerializer.Serialize(proposal.Approval));
        Assert.False(Directory.Exists(fixture.ExportDirectory));
    }

    [Theory]
    [InlineData("configs", "/run//rateldesk-mcp/./config.json", false)]
    [InlineData("configs", "/run/rateldesk-mcp/other.json", true)]
    [InlineData("configs", "/run/rateldesk-mcp-extra/config.json", false)]
    [InlineData("secrets", "/run/rateldesk-mcp/config.json", true)]
    [InlineData("secrets", "/run", true)]
    [InlineData("volumes", "/run/rateldesk-mcp/config.json", true)]
    [InlineData("volumes", "/run/rateldesk-mcp/../rateldesk-mcp/other", true)]
    [InlineData("volumes", "/run/rateldesk-mcp-extra", false)]
    [InlineData("configs", "/output/host-approval.json", true)]
    [InlineData("secrets", "/var/lib/rateldesk/http-client/reader.json", true)]
    public void Complete_proposal_detects_normalized_segment_collisions_across_all_mount_kinds(string kind, string destination, bool conflict)
    {
        using var fixture = new Fixture();
        var source = Complete();
        source[kind] ??= new JsonObject(); source[kind]!["legacy-outbound"] = new JsonObject { ["file"] = "/owner/private/config.json" };
        source["services"]!["mcp-http"]![kind] ??= new JsonArray();
        source["services"]!["mcp-http"]![kind]!.AsArray().Add(new JsonObject { ["source"] = "legacy-outbound", ["target"] = destination, ["type"] = kind == "volumes" ? "bind" : null });
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.Equal(!conflict, proposal.Valid);
        Assert.Equal(conflict, proposal.Diagnostics.Any(item => item.Code == "HOST_MOUNT_COLLISION"));
        if (!conflict && destination.Contains("extra", StringComparison.Ordinal))
            Assert.Contains(proposal.CompleteDeployment["services"]!["mcp-http"]![kind]!.AsArray(), item => item?["target"]?.GetValue<string>() == destination);
    }

    [Theory]
    [InlineData("/private/other:/run/rateldesk-mcp/config.json:ro", true)]
    [InlineData("other:/run/rateldesk-mcp/subdir:ro", true)]
    [InlineData("other:/run/rateldesk-mcp-extra:ro", false)]
    public void Short_volume_syntax_is_checked_without_treating_unrelated_prefixes_as_descendants(string mount, bool conflict)
    {
        using var fixture = new Fixture();
        var source = Complete();
        source["services"]!["mcp-http"]!["volumes"]!.AsArray().Add(mount);
        Assert.Equal(conflict, HttpMcpEffectiveDeployment.Validate(source, Settings).Any(item => item.Code == "HOST_MOUNT_COLLISION"));
        fixture.WriteSource(source);
        Assert.Equal(!conflict, HttpMcpEffectiveDeployment.Create(Settings).Valid);
    }

    [Fact]
    public void Already_migrated_generation_reuses_its_runtime_volume_and_source_changes_require_new_approval()
    {
        using var fixture = new Fixture(); fixture.WriteSource(Legacy());
        var original = HttpMcpEffectiveDeployment.Create(Settings);
        fixture.WriteSource(original.CompleteDeployment);
        var rerun = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.True(rerun.Valid, HostDeploymentWorkflow.Describe(rerun.Diagnostics));
        Assert.DoesNotContain(rerun.Changes, change => change.Contains("legacy config-file mapping", StringComparison.Ordinal));
        Assert.Single(rerun.CompleteDeployment["services"]!["mcp-http"]!["volumes"]!.AsArray(), item => item?["target"]?.GetValue<string>() == "/run/rateldesk-mcp");
        Assert.Equal(original.CompleteDeployment["services"]!["mcp-http"]!["volumes"]!.ToJsonString(), rerun.CompleteDeployment["services"]!["mcp-http"]!["volumes"]!.ToJsonString());
        Assert.NotEqual(original.SourceHash, rerun.SourceHash);
        Assert.NotEqual(original.ProposalHash, rerun.ProposalHash);
        var changed = original.CompleteDeployment.DeepClone().AsObject();
        changed["services"]!["mcp-http"]!["configs"]!.AsArray().Add(new JsonObject { ["source"] = "unreviewed", ["target"] = "/run/rateldesk-mcp/config.json" });
        Assert.Contains(HttpMcpEffectiveDeployment.CheckApprovedEffective(original, changed), item => item.Code == "HOST_MOUNT_COLLISION");
    }

    [Fact]
    public void Legacy_external_mode_preserves_the_selected_effective_downstream_identity()
    {
        using var fixture = new Fixture();
        const string secret = "rdk_synthetic_legacy_external_secret";
        var source = Legacy();
        Env(source, "web")["WebGateway__Mcp__AuthenticationMode"] = "authentik";
        var env = Env(source, "mcp-http"); env["Helpdesk__Mcp__AuthenticationMode"] = "authentik";
        env.Remove("Helpdesk__Mcp__Outbound__ApiBaseUrl"); env.Remove("Helpdesk__Mcp__Outbound__CredentialMode");
        foreach (var key in env.Select(entry => entry.Key).Where(key => key.StartsWith("Helpdesk__Mcp__Outbound__", StringComparison.Ordinal)).ToArray()) env.Remove(key);
        env["Authentication__AuthentikMcp__Authority"] = "https://auth.example.test/application/o/mcp/";
        env["Authentication__AuthentikMcp__Audience"] = Settings.PublicResourceUri;
        env["Authentication__AuthentikMcp__RequiredScopes__0"] = "helpdesk.mcp";
        env["Authentication__AuthentikMcp__RequiredGroups__0"] = "assistant";
        var original = AgentClientConfiguration.Empty with { ApiBaseUrl = Settings.ApiBaseUrl, CredentialMode = "integration", IntegrationCredential = secret, CredentialTarget = Settings.ApiBaseUrl };
        var content = JsonSerializer.Serialize(original, AgentClientConfiguration.JsonOptions);
        Files(source, "mcp-http")[0] = Provider("/run/rateldesk-mcp/config.json", content);
        fixture.WriteSource(source);
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings);
        var proposal = HttpMcpEffectiveDeployment.Create(adopted);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.Equal("authentik", proposal.Settings.AuthenticationMode);
        Assert.Equal(original.Resolve(), proposal.Settings.Outbound!.Resolve());
        Assert.Equal(content, Files(source, "mcp-http")[0]!["content"]!.GetValue<string>());
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(proposal.Approval));
        Assert.DoesNotContain(secret, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        var changed = adopted with { Outbound = original with { IntegrationCredential = "rdk_unreviewed_replacement" } };
        Assert.Contains(HttpMcpEffectiveDeployment.Create(changed).Diagnostics, item => item.Code == "HOST_LEGACY_CONFIG_SOURCE");
    }

    [Fact]
    public void Config_default_destination_and_unknown_same_target_volume_are_validated_before_overlay_replacement()
    {
        using var fixture = new Fixture(); var source = Complete();
        source["services"]!["mcp-http"]!["configs"] = new JsonArray("output");
        fixture.WriteSource(source);
        Assert.Contains(HttpMcpEffectiveDeployment.Create(Settings).Diagnostics, item => item.Code == "HOST_MOUNT_COLLISION");
        source = Complete();
        source["services"]!["mcp-http"]!["volumes"]!.AsArray().Add(new JsonObject { ["type"] = "bind", ["source"] = "/owner/unknown", ["target"] = "/output" });
        fixture.WriteSource(source);
        Assert.Contains(HttpMcpEffectiveDeployment.Create(Settings).Diagnostics, item => item.Code == "HOST_MOUNT_COLLISION");
    }

    [Fact]
    public void Writable_directory_volume_ancestor_can_coexist_with_the_private_child_runtime_mount()
    {
        using var fixture = new Fixture(); var source = Complete();
        source["services"]!["mcp-http"]!["volumes"]!.AsArray().Add(new JsonObject { ["type"] = "volume", ["source"] = "run-directory", ["target"] = "/run" });
        fixture.WriteSource(source);
        Assert.Contains(HttpMcpEffectiveDeployment.Create(Settings).Diagnostics, item => item.Code == "HOST_MOUNT_COLLISION");
        source["x-rateldesk-mount-ancestor-checks"] = new JsonArray(new JsonObject
        {
            ["service"] = "mcp-http", ["source"] = "run-directory", ["target"] = "/run", ["children"] = new JsonArray("/run/rateldesk-mcp")
        });
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.Contains(proposal.CompleteDeployment["services"]!["mcp-http"]!["volumes"]!.AsArray(), mount => mount?["target"]?.GetValue<string>() == "/run");
    }

    [Fact]
    public void Runtime_observation_rejects_an_unreviewed_config_volume_even_with_identical_provider_bytes()
    {
        using var fixture = new Fixture(); fixture.WriteSource(Complete());
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        var runtime = proposal.CompleteDeployment.DeepClone().AsObject();
        var mount = Assert.Single(runtime["services"]!["mcp-http"]!["volumes"]!.AsArray(), item => item?["target"]?.GetValue<string>() == "/run/rateldesk-mcp");
        mount!["source"] = "unreviewed-runtime-volume";
        fixture.WriteRuntime(runtime);
        Assert.Contains(HttpMcpEffectiveDeployment.Observe(proposal, fixture.Runtime), item => item.Code == "HOST_RUNTIME_CONFIG_STORAGE_MISMATCH");
    }

    [Fact]
    public void Anonymous_runtime_store_requires_explicit_named_storage_before_approval()
    {
        using var fixture = new Fixture(); var source = Complete();
        var mounts = source["services"]!["mcp-http"]!["volumes"]!.AsArray();
        mounts[2] = "/run/rateldesk-mcp:ro";
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.False(proposal.Valid);
        Assert.Contains(proposal.Diagnostics, item => item.Code == "HOST_RUNTIME_CONFIG_STORAGE_REQUIRED");
        Assert.Null(proposal.Overlay["volumes"]![""]);
    }

    private static JsonObject Legacy()
    {
        var source = Complete();
        source["configs"] = new JsonObject { ["legacy-outbound"] = new JsonObject { ["file"] = "/owner/private/outbound.json" } };
        source["services"]!["mcp-http"]!["configs"] = new JsonArray(new JsonObject { ["source"] = "legacy-outbound", ["target"] = "/run/rateldesk-mcp/config.json" });
        source["services"]!["api"]!["configs"] = new JsonArray(new JsonObject { ["source"] = "legacy-outbound", ["target"] = "/unrelated/config.json" });
        return source;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_serving_validators_accept_complete_gateway_map_and_list_environments(bool listEnvironment)
    {
        using var fixture = new Fixture();
        var source = Complete();
        if (listEnvironment)
            foreach (var service in new[] { "api", "web", "mcp-http" })
            {
                var environment = Env(source, service);
                source["services"]![service]!["environment"] = new JsonArray(environment.Select(entry => JsonValue.Create(entry.Key + "=" + entry.Value!.GetValue<string>())).ToArray());
            }
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings, fixture.Source);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.Equal(Settings.ApiBaseUrl, proposal.Settings.ApiBaseUrl);
        Assert.Equal("customer-west", proposal.Settings.Instance);
        Assert.Equal("gateway", proposal.Settings.AuthenticationMode);
        Assert.DoesNotContain("SharedKey", proposal.Overlay.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adoption_uses_ordered_protected_providers_then_image_then_inline_and_preserves_source_bytes()
    {
        using var fixture = new Fixture();
        var source = Complete();
        Env(source, "mcp-http").Remove("Helpdesk__Mcp__Instance");
        Env(source, "mcp-http")["RATELDESK_MCP_API_BASE_URL"] = "http://obsolete-alias:8222/";
        source["x-rateldesk-image-environment"]!["mcp-http"] = new JsonArray("Helpdesk__Mcp__Instance=image-site");
        Files(source, "mcp-http").Insert(0, Provider("/app/appsettings.json",
            """{"Helpdesk":{"Mcp":{"Instance":"file-site","DelegationTimeoutSeconds":12}}}"""));
        Files(source, "mcp-http").Insert(1, Provider("/app/appsettings.Production.json",
            """{"Helpdesk":{"Mcp":{"DelegationTimeoutSeconds":34}}}"""));
        Env(source, "mcp-http").Remove("Helpdesk__Mcp__DelegationTimeoutSeconds");
        fixture.WriteSource(source);
        var before = File.ReadAllBytes(fixture.Source);
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source);
        Assert.Equal("image-site", adopted.Instance);
        Assert.Equal(34, adopted.DelegationTimeoutSeconds);
        Assert.Equal(Settings.ApiBaseUrl, adopted.Outbound!.ApiBaseUrl);
        Assert.Equal("gateway", adopted.Outbound.CredentialMode);
        Assert.Equal(before, File.ReadAllBytes(fixture.Source));
    }

    [Theory]
    [InlineData(null, "file-site")]
    [InlineData("", "")]
    public void Unset_image_value_and_explicit_empty_have_distinct_provider_semantics(string? value, string expected)
    {
        using var fixture = new Fixture();
        var source = Complete();
        Env(source, "mcp-http")["Helpdesk__Mcp__Instance"] = value;
        source["x-rateldesk-image-environment"]!["mcp-http"] = new JsonArray("Helpdesk__Mcp__Instance=image-site");
        Files(source, "mcp-http").Insert(0, Provider("/app/appsettings.json", """{"Helpdesk":{"Mcp":{"Instance":"file-site"}}}"""));
        fixture.WriteSource(source);
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source);
        // An empty instance remains invalid rather than resurrecting the image key.
        if (value is null) Assert.Equal(expected, adopted.Instance);
        else
        {
            Assert.DoesNotContain("image-site", adopted.Instance, StringComparison.Ordinal);
            Assert.NotEmpty(HttpMcpEffectiveDeployment.Validate(source, Settings));
        }
    }

    [Fact]
    public void Higher_empty_canonical_credentials_clear_legacy_aliases_and_protected_identity()
    {
        using var fixture = new Fixture();
        var source = Complete();
        Env(source, "mcp-http")["RATELDESK_MCP_INTEGRATION_CREDENTIAL"] = "rdk_lower_alias_secret";
        Env(source, "mcp-http")["RATELDESK_MCP_CREDENTIAL_MODE"] = "integration";
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings, fixture.Source);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        var overlay = proposal.Overlay["services"]!["mcp-http"]!["environment"]!;
        Assert.Equal("", overlay["Helpdesk__Mcp__Outbound__IntegrationCredential"]!.GetValue<string>());
        Assert.Equal("gateway", overlay["Helpdesk__Mcp__Outbound__CredentialMode"]!.GetValue<string>());
        Assert.DoesNotContain("rdk_lower_alias_secret", HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.DoesNotContain("rdk_lower_alias_secret", JsonSerializer.Serialize(proposal.Approval));
    }

    [Theory]
    [InlineData("Helpdesk:Mcp:Instance")]
    [InlineData("helpdesk__mcp__instance")]
    public void Duplicate_canonical_environment_sources_fail_closed_without_printing_values(string duplicate)
    {
        var source = Complete();
        Env(source, "mcp-http")[duplicate] = "untrusted-private-value";
        var failure = Assert.Single(HttpMcpEffectiveDeployment.Validate(source, Settings));
        Assert.Equal("HOST_EFFECTIVE_INVALID", failure.Code);
        Assert.DoesNotContain("untrusted-private-value", HostDeploymentWorkflow.Describe([failure]));
    }

    [Fact]
    public void Full_serving_validator_rejects_lower_provider_values_that_survive_a_valid_fragment()
    {
        using var fixture = new Fixture();
        var source = Complete();
        Env(source, "mcp-http").Remove("Helpdesk__Mcp__AllowedOrigins__1");
        Files(source, "mcp-http").Insert(0, Provider("/app/appsettings.json",
            """{"Helpdesk":{"Mcp":{"AllowedOrigins":["https://desk.example.test:8443","https://invalid.example.test/path"]}}}"""));
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings, fixture.Source);
        Assert.False(proposal.Valid);
        Assert.Contains(proposal.Diagnostics, diagnostic => diagnostic.Code == "MCP_OPTIONS_INVALID" && diagnostic.Key.Contains("AllowedOrigins", StringComparison.Ordinal));
        Assert.All(proposal.Diagnostics, diagnostic => Assert.False(string.IsNullOrWhiteSpace(diagnostic.NextAction)));
    }

    [Fact]
    public void Custom_routes_are_retained_but_conflicting_mcp_destination_is_rejected()
    {
        using var fixture = new Fixture();
        var source = Complete();
        Env(source, "web")["ReverseProxy__Routes__customStatus__ClusterId"] = "customStatus";
        Env(source, "web")["ReverseProxy__Routes__customStatus__Match__Path"] = "/custom-status/{**catch-all}";
        Env(source, "web")["ReverseProxy__Clusters__customStatus__Destinations__one__Address"] = "http://custom.private:8080/";
        fixture.WriteSource(source);
        var before = File.ReadAllBytes(fixture.Source);
        Assert.True(HttpMcpEffectiveDeployment.Create(Settings, fixture.Source).Valid);
        Assert.Equal(before, File.ReadAllBytes(fixture.Source));
        Env(source, "web")["ReverseProxy__Clusters__mcpCluster__Destinations__operator__Address"] = "http://other.private:8223/";
        var failure = HttpMcpEffectiveDeployment.Validate(source, Settings);
        Assert.Contains(failure, diagnostic => diagnostic.Code == "HOST_ROUTE_DESTINATION" && diagnostic.Layer == "web");
        Assert.Equal("http://custom.private:8080/", Env(source, "web")["ReverseProxy__Clusters__customStatus__Destinations__one__Address"]!.GetValue<string>());
    }

    [Fact]
    public void Explicit_hard_disabled_mcp_is_not_silently_exposed_by_setup()
    {
        using var fixture = new Fixture();
        var source = Complete();
        Env(source, "web")["WebGateway__Mcp__HardDisable"] = "true";
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings, fixture.Source);
        Assert.False(proposal.Valid);
        Assert.Contains(proposal.Diagnostics, diagnostic => diagnostic.Code == "WEB_MCP_DISABLED");
        Assert.Equal("true", Env(source, "web")["WebGateway__Mcp__HardDisable"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("host")]
    [InlineData("client")]
    public void Missing_preflighted_private_storage_is_rejected(string kind)
    {
        var source = Complete();
        source["x-rateldesk-storage"]![kind] = null;
        Assert.Contains(HttpMcpEffectiveDeployment.Validate(source, Settings), diagnostic => diagnostic.Code == "HOST_STORAGE_REQUIRED" && diagnostic.Key == kind);
    }

    [Fact]
    public void Client_storage_cannot_be_shared_with_host_or_mounted_by_API_through_a_volume_alias()
    {
        var source = Complete();
        source["x-rateldesk-storage"]!["host"]!["source"] = "fixture-client-data";
        Assert.Contains(HttpMcpEffectiveDeployment.Validate(source, Settings), diagnostic => diagnostic.Code == "HOST_STORAGE_ISOLATION");
        source = Complete();
        source["services"]!["api"]!["volumes"] = new JsonArray(new JsonObject { ["type"] = "volume", ["source"] = "client-store", ["target"] = "/private/clients" });
        Assert.Contains(HttpMcpEffectiveDeployment.Validate(source, Settings), diagnostic => diagnostic.Code == "HOST_STORAGE_API_EXPOSURE");
    }

    [Fact]
    public void Storage_receipt_without_actual_serving_mount_cannot_establish_persistence()
    {
        var source = Complete();
        source["services"]!["mcp-http"]!["volumes"] = new JsonArray();
        Assert.NotEmpty(HttpMcpEffectiveDeployment.Validate(source, Settings));
    }

    [Fact]
    public void Serving_authentication_mode_disagreement_has_a_single_safe_diagnostic()
    {
        var source = Complete();
        Env(source, "web")["WebGateway__Mcp__AuthenticationMode"] = "authentik";
        var diagnostics = HttpMcpEffectiveDeployment.Validate(source, Settings);
        Assert.Single(diagnostics.Where(diagnostic => diagnostic.Code == "HOST_AUTHENTICATION_MISMATCH"));
    }

    [Fact]
    public void Existing_external_Authentik_and_separate_downstream_identity_are_preserved_and_redacted()
    {
        using var fixture = new Fixture();
        const string secret = "rdk_synthetic_existing_external_identity";
        var external = Settings with
        {
            AuthenticationMode = "authentik", AuthentikAuthority = "https://auth.example.test/application/o/mcp/",
            AuthentikAudience = Settings.PublicResourceUri, RequiredScopes = ["helpdesk.mcp"], RequiredGroups = ["assistant"],
            Outbound = AgentClientConfiguration.Empty with { ApiBaseUrl = Settings.ApiBaseUrl, CredentialMode = "integration", IntegrationCredential = secret }
        };
        var source = Complete();
        Env(source, "web")["WebGateway__Mcp__AuthenticationMode"] = "authentik";
        Env(source, "mcp-http")["Helpdesk__Mcp__AuthenticationMode"] = "authentik";
        Env(source, "mcp-http")["Helpdesk__Mcp__Outbound__CredentialMode"] = "integration";
        Env(source, "mcp-http")["Helpdesk__Mcp__Outbound__IntegrationCredential"] = secret;
        Env(source, "mcp-http")["Authentication__AuthentikMcp__Authority"] = external.AuthentikAuthority;
        Env(source, "mcp-http")["Authentication__AuthentikMcp__Audience"] = external.AuthentikAudience;
        Env(source, "mcp-http")["Authentication__AuthentikMcp__RequiredScopes__0"] = "helpdesk.mcp";
        Env(source, "mcp-http")["Authentication__AuthentikMcp__RequiredGroups__0"] = "assistant";
        Files(source, "mcp-http")[0] = Provider("/run/rateldesk-mcp/config.json", JsonSerializer.Serialize(external.Outbound, AgentClientConfiguration.JsonOptions));
        fixture.WriteSource(source);
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source);
        Assert.Equal("authentik", adopted.AuthenticationMode);
        Assert.Equal(secret, adopted.Outbound!.IntegrationCredential);
        Assert.Equal(Settings.ApiBaseUrl, adopted.Outbound.ApiBaseUrl);
        var proposal = HttpMcpEffectiveDeployment.Create(external, fixture.Source);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(proposal.Approval));
        Assert.DoesNotContain(secret, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
    }

    [Fact]
    public async Task Changed_source_rejects_export_before_polling_or_writing_any_generation()
    {
        using var fixture = new Fixture();
        fixture.WriteSource(Complete());
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        var changed = Complete();
        changed["services"]!["worker"] = new JsonObject { ["image"] = "synthetic-worker:v2" };
        fixture.WriteSource(changed);
        using var handler = new ApprovalTransport(proposal);
        using var client = new HttpClient(handler);
        using var workflow = new HostDeploymentWorkflow(Settings.PublicOrigin, client);
        var error = await Assert.ThrowsAsync<AgentClientValidationException>(() => workflow.SaveApprovedAsync(proposal,
            new(new string('a', 64), "ABCD-EFGH", Settings.PublicOrigin + "/account/integration-credentials/approve?kind=host", DateTimeOffset.UtcNow.AddMinutes(5), 1),
            fixture.ExportDirectory, false, CancellationToken.None));
        Assert.Contains("HOST_SOURCE_CHANGED", error.Message);
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(fixture.ExportDirectory));
    }

    [Fact]
    public async Task New_invocation_resumes_same_approved_generation_and_rejects_changed_export_or_source()
    {
        using var fixture = new Fixture();
        var source = Complete();
        source["x-rateldesk-source-files"] = new JsonArray(new JsonObject { ["path"] = "/owner/compose.json", ["sha256"] = new string('d', 64) });
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        using var handler = new ApprovalTransport(proposal);
        using var client = new HttpClient(handler);
        using var workflow = new HostDeploymentWorkflow(Settings.PublicOrigin, client);
        await workflow.SaveApprovedAsync(proposal,
            new(new string('a', 64), "ABCD-EFGH", Settings.PublicOrigin + "/account/integration-credentials/approve?kind=host", DateTimeOffset.UtcNow.AddMinutes(5), 1),
            fixture.ExportDirectory, false, CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        var resumed = HostDeploymentWorkflow.ResumeApproved(fixture.ExportDirectory);
        Assert.Equal(proposal.ProposalHash, resumed.ProposalHash);
        Assert.Equal(proposal.SourceHash, resumed.SourceHash);
        Assert.Equal(Settings.Instance, resumed.Settings.Instance);
        var exportPath = Path.Combine(fixture.ExportDirectory, "mcp-outbound.json");
        var original = File.ReadAllText(exportPath);
        SecureConfigurationFiles.WriteAtomic(exportPath, original + " ");
        Assert.Contains("HOST_EXPORT_CHANGED", Assert.Throws<AgentClientValidationException>(() => HostDeploymentWorkflow.ResumeApproved(fixture.ExportDirectory)).Message);
        SecureConfigurationFiles.WriteAtomic(exportPath, original);
        // A real launcher also retains its reviewed complete manager document
        // and generation receipt for the sole permitted import transition.
        var document = new JsonObject { ["services"] = source["services"]!.DeepClone(), ["volumes"] = source["volumes"]!.DeepClone() }.ToJsonString();
        SecureConfigurationFiles.WritePrivateDocument(Path.Combine(fixture.ExportDirectory, "deployment.json"), document);
        SecureConfigurationFiles.WritePrivateDocument(Path.Combine(fixture.ExportDirectory, "launcher-state.json"),
            new JsonObject { ["sourceHash"] = proposal.SourceHash, ["deploymentSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))) }.ToJsonString());
        source["x-rateldesk-image-ids"]!["mcp-http"] = "sha256:" + new string('f', 64);
        fixture.WriteSource(source);
        Assert.Contains("HOST_SOURCE_CHANGED", Assert.Throws<AgentClientValidationException>(() => HostDeploymentWorkflow.ResumeApproved(fixture.ExportDirectory)).Message);
    }

    [Fact]
    public void Runtime_observation_requires_matching_running_configuration_and_image_without_verifier_consent()
    {
        using var fixture = new Fixture();
        fixture.WriteSource(Complete());
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.Equal("HOST_RUNTIME_REQUIRED", Assert.Single(HttpMcpEffectiveDeployment.Observe(proposal, "")).Code);
        fixture.WriteRuntime(Complete());
        Assert.Empty(HttpMcpEffectiveDeployment.Observe(proposal, fixture.Runtime));
        var changed = Complete();
        Env(changed, "mcp-http")["Helpdesk__Mcp__DelegationTimeoutSeconds"] = "35";
        changed["x-rateldesk-image-ids"]!["web"] = "sha256:" + new string('f', 64);
        fixture.WriteRuntime(changed);
        var failures = HttpMcpEffectiveDeployment.Observe(proposal, fixture.Runtime);
        Assert.Contains(failures, diagnostic => diagnostic.Code == "HOST_RUNTIME_MISMATCH" && diagnostic.Key == "Helpdesk:Mcp:DelegationTimeoutSeconds");
        Assert.Contains(failures, diagnostic => diagnostic.Code == "HOST_RUNTIME_IMAGE" && diagnostic.Layer == "web");
        Assert.DoesNotContain("35", HostDeploymentWorkflow.Describe(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_only_lower_provider_origin_cannot_expand_the_reviewed_browser_allowlist()
    {
        using var fixture = new Fixture();
        fixture.WriteSource(Complete());
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        var runtime = Complete();
        Files(runtime, "mcp-http").Insert(0, Provider("/app/appsettings.json",
            """{"Helpdesk":{"Mcp":{"AllowedOrigins":["https://desk.example.test:8443","https://unreviewed.example.test"]}}}"""));
        // The serving options are valid in isolation, but exceed the exact
        // browser origins approved for this deployment generation.
        Assert.True(new Helpdesk.Mcp.Http.Configuration.HelpdeskMcpHttpOptionsValidator().Validate(null,
            new Helpdesk.Mcp.Http.Configuration.HelpdeskMcpHttpOptions { PublicResourceUri = Settings.PublicResourceUri,
                AuthenticationMode = "gateway", AllowedOrigins = [Settings.PublicOrigin, "https://unreviewed.example.test"] }).Succeeded);
        Assert.Contains(HttpMcpEffectiveDeployment.Validate(runtime, Settings), diagnostic => diagnostic.Code == "HOST_EFFECTIVE_OPTIONS_MISMATCH");
        fixture.WriteRuntime(runtime);
        Assert.NotEmpty(HttpMcpEffectiveDeployment.Observe(proposal, fixture.Runtime));
    }

    [Fact]
    public async Task Exact_manager_document_import_resumes_the_same_approved_generation()
    {
        using var fixture = new Fixture();
        var proposal = await PrepareManagerImportAsync(fixture);
        var resumed = HostDeploymentWorkflow.ResumeApproved(fixture.ExportDirectory);
        Assert.Equal(proposal.SourceHash, resumed.SourceHash);
        Assert.Equal(proposal.ProposalHash, resumed.ProposalHash);
        Assert.Equal(Settings.ApiBaseUrl, resumed.Settings.ApiBaseUrl);
        Assert.Equal(Settings.AllowedOrigins, resumed.Settings.AllowedOrigins);
    }

    [Fact]
    public async Task Matching_manager_document_receipt_cannot_authorize_a_subtle_effective_provider_change()
    {
        using var fixture = new Fixture();
        await PrepareManagerImportAsync(fixture);
        var imported = JsonNode.Parse(File.ReadAllText(fixture.Source))!.AsObject();
        Files(imported, "mcp-http").Insert(0, Provider("/app/appsettings.json",
            """{"Helpdesk":{"Mcp":{"AllowedOrigins":["https://desk.example.test:8443","https://unreviewed.example.test"]}}}"""));
        fixture.WriteSource(imported);
        var failure = Assert.Throws<AgentClientValidationException>(() => HostDeploymentWorkflow.ResumeApproved(fixture.ExportDirectory));
        Assert.Contains("HOST_EFFECTIVE_OPTIONS_MISMATCH", failure.Message);
        Assert.DoesNotContain("https://unreviewed.example.test", failure.Message);
    }

    [Theory]
    [InlineData("web", "--ApiBaseUrl=http://unreviewed.private:8222/")]
    [InlineData("mcp-http", "--Helpdesk:Mcp:Outbound:ApiBaseUrl=http://unreviewed.private:8222/")]
    public void Command_line_backend_override_cannot_be_hidden_by_the_proposed_environment(string service, string argument)
    {
        using var fixture = new Fixture();
        var source = Complete();
        source["services"]![service]!["command"] = new JsonArray(argument);
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.False(proposal.Valid);
        Assert.Contains(proposal.Diagnostics, diagnostic => diagnostic.Code is "HOST_API_TARGET_MISMATCH" or "HOST_EFFECTIVE_INVALID");
        Assert.DoesNotContain("unreviewed.private", HostDeploymentWorkflow.Describe(proposal.Diagnostics));
    }

    [Fact]
    public void Custom_shell_entrypoint_is_rejected_before_host_consent()
    {
        using var fixture = new Fixture();
        var source = Complete();
        source["services"]!["mcp-http"]!["entrypoint"] = new JsonArray("/bin/sh", "-ec", "exec dotnet Helpdesk.Mcp.Http.dll");
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.False(proposal.Valid);
        Assert.Contains(proposal.Diagnostics, diagnostic => diagnostic.Code == "HOST_EFFECTIVE_INVALID");
    }

    public static IEnumerable<object[]> AlternateProviderRootSources =>
        from service in new[] { "api", "web", "mcp-http" }
        from provider in new[] { "command-equals", "command-two-token", "image-command", "dotnet-environment", "aspnetcore-environment", "compose-directory", "image-directory" }
        select new object[] { service, provider };

    [Theory]
    [MemberData(nameof(AlternateProviderRootSources))]
    public void Alternate_serving_provider_roots_fail_before_consent_without_disclosing_source_values(string service, string provider)
    {
        using var fixture = new Fixture();
        const string alternate = "/alternate-unobserved-provider";
        var source = Complete();
        SelectProviderRoot(source, service, provider, alternate);
        fixture.WriteSource(source);
        var before = File.ReadAllBytes(fixture.Source);
        var diagnostic = Assert.Single(HttpMcpEffectiveDeployment.Validate(source, Settings));
        Assert.Equal("HOST_EFFECTIVE_INVALID", diagnostic.Code);
        Assert.DoesNotContain(alternate, HostDeploymentWorkflow.Describe([diagnostic]));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.NextAction));
        var proposal = HttpMcpEffectiveDeployment.Create(Settings, fixture.Source);
        Assert.False(proposal.Valid);
        Assert.Contains(proposal.Diagnostics, item => item.Code == "HOST_EFFECTIVE_INVALID");
        Assert.DoesNotContain(alternate, JsonSerializer.Serialize(proposal.Approval));
        if (service != "api")
        {
            var failure = Assert.Throws<AgentClientValidationException>(() => HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source));
            Assert.StartsWith("HOST_PROVIDER_ROOT_UNSUPPORTED:", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(alternate, failure.Message);
        }
        Assert.Equal(before, File.ReadAllBytes(fixture.Source));
        Assert.False(Directory.Exists(fixture.ExportDirectory));
    }

    [Fact]
    public void Explicit_canonical_app_roots_are_accepted_across_serving_provider_sources()
    {
        using var fixture = new Fixture();
        var source = Complete();
        foreach (var service in new[] { "api", "web", "mcp-http" })
        {
            foreach (var provider in new[] { "image-directory", "compose-directory", "aspnetcore-environment", "dotnet-environment", "image-command", "command-two-token" })
                SelectProviderRoot(source, service, provider, "/app/");
            Files(source, service).Insert(0, Provider("/app/appsettings.json", "{}"));
        }
        fixture.WriteSource(source);
        Assert.Empty(HttpMcpEffectiveDeployment.Validate(source, Settings));
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source);
        Assert.Equal(Settings.PublicOrigin, adopted.PublicOrigin);
        Assert.Equal(Settings.ApiBaseUrl, adopted.Outbound!.ApiBaseUrl);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings, fixture.Source);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
    }

    [Fact]
    public void Actual_mixed_origins_file_under_an_uncaptured_content_root_cannot_be_approved_as_canonical_only()
    {
        using var fixture = new Fixture();
        var source = Complete();
        var alternate = Path.Combine(Path.GetDirectoryName(fixture.Source)!, "alternate");
        var mixed = """{"Helpdesk":{"Mcp":{"AllowedOrigins":["https://desk.example.test:8443","https://unreviewed.example.test"]}}}""";
        SecureConfigurationFiles.WriteAtomic(Path.Combine(alternate, "appsettings.json"), mixed);
        using var actual = (ConfigurationRoot)new ConfigurationBuilder().SetBasePath(alternate).AddJsonFile("appsettings.json")
            .AddInMemoryCollection(Env(source, "mcp-http").Select(entry => new KeyValuePair<string, string?>(entry.Key.Replace("__", ":", StringComparison.Ordinal), entry.Value?.GetValue<string>())))
            .AddCommandLine(["--contentRoot", alternate]).Build();
        var actualOptions = actual.GetSection(Helpdesk.Mcp.Http.Configuration.HelpdeskMcpHttpOptions.SectionName)
            .Get<Helpdesk.Mcp.Http.Configuration.HelpdeskMcpHttpOptions>()!;
        Assert.True(new Helpdesk.Mcp.Http.Configuration.HelpdeskMcpHttpOptionsValidator().Validate(null, actualOptions).Succeeded);
        Assert.Equal(new[] { Settings.PublicOrigin, "https://unreviewed.example.test" }, actualOptions.AllowedOrigins);
        // The captured canonical provider looks safe in isolation. The actual
        // process would instead load the physical alternate file above.
        Files(source, "mcp-http").Insert(0, Provider("/app/appsettings.json",
            """{"Helpdesk":{"Mcp":{"AllowedOrigins":["https://desk.example.test:8443"]}}}"""));
        SelectProviderRoot(source, "mcp-http", "command-two-token", alternate);
        Assert.DoesNotContain(Files(source, "mcp-http"), file => file?["path"]?.GetValue<string>() is { } path && path.StartsWith(alternate, StringComparison.Ordinal));
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings, fixture.Source);
        Assert.False(proposal.Valid);
        Assert.Contains(proposal.Diagnostics, item => item.Code == "HOST_EFFECTIVE_INVALID");
        Assert.DoesNotContain("unreviewed.example.test", HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.False(Directory.Exists(fixture.ExportDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Empty_or_whitespace_canonical_configuration_path_uses_the_captured_legacy_file(string? configuredPath)
    {
        using var fixture = new Fixture();
        var source = Complete();
        const string legacy = "/run/legacy-outbound/config.json";
        Env(source, "mcp-http")["Helpdesk__Mcp__ConfigurationPath"] = configuredPath;
        Env(source, "mcp-http")["RATELDESK_MCP_CONFIG"] = legacy;
        Env(source, "mcp-http").Remove("Helpdesk__Mcp__Outbound__ApiBaseUrl");
        Env(source, "mcp-http").Remove("Helpdesk__Mcp__Outbound__CredentialMode");
        Files(source, "mcp-http")[0] = Provider("/run/rateldesk-mcp/config.json", "not the selected JSON provider");
        Files(source, "mcp-http").Add(Provider(legacy, JsonSerializer.Serialize(AgentClientConfiguration.Empty with
        { ApiBaseUrl = Settings.ApiBaseUrl, CredentialMode = "gateway" }, AgentClientConfiguration.JsonOptions)));
        fixture.WriteSource(source);
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source);
        Assert.Equal(Settings.ApiBaseUrl, adopted.Outbound!.ApiBaseUrl);
        Assert.Equal("gateway", adopted.Outbound.CredentialMode);
        Assert.Empty(HttpMcpEffectiveDeployment.Validate(source, Settings));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Whitespace_canonical_path_does_not_hide_an_uncaptured_selected_legacy_file(string configuredPath)
    {
        using var fixture = new Fixture();
        var source = Complete();
        const string legacy = "/run/uncaptured-legacy/config.json";
        Env(source, "mcp-http")["Helpdesk__Mcp__ConfigurationPath"] = configuredPath;
        Env(source, "mcp-http")["RATELDESK_MCP_CONFIG"] = legacy;
        fixture.WriteSource(source);
        Assert.Equal("HOST_EFFECTIVE_INVALID", Assert.Single(HttpMcpEffectiveDeployment.Validate(source, Settings)).Code);
        var error = Assert.Throws<AgentClientValidationException>(() => HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source));
        Assert.StartsWith("HOST_OUTBOUND_PROVIDER_MISSING:", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(legacy, error.Message);
        Assert.False(Directory.Exists(fixture.ExportDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Command_selected_outbound_file_must_be_captured_and_command_values_override_its_lower_sources(bool captured)
    {
        using var fixture = new Fixture();
        var source = Complete();
        const string commandPath = "/run/command-selected/config.json";
        Env(source, "mcp-http")["Helpdesk__Mcp__ConfigurationPath"] = " ";
        Env(source, "mcp-http")["Helpdesk__Mcp__Outbound__ApiBaseUrl"] = "http://lower-environment.private:8222/";
        source["services"]!["mcp-http"]!["command"] = new JsonArray("--Helpdesk:Mcp:ConfigurationPath=" + commandPath,
            "--Helpdesk:Mcp:Outbound:ApiBaseUrl=" + Settings.ApiBaseUrl, "--Helpdesk:Mcp:Outbound:CredentialMode=gateway");
        if (captured) Files(source, "mcp-http").Add(Provider(commandPath, JsonSerializer.Serialize(AgentClientConfiguration.Empty with
        { ApiBaseUrl = "http://lower-file.private:8222/", CredentialMode = "gateway" }, AgentClientConfiguration.JsonOptions)));
        fixture.WriteSource(source);
        var diagnostics = HttpMcpEffectiveDeployment.Validate(source, Settings);
        if (!captured)
        {
            Assert.Equal("HOST_EFFECTIVE_INVALID", Assert.Single(diagnostics).Code);
            Assert.StartsWith("HOST_OUTBOUND_PROVIDER_MISSING:", Assert.Throws<AgentClientValidationException>(() => HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source)).Message, StringComparison.Ordinal);
            return;
        }
        Assert.Empty(diagnostics);
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source);
        Assert.Equal(Settings.ApiBaseUrl, adopted.Outbound!.ApiBaseUrl);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings, fixture.Source);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Assert.Contains("highest command-line precedence", Assert.Single(proposal.ReviewSettings, setting => setting.Key == "Downstream identity target").Source);
    }

    [Fact]
    public void Highest_command_whitespace_path_selects_legacy_instead_of_lower_nonempty_canonical_path()
    {
        using var fixture = new Fixture();
        var source = Complete();
        const string canonical = "/run/lower-canonical/config.json";
        Env(source, "mcp-http")["Helpdesk__Mcp__ConfigurationPath"] = canonical;
        source["services"]!["mcp-http"]!["command"] = new JsonArray("--Helpdesk:Mcp:ConfigurationPath", " ");
        Files(source, "mcp-http").Add(Provider(canonical, "the unselected canonical provider is deliberately invalid JSON"));
        fixture.WriteSource(source);
        Assert.Empty(HttpMcpEffectiveDeployment.Validate(source, Settings));
        var adopted = HttpMcpEffectiveDeployment.Adopt(Settings, fixture.Source);
        Assert.Equal("gateway", adopted.Outbound!.CredentialMode);
        Assert.Equal(Settings.ApiBaseUrl, adopted.Outbound.ApiBaseUrl);
    }

    [Fact]
    public void Runtime_external_issuer_must_equal_the_reviewed_issuer_even_when_both_are_valid()
    {
        using var fixture = new Fixture();
        var external = Settings with
        {
            AuthenticationMode = "authentik", AuthentikAuthority = "https://issuer-a.example.test/application/o/mcp/",
            AuthentikAudience = Settings.PublicResourceUri, RequiredScopes = ["helpdesk.mcp"], RequiredGroups = ["assistant"],
            Outbound = AgentClientConfiguration.Empty with { ApiBaseUrl = Settings.ApiBaseUrl, CredentialMode = "integration", IntegrationCredential = "rdk_fixture_external_identity" }
        };
        var source = Complete();
        Env(source, "web")["WebGateway__Mcp__AuthenticationMode"] = "authentik";
        Env(source, "mcp-http")["Helpdesk__Mcp__AuthenticationMode"] = "authentik";
        Env(source, "mcp-http")["Helpdesk__Mcp__Outbound__CredentialMode"] = "integration";
        Env(source, "mcp-http")["Helpdesk__Mcp__Outbound__IntegrationCredential"] = external.Outbound.IntegrationCredential;
        Env(source, "mcp-http")["Authentication__AuthentikMcp__Authority"] = external.AuthentikAuthority;
        Env(source, "mcp-http")["Authentication__AuthentikMcp__Audience"] = external.AuthentikAudience;
        Env(source, "mcp-http")["Authentication__AuthentikMcp__RequiredScopes__0"] = "helpdesk.mcp";
        Env(source, "mcp-http")["Authentication__AuthentikMcp__RequiredGroups__0"] = "assistant";
        Files(source, "mcp-http")[0] = Provider("/run/rateldesk-mcp/config.json", JsonSerializer.Serialize(external.Outbound, AgentClientConfiguration.JsonOptions));
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(external);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        Env(source, "mcp-http")["Authentication__AuthentikMcp__Authority"] = "https://issuer-b.example.test/application/o/mcp/";
        fixture.WriteRuntime(source);
        var failures = HttpMcpEffectiveDeployment.Observe(proposal, fixture.Runtime);
        Assert.Contains(failures, diagnostic => diagnostic.Code == "HOST_EFFECTIVE_OPTIONS_MISMATCH" && diagnostic.Key == "Authentication:AuthentikMcp");
        Assert.Contains(failures, diagnostic => diagnostic.Code == "HOST_RUNTIME_PROVIDER_MISMATCH" && diagnostic.Layer == "mcp-http");
        Assert.DoesNotContain("rdk_fixture_external_identity", HostDeploymentWorkflow.Describe(failures));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Approval_review_identifies_actual_winning_command_and_environment_providers_with_bounded_nonsecret_values(bool composeCommand)
    {
        using var fixture = new Fixture();
        var source = Complete();
        var arguments = new JsonArray("--ApiBaseUrl=" + Settings.ApiBaseUrl);
        if (composeCommand) source["services"]!["web"]!["command"] = arguments;
        else source["x-rateldesk-image-commands"] = new JsonObject { ["web"] = arguments };
        source["x-rateldesk-source-files"] = new JsonArray(Enumerable.Range(0, 60)
            .Select(index => (JsonNode)new JsonObject { ["path"] = $"/owner/source-{index}.json", ["sha256"] = new string('a', 64) }).ToArray());
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        var command = Assert.Single(proposal.ReviewSettings, setting => setting.Key == "Web API backend");
        Assert.Contains(composeCommand ? "services.web.command" : "web image Config.Cmd", command.Source);
        Assert.Contains("highest", command.Source);
        Assert.Contains("compose.host.json", Assert.Single(proposal.ReviewSettings, setting => setting.Key == "Public Web origin").Source);
        Assert.Contains(proposal.ReviewSettings, setting => setting.Key == "Pairing public origin");
        Assert.Contains(proposal.ReviewSettings, setting => setting.Key == "Downstream identity mode" && setting.Value == "gateway");
        Assert.Contains(proposal.ReviewSettings, setting => setting.Key == "Provider precedence" && setting.Value.Contains("legacy aliases", StringComparison.Ordinal));
        Assert.InRange(proposal.ReviewSettings.Count, 1, 48);
        Assert.All(proposal.ReviewSettings, setting =>
        {
            Assert.InRange(setting.Key.Length, 1, 128);
            Assert.InRange(setting.Value.Length, 0, 2048);
            Assert.InRange(setting.Source.Length, 1, 256);
            Assert.DoesNotContain(setting.Value + setting.Source, char.IsControl);
        });
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("changed")]
    [InlineData("stale")]
    public async Task Post_verifier_observation_command_requires_a_fresh_matching_runtime_without_requesting_more_consent(string capture)
    {
        using var fixture = new Fixture();
        fixture.WriteSource(Complete());
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        using var handler = new ApprovalTransport(proposal);
        using var client = new HttpClient(handler);
        using var workflow = new HostDeploymentWorkflow(Settings.PublicOrigin, client);
        await workflow.SaveApprovedAsync(proposal,
            new(new string('a', 64), "ABCD-EFGH", Settings.PublicOrigin + "/account/integration-credentials/approve?kind=host", DateTimeOffset.UtcNow.AddMinutes(5), 1),
            fixture.ExportDirectory, false, CancellationToken.None);
        var probesCompletedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var originalRuntime = Complete();
        originalRuntime["x-rateldesk-observed-at-utc"] = probesCompletedAt.AddSeconds(-1).ToString("O");
        fixture.WriteRuntime(originalRuntime);
        Assert.Empty(HttpMcpEffectiveDeployment.Observe(proposal, fixture.Runtime));
        SecureConfigurationFiles.WriteAtomic(Path.Combine(fixture.ExportDirectory, "host-phase.json"), new JsonObject
        {
            ["phase"] = "verification-probes-passed", ["probesCompletedAtUtc"] = probesCompletedAt.ToString("O"),
            ["sourceHash"] = proposal.SourceHash, ["proposalHash"] = proposal.ProposalHash
        }.ToJsonString());
        Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.RuntimeSourceVariable, fixture.Runtime);
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] arguments = ["host", "observation-check", "--export-directory", fixture.ExportDirectory];
        // The host has captured the actual containers again after the verifier
        // returned. Changed options and the earlier capture both prevent success.
        var runtime = capture == "stale" ? originalRuntime : Complete();
        if (capture != "stale") runtime["x-rateldesk-observed-at-utc"] = DateTimeOffset.UtcNow.ToString("O");
        if (capture == "changed") Env(runtime, "mcp-http")["Helpdesk__Mcp__DelegationTimeoutSeconds"] = "35";
        fixture.WriteRuntime(runtime);
        Assert.Equal(capture == "fresh" ? 0 : SetupRunner.ConfigurationError,
            await SetupCommands.InvokeAsync(arguments, SetupTransport.Http, output, error));
        if (capture == "changed") Assert.Contains("HOST_RUNTIME_MISMATCH", error.ToString());
        Assert.DoesNotContain("Temporary verification client", output.ToString() + error.ToString());
        Assert.Equal(capture == "fresh", File.ReadAllText(Path.Combine(fixture.ExportDirectory, "host-phase.json")).Contains("end-to-end-verified", StringComparison.Ordinal));
        Assert.Equal(1, handler.Calls); // Only the original host approval poll used this transport.
    }

    [Fact]
    public async Task New_verification_with_mismatched_runtime_clears_a_previous_success_before_requesting_consent()
    {
        using var fixture = new Fixture();
        fixture.WriteSource(Complete());
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        using var handler = new ApprovalTransport(proposal);
        using var client = new HttpClient(handler);
        using var workflow = new HostDeploymentWorkflow(Settings.PublicOrigin, client);
        await workflow.SaveApprovedAsync(proposal,
            new(new string('a', 64), "ABCD-EFGH", Settings.PublicOrigin + "/account/integration-credentials/approve?kind=host", DateTimeOffset.UtcNow.AddMinutes(5), 1),
            fixture.ExportDirectory, false, CancellationToken.None);
        var phasePath = Path.Combine(fixture.ExportDirectory, "host-phase.json");
        SecureConfigurationFiles.WriteAtomic(phasePath, """{"phase":"end-to-end-verified"}""");
        var runtime = Complete();
        Env(runtime, "mcp-http")["Helpdesk__Mcp__DelegationTimeoutSeconds"] = "35";
        fixture.WriteRuntime(runtime);
        Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.RuntimeSourceVariable, fixture.Runtime);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(SetupRunner.ConfigurationError, await HostDeploymentWorkflow.VerifyAsync(fixture.ExportDirectory, output, error, CancellationToken.None));
        Assert.Contains("HOST_RUNTIME_MISMATCH", error.ToString());
        Assert.DoesNotContain("end-to-end-verified", File.ReadAllText(phasePath));
        Assert.DoesNotContain("Temporary verification client", output.ToString() + error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    private static async Task<HttpMcpDeploymentProposal> PrepareManagerImportAsync(Fixture fixture)
    {
        var source = Complete();
        source["x-rateldesk-source-files"] = new JsonArray(new JsonObject { ["path"] = "/owner/compose.json", ["sha256"] = new string('d', 64) });
        fixture.WriteSource(source);
        var proposal = HttpMcpEffectiveDeployment.Create(Settings);
        Assert.True(proposal.Valid, HostDeploymentWorkflow.Describe(proposal.Diagnostics));
        using var handler = new ApprovalTransport(proposal);
        using var client = new HttpClient(handler);
        using var workflow = new HostDeploymentWorkflow(Settings.PublicOrigin, client);
        await workflow.SaveApprovedAsync(proposal,
            new(new string('a', 64), "ABCD-EFGH", Settings.PublicOrigin + "/account/integration-credentials/approve?kind=host", DateTimeOffset.UtcNow.AddMinutes(5), 1),
            fixture.ExportDirectory, false, CancellationToken.None);
        // The manager replaces its one original source with the exact exported
        // complete document. The rendered providers still match this review.
        source = proposal.CompleteDeployment.DeepClone().AsObject();
        var document = new JsonObject { ["services"] = source["services"]!.DeepClone(), ["volumes"] = source["volumes"]!.DeepClone() }.ToJsonString();
        SecureConfigurationFiles.WritePrivateDocument(Path.Combine(fixture.ExportDirectory, "deployment.json"), document);
        var documentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
        SecureConfigurationFiles.WritePrivateDocument(Path.Combine(fixture.ExportDirectory, "launcher-state.json"),
            new JsonObject { ["sourceHash"] = proposal.SourceHash, ["deploymentSha256"] = documentHash }.ToJsonString());
        source["x-rateldesk-source-files"]![0]!["sha256"] = documentHash;
        Assert.Empty(HttpMcpEffectiveDeployment.CheckApprovedEffective(proposal, source));
        fixture.WriteSource(source);
        return proposal;
    }

    private static JsonObject Env(JsonObject source, string service) => source["services"]![service]!["environment"]!.AsObject();
    private static void SelectProviderRoot(JsonObject source, string service, string provider, string root)
    {
        switch (provider)
        {
            case "command-equals": source["services"]![service]!["command"] = new JsonArray("--contentRoot=" + root); break;
            case "command-two-token": source["services"]![service]!["command"] = new JsonArray("--contentRoot", root); break;
            case "image-command":
                source["x-rateldesk-image-commands"] ??= new JsonObject();
                source["x-rateldesk-image-commands"]![service] = new JsonArray("--contentRoot=" + root); break;
            case "dotnet-environment": Env(source, service)["DOTNET_CONTENTROOT"] = root; break;
            case "aspnetcore-environment": Env(source, service)["ASPNETCORE_CONTENTROOT"] = root; break;
            case "compose-directory": source["services"]![service]!["working_dir"] = root; break;
            case "image-directory":
                source["x-rateldesk-image-working-directories"] ??= new JsonObject();
                source["x-rateldesk-image-working-directories"]![service] = root; break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }
    private static JsonArray Files(JsonObject source, string service) => source["x-rateldesk-protected-configuration"]![service]!.AsArray();
    private static JsonObject Provider(string path, string content) => new() { ["path"] = path, ["content"] = content, ["source"] = "synthetic private provider", ["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))) };

    internal static JsonObject Complete()
    {
        var mcp = new JsonObject
        {
            ["ASPNETCORE_URLS"] = Settings.Listener,
            ["Helpdesk__Mcp__Instance"] = Settings.Instance,
            ["Helpdesk__Mcp__ExpectedApiBaseUrl"] = Settings.ApiBaseUrl,
            ["Helpdesk__Mcp__PublicResourceUri"] = Settings.PublicResourceUri,
            ["Helpdesk__Mcp__AuthenticationMode"] = "gateway",
            ["Helpdesk__Mcp__AllowedOrigins__0"] = Settings.PublicOrigin,
            ["Helpdesk__Mcp__DelegationTimeoutSeconds"] = "20",
            ["Helpdesk__Mcp__ConfigurationPath"] = "/run/rateldesk-mcp/config.json",
            ["RATELDESK_MCP_CONFIG"] = "/run/rateldesk-mcp/config.json",
            ["Helpdesk__Mcp__Outbound__ApiBaseUrl"] = Settings.ApiBaseUrl,
            ["Helpdesk__Mcp__Outbound__CredentialMode"] = "gateway",
            ["RATELDESK_MCP_HOST_DIRECTORY"] = "/output",
            ["RATELDESK_MCP_CLIENT_DIRECTORY"] = "/var/lib/rateldesk/http-client"
        };
        foreach (var key in new[] { "IntegrationCredential", "IntegrationCredentialFile", "AuthentikTokenUrl", "AuthentikClientId", "AuthentikUsername", "AuthentikAppPassword", "AuthentikAppPasswordFile", "CredentialTarget" })
            mcp["Helpdesk__Mcp__Outbound__" + key] = "";
        JsonObject Service(string name, JsonObject environment) => new() { ["image"] = "synthetic-" + name + ":fixture", ["environment"] = environment, ["volumes"] = new JsonArray() };
        var services = new JsonObject
        {
            ["api"] = Service("api", new() { ["IntegrationCredentials__Pairing__PublicOrigin"] = Settings.PublicOrigin }),
            ["web"] = Service("web", new()
            {
                ["ApiBaseUrl"] = Settings.ApiBaseUrl, ["WebGateway__PublicOrigin"] = Settings.PublicOrigin,
                ["WebGateway__Mcp__Enabled"] = "true", ["WebGateway__Mcp__Destination"] = Settings.McpDestination,
                ["WebGateway__Mcp__AuthenticationMode"] = "gateway"
            }),
            ["mcp-http"] = Service("mcp-http", mcp)
        };
        services["mcp-http"]!["volumes"] = new JsonArray(
            new JsonObject { ["type"] = "volume", ["source"] = "host-store", ["target"] = "/output" },
            new JsonObject { ["type"] = "volume", ["source"] = "client-store", ["target"] = "/var/lib/rateldesk/http-client" },
            new JsonObject { ["type"] = "volume", ["source"] = "mcp-config", ["target"] = "/run/rateldesk-mcp", ["read_only"] = true });
        return new()
        {
            ["services"] = services,
            ["volumes"] = new JsonObject { ["host-store"] = new JsonObject { ["name"] = "fixture-host-data" }, ["client-store"] = new JsonObject { ["name"] = "fixture-client-data" } },
            ["x-rateldesk-image-environment"] = new JsonObject { ["api"] = new JsonArray(), ["web"] = new JsonArray(), ["mcp-http"] = new JsonArray() },
            ["x-rateldesk-image-ids"] = new JsonObject { ["api"] = "sha256:" + new string('a', 64), ["web"] = "sha256:" + new string('b', 64), ["mcp-http"] = "sha256:" + new string('c', 64) },
            ["x-rateldesk-protected-configuration"] = new JsonObject
            {
                ["api"] = new JsonArray(), ["web"] = new JsonArray(),
                ["mcp-http"] = new JsonArray(Provider("/run/rateldesk-mcp/config.json", JsonSerializer.Serialize(AgentClientConfiguration.Empty with { ApiBaseUrl = Settings.ApiBaseUrl, CredentialMode = "gateway" }, AgentClientConfiguration.JsonOptions)))
            },
            ["x-rateldesk-storage"] = new JsonObject
            {
                ["host"] = new JsonObject { ["type"] = "volume", ["source"] = "fixture-host-data", ["target"] = "/output", ["readOnly"] = false, ["private"] = true, ["uid"] = 10001 },
                ["client"] = new JsonObject { ["type"] = "volume", ["source"] = "fixture-client-data", ["target"] = "/var/lib/rateldesk/http-client", ["readOnly"] = false, ["private"] = true, ["uid"] = 10001 }
            }
        };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rateldesk-effective-" + Guid.NewGuid().ToString("N"));
        private readonly string? previousSource = Environment.GetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable);
        private readonly string? previousRuntime = Environment.GetEnvironmentVariable(HttpMcpEffectiveDeployment.RuntimeSourceVariable);
        public string Source => Path.Combine(directory, "effective.json");
        public string Runtime => Path.Combine(directory, "runtime.json");
        public string ExportDirectory => Path.Combine(directory, "export");
        public Fixture()
        {
            Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable, Source);
            Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.RuntimeSourceVariable, null);
        }
        public void WriteSource(JsonObject source) => SecureConfigurationFiles.WriteAtomic(Source, source.ToJsonString());
        public void WriteRuntime(JsonObject runtime) => SecureConfigurationFiles.WriteAtomic(Runtime, runtime.ToJsonString());
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable, previousSource);
            Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.RuntimeSourceVariable, previousRuntime);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class ApprovalTransport(HttpMcpDeploymentProposal proposal) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.EndsWith("/host-proposals/poll", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new HostDeploymentApprovalStatus("approved", proposal.ProposalHash, proposal.SourceHash, "fixture-administrator", DateTimeOffset.UtcNow))
            });
        }
    }
}

