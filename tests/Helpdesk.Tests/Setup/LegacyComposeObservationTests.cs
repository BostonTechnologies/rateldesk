using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Setup;

namespace Helpdesk.Tests.Setup;

public sealed class LegacyComposeObservationTests
{
    [Theory]
    [InlineData("initializer-network")]
    [InlineData("dependency-required")]
    [InlineData("volume-name")]
    [InlineData("mount-default")]
    [InlineData("service-network")]
    public void Fresh_owner_projections_accept_equivalent_legacy_rendering_without_weakening_the_raw_checks(string normalization)
    {
        using var fixture = new Fixture();
        Normalize(fixture.Current, normalization);
        // With no explicit fresh observation, the original exact comparison is retained.
        Assert.Contains(HttpMcpEffectiveDeployment.CheckApprovedEffective(fixture.Proposal, fixture.Current),
            diagnostic => diagnostic.Code == "HOST_SOURCE_CHANGED");
        fixture.Observe(Plain(fixture.Current), Plain(fixture.Current));
        Assert.Empty(HttpMcpEffectiveDeployment.CheckApprovedEffective(fixture.Proposal, fixture.Current));
    }

    [Theory]
    [InlineData("initializer-network")]
    [InlineData("initializer-dependency")]
    [InlineData("initializer-image")]
    [InlineData("mount-source")]
    [InlineData("unrelated-environment")]
    [InlineData("resource-definition")]
    [InlineData("project-name")]
    [InlineData("unknown-root-field")]
    public void Fresh_owner_projections_reject_every_actual_topology_or_environment_change(string change)
    {
        using var fixture = new Fixture();
        var expected = Plain(fixture.Current);
        var actual = expected.DeepClone().AsObject();
        switch (change)
        {
            case "initializer-network": actual["services"]!["mcp-config-init"]!["networks"] = new JsonObject { ["unreviewed"] = null }; break;
            case "initializer-dependency": actual["services"]!["mcp-config-init"]!["depends_on"] = new JsonObject { ["api"] = new JsonObject { ["condition"] = "service_started" } }; break;
            case "initializer-image": actual["services"]!["mcp-config-init"]!["image"] = "unreviewed:fixture"; break;
            case "mount-source": actual["services"]!["mcp-http"]!["volumes"]![0]!["source"] = "unreviewed"; break;
            case "unrelated-environment": actual["services"]!["api"]!["environment"]!["UNRELATED_PRIVATE_SETTING"] = "changed"; break;
            case "resource-definition": actual["volumes"]!["mcp-config"] = new JsonObject { ["name"] = "unreviewed" }; break;
            case "project-name": actual["name"] = "unreviewed-project"; break;
            case "unknown-root-field": actual["unreviewed-owner-field"] = "changed"; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        fixture.SetCurrentCompose(actual);
        fixture.Observe(expected, actual);
        Assert.Contains(HttpMcpEffectiveDeployment.CheckApprovedEffective(fixture.Proposal, fixture.Current),
            diagnostic => diagnostic.Code == "HOST_SOURCE_CHANGED");
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("image-id")]
    [InlineData("mount")]
    [InlineData("initializer-command")]
    public void A_valid_fresh_observation_does_not_replace_existing_raw_provider_image_mount_or_initializer_validation(string change)
    {
        using var fixture = new Fixture();
        switch (change)
        {
            case "provider": fixture.Current["x-rateldesk-protected-configuration"]!["api"]!.AsArray().Add(new JsonObject
                { ["path"] = "/app/appsettings.json", ["content"] = "{\"Helpdesk\":{\"Mcp\":{\"DelegationTimeoutSeconds\":42}}}" }); break;
            case "image-id": fixture.Current["x-rateldesk-image-ids"]!["mcp-http"] = "sha256:" + new string('f', 64); break;
            case "mount": fixture.Current["services"]!["mcp-http"]!["volumes"]![0]!["source"] = "unreviewed"; break;
            case "initializer-command": fixture.Current["services"]!["mcp-config-init"]!["command"] = new JsonArray("unreviewed"); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        fixture.Observe(Plain(fixture.Current), Plain(fixture.Current));
        Assert.NotEmpty(HttpMcpEffectiveDeployment.CheckApprovedEffective(fixture.Proposal, fixture.Current));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("expected-digest")]
    [InlineData("actual-digest")]
    [InlineData("raw-digest")]
    [InlineData("deployment-digest")]
    [InlineData("overlay-digest")]
    [InlineData("partial-overlay-proof")]
    [InlineData("expected-tamper")]
    [InlineData("actual-tamper")]
    [InlineData("raw-tamper")]
    [InlineData("deployment-tamper")]
    [InlineData("frozen-source-tamper")]
    [InlineData("approved-model-tamper")]
    [InlineData("receipt-source")]
    [InlineData("receipt-proposal")]
    [InlineData("receipt-export")]
    [InlineData("receipt-intent")]
    [InlineData("receipt-unapproved")]
    [InlineData("receipt-malformed")]
    [InlineData("expected-metadata")]
    [InlineData("actual-metadata")]
    [InlineData("different-attempt")]
    [InlineData("outside-parent")]
    [InlineData("missing-file")]
    [InlineData("current-image")]
    [InlineData("current-topology")]
    [InlineData("current-environment")]
    [InlineData("current-unknown-field")]
    [InlineData("empty-projections")]
    public void Invalid_or_incomplete_observations_fail_closed_with_a_safe_bounded_error(string failure)
    {
        using var fixture = new Fixture();
        fixture.Observe(Plain(fixture.Current), Plain(fixture.Current));
        const string secret = "private-observation-marker-do-not-print";
        switch (failure)
        {
            case "partial": Environment.SetEnvironmentVariable(LegacyComposeObservation.ActualHashVariable, null); break;
            case "expected-digest": Environment.SetEnvironmentVariable(LegacyComposeObservation.ExpectedHashVariable, secret); break;
            case "actual-digest": Environment.SetEnvironmentVariable(LegacyComposeObservation.ActualHashVariable, new string('f', 64)); break;
            case "raw-digest": Environment.SetEnvironmentVariable(LegacyComposeObservation.ApprovedRawHashVariable, new string('f', 64)); break;
            case "deployment-digest": Environment.SetEnvironmentVariable(LegacyComposeObservation.ReviewedDeploymentHashVariable, new string('f', 64)); break;
            case "overlay-digest": Environment.SetEnvironmentVariable(LegacyComposeObservation.ApprovedOverlayHashVariable, new string('f', 64)); break;
            case "partial-overlay-proof": Environment.SetEnvironmentVariable(LegacyComposeObservation.ApprovedOverlayHashVariable, null); break;
            case "expected-tamper": SecureConfigurationFiles.WritePrivateDocument(fixture.ExpectedPath, "{\"private\":\"" + secret + "\"}"); break;
            case "actual-tamper": SecureConfigurationFiles.WritePrivateDocument(fixture.ActualPath, "{\"private\":\"" + secret + "\"}"); break;
            case "raw-tamper": SecureConfigurationFiles.WritePrivateDocument(fixture.RawPath, "{\"private\":\"" + secret + "\"}"); break;
            case "deployment-tamper": SecureConfigurationFiles.WritePrivateDocument(fixture.DeploymentPath, "{\"private\":\"" + secret + "\"}"); break;
            case "frozen-source-tamper": SecureConfigurationFiles.WritePrivateDocument(fixture.SourcePath, "{\"private\":\"" + secret + "\"}"); break;
            case "approved-model-tamper": fixture.Proposal.CompleteDeployment["services"]!["api"]!["image"] = secret; break;
            case "receipt-source": fixture.ChangeReceipt(receipt => receipt["sourceHash"] = new string('f', 64)); break;
            case "receipt-proposal": fixture.ChangeReceipt(receipt => receipt["proposalHash"] = new string('f', 64)); break;
            case "receipt-export": fixture.ChangeReceipt(receipt => receipt["exportHashes"]!["approved-proposal.json"] = new string('f', 64)); break;
            case "receipt-intent": fixture.ChangeReceipt(receipt => receipt["intent"] = (int)HttpMcpHostIntent.VerifyExisting); break;
            case "receipt-unapproved": fixture.ChangeReceipt(receipt => receipt["approved"] = false); break;
            case "receipt-malformed": SecureConfigurationFiles.WriteAtomic(fixture.ReceiptPath, secret); break;
            case "expected-metadata":
                var expected = Plain(fixture.Current); expected["x-rateldesk-untrusted"] = secret;
                fixture.Observe(expected, Plain(fixture.Current)); break;
            case "actual-metadata":
                var actual = Plain(fixture.Current); actual["x-rateldesk-untrusted"] = secret;
                fixture.Observe(Plain(fixture.Current), actual); break;
            case "different-attempt":
                var other = Path.Combine(fixture.DirectoryPath, ".legacy-observation-" + Guid.NewGuid().ToString("N") + "-actual.json");
                SecureConfigurationFiles.WritePrivateDocument(other, SecureConfigurationFiles.ReadPrivateDocument(fixture.ActualPath));
                Environment.SetEnvironmentVariable(LegacyComposeObservation.ActualSourceVariable, other); break;
            case "outside-parent": Environment.SetEnvironmentVariable(LegacyComposeObservation.ExpectedSourceVariable, Path.Combine(fixture.DirectoryPath, "outside", Path.GetFileName(fixture.ExpectedPath))); break;
            case "missing-file": File.Delete(fixture.ActualPath); break;
            case "current-image": fixture.Current["services"]!["api"]!["image"] = secret; break;
            case "current-topology": fixture.Current["services"]!["api"]!["labels"] = new JsonObject { ["unreviewed"] = secret }; break;
            case "current-environment": fixture.Current["services"]!["api"]!["environment"]!["UNRELATED_PRIVATE_SETTING"] = secret; break;
            case "current-unknown-field": fixture.Current["unreviewed-owner-field"] = secret; break;
            case "empty-projections": fixture.Observe(new JsonObject(), new JsonObject()); break;
            default: throw new ArgumentOutOfRangeException(nameof(failure));
        }
        var error = Assert.Throws<AgentClientValidationException>(() => HttpMcpEffectiveDeployment.CheckApprovedEffective(fixture.Proposal, fixture.Current));
        Assert.StartsWith("HOST_COMPOSE_OBSERVATION_INVALID:", error.Message);
        Assert.DoesNotContain(secret, error.Message);
        Assert.DoesNotContain(fixture.DirectoryPath, error.Message);
        Assert.True(error.Message.Length < 300);
    }

    [Fact]
    public void Normal_changes_keep_the_original_strict_comparison_and_ignore_legacy_observation_inputs()
    {
        using var fixture = new Fixture();
        var normal = HttpMcpEffectiveDeployment.Create(HttpMcpEffectiveDeploymentTests.Settings, fixture.SourcePath,
            HttpMcpHostIntent.PrepareChanges, renderSourceReview: false);
        var current = normal.CompleteDeployment.DeepClone().AsObject();
        current["services"]!["api"]!["labels"] = new JsonObject { ["unreviewed"] = "true" };
        Environment.SetEnvironmentVariable(LegacyComposeObservation.ExpectedHashVariable, "invalid-legacy-proof");
        Assert.Contains(HttpMcpEffectiveDeployment.CheckApprovedEffective(normal, current), diagnostic => diagnostic.Code == "HOST_SOURCE_CHANGED");
    }

    [Fact]
    public void Export_validation_uses_only_fresh_rendered_compose_fields_when_unused_resources_are_pruned()
    {
        using var fixture = new Fixture(unusedResource: true);
        var rendered = Plain(fixture.Current); rendered.Remove("configs");
        Assert.Contains(HttpMcpEffectiveDeployment.ValidateExport(fixture.Proposal, rendered), diagnostic => diagnostic.Code == "HOST_SOURCE_CHANGED");
        fixture.Observe(rendered, rendered);
        Assert.Empty(HttpMcpEffectiveDeployment.ValidateExport(fixture.Proposal, rendered));
        // Exported metadata never replaces the reviewed private provider capture.
        rendered["x-rateldesk-protected-configuration"] = new JsonObject();
        Assert.Empty(HttpMcpEffectiveDeployment.ValidateExport(fixture.Proposal, rendered));
    }

    [Theory]
    [InlineData("unused-config")]
    [InlineData("project-name")]
    public void A_rebased_mutable_state_cannot_authorize_source_only_export_edits_pruned_from_the_fresh_observation(string change)
    {
        using var fixture = new Fixture(unusedResource: true);
        var rendered = Plain(fixture.Current); rendered.Remove("configs");
        fixture.Observe(rendered, rendered);
        var raw = JsonNode.Parse(SecureConfigurationFiles.ReadPrivateDocument(fixture.DeploymentPath))!.AsObject();
        if (change == "unused-config") raw["configs"]!["unused-config"]!["file"] = "/owner/private/unreviewed.json";
        else raw["name"] = "unreviewed-project";
        var changedHash = SecureConfigurationFiles.WritePrivateDocument(fixture.DeploymentPath, raw.ToJsonString());
        SecureConfigurationFiles.WriteAtomic(Path.Combine(fixture.DirectoryPath, "launcher-state.json"),
            new JsonObject { ["deploymentSha256"] = changedHash, ["sourceHash"] = fixture.Proposal.SourceHash }.ToJsonString());
        var error = Assert.Throws<AgentClientValidationException>(() => HttpMcpEffectiveDeployment.ValidateExport(fixture.Proposal, rendered));
        Assert.StartsWith("HOST_COMPOSE_OBSERVATION_INVALID:", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preapply_resume_validates_the_original_approved_overlay_without_comparing_unapplied_runtime_topology(bool observe)
    {
        using var fixture = new Fixture();
        if (observe) fixture.Observe(Plain(fixture.Current), Plain(fixture.Current));
        // The source still has three services, while the reviewed legacy
        // generation has four. Approval checking must not pretend it is applied.
        Assert.Equal(3, JsonNode.Parse(SecureConfigurationFiles.ReadPrivateDocument(fixture.SourcePath))!["services"]!.AsObject().Count);
        Assert.Equal(4, fixture.Proposal.CompleteDeployment["services"]!.AsObject().Count);
        var resumed = HostDeploymentWorkflow.ResumeApproved(fixture.DirectoryPath);
        Assert.Equal(fixture.Proposal.ProposalHash, resumed.ProposalHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unused_patch_addition_cannot_rebase_its_receipt_before_or_after_source_import(bool observe)
    {
        using var fixture = new Fixture();
        if (observe) fixture.Observe(Plain(fixture.Current), Plain(fixture.Current));
        var patch = JsonNode.Parse(SecureConfigurationFiles.ReadPrivateDocument(fixture.OverlayPath))!.AsObject();
        patch["configs"] = new JsonObject { ["unapproved-unused"] = new JsonObject { ["file"] = "/owner/private/unapproved.json" } };
        var changedHash = SecureConfigurationFiles.WritePrivateDocument(fixture.OverlayPath, patch.ToJsonString());
        fixture.ChangeReceipt(receipt => receipt["exportHashes"]!["compose.host.json"] = changedHash);
        if (observe) Environment.SetEnvironmentVariable(LegacyComposeObservation.ApprovedOverlayHashVariable, changedHash);
        var error = Assert.Throws<AgentClientValidationException>(() => HostDeploymentWorkflow.ResumeApproved(fixture.DirectoryPath));
        Assert.StartsWith("HOST_COMPOSE_OBSERVATION_INVALID:", error.Message);
        if (observe)
            Assert.StartsWith("HOST_COMPOSE_OBSERVATION_INVALID:", Assert.Throws<AgentClientValidationException>(() =>
                HttpMcpEffectiveDeployment.CheckApprovedEffective(fixture.Proposal, fixture.Current)).Message);
    }

    [Fact]
    public void A_changed_host_patch_hash_cannot_use_an_unchanged_private_volume_patch_or_receipt()
    {
        using var fixture = new Fixture();
        fixture.Observe(Plain(fixture.Current), Plain(fixture.Current));
        var hostPatch = fixture.Proposal.Overlay.DeepClone().AsObject();
        hostPatch["configs"] = new JsonObject { ["unapproved-unused"] = new JsonObject { ["file"] = "/owner/private/unapproved.json" } };
        Environment.SetEnvironmentVariable(LegacyComposeObservation.ApprovedOverlayHashVariable, HttpMcpEffectiveDeployment.Hash(hostPatch.ToJsonString()));
        Assert.StartsWith("HOST_COMPOSE_OBSERVATION_INVALID:", Assert.Throws<AgentClientValidationException>(() =>
            HostDeploymentWorkflow.ResumeApproved(fixture.DirectoryPath)).Message);
        Assert.StartsWith("HOST_COMPOSE_OBSERVATION_INVALID:", Assert.Throws<AgentClientValidationException>(() =>
            HttpMcpEffectiveDeployment.CheckApprovedEffective(fixture.Proposal, fixture.Current)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rebased_settings_or_outbound_receipts_cannot_replace_the_freshly_reconstructed_legacy_generation(bool outboundSecret)
    {
        using var fixture = new Fixture(externalIdentity: outboundSecret);
        var path = Path.Combine(fixture.DirectoryPath, outboundSecret ? "mcp-outbound.json" : "host-settings.json");
        var changed = JsonNode.Parse(SecureConfigurationFiles.ReadPrivateDocument(path))!.AsObject();
        const string secret = "rdk_fixture_unapproved_identity";
        if (outboundSecret) changed["integrationCredential"] = secret;
        else changed["delegationTimeoutSeconds"] = 30;
        var changedHash = SecureConfigurationFiles.WritePrivateDocument(path, changed.ToJsonString());
        fixture.ChangeReceipt(receipt => receipt["exportHashes"]![Path.GetFileName(path)] = changedHash);
        if (outboundSecret)
        {
            var reconstructed = HttpMcpEffectiveDeployment.Create(HttpMcpHostingCoordinator.Load(fixture.DirectoryPath),
                fixture.SourcePath, HttpMcpHostIntent.LegacyInitializer);
            Assert.True(JsonNode.DeepEquals(fixture.Proposal.Overlay, reconstructed.Overlay));
            Assert.NotEqual(fixture.Proposal.ProposalHash, reconstructed.ProposalHash);
        }
        var error = Assert.Throws<AgentClientValidationException>(() => HostDeploymentWorkflow.ResumeApproved(fixture.DirectoryPath));
        Assert.StartsWith("HOST_SOURCE_CHANGED:", error.Message);
        Assert.DoesNotContain(secret, error.Message);
    }

    private static JsonObject Plain(JsonObject model)
    {
        var result = model.DeepClone().AsObject();
        foreach (var key in result.Select(entry => entry.Key).Where(key => key.StartsWith("x-rateldesk-", StringComparison.Ordinal)).ToArray()) result.Remove(key);
        return result;
    }

    private static void Normalize(JsonObject model, string normalization)
    {
        switch (normalization)
        {
            case "initializer-network": model["services"]!["mcp-config-init"]!["networks"] = new JsonObject { ["default"] = null }; break;
            case "dependency-required": model["services"]!["mcp-http"]!["depends_on"]!["mcp-config-init"]!["required"] = true; break;
            case "volume-name": model["volumes"]!["mcp-config"]!["name"] = "fixture_mcp-config"; break;
            case "mount-default": model["services"]!["mcp-http"]!["volumes"]![0]!["volume"] = new JsonObject { ["nocopy"] = false }; break;
            case "service-network": model["services"]!["api"]!["networks"] = new JsonObject { ["default"] = null }; break;
            default: throw new ArgumentOutOfRangeException(nameof(normalization));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] Variables = [LegacyComposeObservation.ApprovedRawHashVariable,
            LegacyComposeObservation.ExpectedSourceVariable, LegacyComposeObservation.ExpectedHashVariable,
            LegacyComposeObservation.ActualSourceVariable, LegacyComposeObservation.ActualHashVariable,
            LegacyComposeObservation.ReviewedDeploymentHashVariable, LegacyComposeObservation.ApprovedOverlayHashVariable];
        private readonly string?[] previous = Variables.Select(Environment.GetEnvironmentVariable).ToArray();
        private readonly string? previousSource = Environment.GetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable);
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "rateldesk-compose-observation-" + Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(DirectoryPath, "approved-effective-deployment.json");
        public string RawPath => Path.Combine(DirectoryPath, "approved-proposal.json");
        public string DeploymentPath => Path.Combine(DirectoryPath, "deployment.json");
        public string OverlayPath => Path.Combine(DirectoryPath, "compose.host.json");
        public string ReceiptPath => Path.Combine(DirectoryPath, "host-approval.json");
        public string ExpectedPath { get; }
        public string ActualPath { get; }
        public HttpMcpDeploymentProposal Proposal { get; }
        public JsonObject Current { get; }

        public Fixture(bool unusedResource = false, bool externalIdentity = false)
        {
            foreach (var variable in Variables) Environment.SetEnvironmentVariable(variable, null);
            var stem = Path.Combine(DirectoryPath, ".legacy-observation-" + Guid.NewGuid().ToString("N"));
            ExpectedPath = stem + "-expected.json"; ActualPath = stem + "-actual.json";
            var source = HttpMcpEffectiveDeploymentTests.Complete(); source["name"] = "fixture";
            var settings = HttpMcpEffectiveDeploymentTests.Settings;
            if (externalIdentity)
            {
                settings = settings with { AuthenticationMode = "authentik", AuthentikAuthority = "https://issuer.example.test/application/o/mcp/",
                    AuthentikAudience = settings.PublicResourceUri, RequiredScopes = ["helpdesk.mcp"], RequiredGroups = ["assistant"],
                    Outbound = AgentClientConfiguration.Empty with { ApiBaseUrl = settings.ApiBaseUrl, CredentialMode = "integration", IntegrationCredential = "rdk_fixture_approved_identity" } };
                var mcp = source["services"]!["mcp-http"]!["environment"]!;
                mcp["Helpdesk__Mcp__AuthenticationMode"] = "authentik";
                mcp["Helpdesk__Mcp__Outbound__CredentialMode"] = "integration";
                mcp["Helpdesk__Mcp__Outbound__IntegrationCredential"] = settings.Outbound!.IntegrationCredential;
                mcp["Authentication__AuthentikMcp__Authority"] = settings.AuthentikAuthority;
                mcp["Authentication__AuthentikMcp__Audience"] = settings.AuthentikAudience;
                mcp["Authentication__AuthentikMcp__RequiredScopes__0"] = "helpdesk.mcp";
                mcp["Authentication__AuthentikMcp__RequiredGroups__0"] = "assistant";
                source["services"]!["web"]!["environment"]!["WebGateway__Mcp__AuthenticationMode"] = "authentik";
            }
            if (unusedResource) source["configs"] = new JsonObject { ["unused-config"] = new JsonObject { ["file"] = "/owner/private/unused.json" } };
            SecureConfigurationFiles.WritePrivateDocument(SourcePath, source.ToJsonString());
            Proposal = HttpMcpEffectiveDeployment.Create(settings, SourcePath, HttpMcpHostIntent.LegacyInitializer);
            Assert.True(Proposal.Valid, HostDeploymentWorkflow.Describe(Proposal.Diagnostics));
            var export = HttpMcpHostingCoordinator.Export(Proposal.Settings, DirectoryPath, overwrite: true, Proposal.Overlay);
            Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable, SourcePath);
            Current = Proposal.CompleteDeployment.DeepClone().AsObject();
            SecureConfigurationFiles.WritePrivateDocument(RawPath, Proposal.CompleteDeployment.ToJsonString());
            SecureConfigurationFiles.WritePrivateDocument(DeploymentPath, Plain(Current).ToJsonString());
            var hashes = export.Files.Concat([SourcePath, RawPath]).ToDictionary(path => Path.GetFileName(path)!,
                path => HttpMcpEffectiveDeployment.Hash(SecureConfigurationFiles.ReadPrivateDocument(path)), StringComparer.Ordinal);
            var receipt = new HostApprovalReceipt(true, Proposal.SourceHash, Proposal.ProposalHash, "synthetic-administrator",
                DateTimeOffset.UtcNow, null, hashes)
                { Intent = HttpMcpHostIntent.LegacyInitializer };
            SecureConfigurationFiles.WriteAtomic(ReceiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        public void Observe(JsonObject expected, JsonObject actual)
        {
            var expectedHash = SecureConfigurationFiles.WritePrivateDocument(ExpectedPath, expected.ToJsonString());
            var actualHash = SecureConfigurationFiles.WritePrivateDocument(ActualPath, actual.ToJsonString());
            Environment.SetEnvironmentVariable(LegacyComposeObservation.ApprovedRawHashVariable,
                HttpMcpEffectiveDeployment.Hash(SecureConfigurationFiles.ReadPrivateDocument(RawPath)));
            Environment.SetEnvironmentVariable(LegacyComposeObservation.ExpectedSourceVariable, ExpectedPath);
            Environment.SetEnvironmentVariable(LegacyComposeObservation.ExpectedHashVariable, expectedHash);
            Environment.SetEnvironmentVariable(LegacyComposeObservation.ActualSourceVariable, ActualPath);
            Environment.SetEnvironmentVariable(LegacyComposeObservation.ActualHashVariable, actualHash);
            Environment.SetEnvironmentVariable(LegacyComposeObservation.ReviewedDeploymentHashVariable,
                HttpMcpEffectiveDeployment.Hash(SecureConfigurationFiles.ReadPrivateDocument(DeploymentPath)));
            Environment.SetEnvironmentVariable(LegacyComposeObservation.ApprovedOverlayHashVariable,
                HttpMcpEffectiveDeployment.Hash(SecureConfigurationFiles.ReadPrivateDocument(OverlayPath)));
        }

        public void SetCurrentCompose(JsonObject model)
        {
            foreach (var key in Current.Select(entry => entry.Key).Where(key => !key.StartsWith("x-rateldesk-", StringComparison.Ordinal)).ToArray()) Current.Remove(key);
            foreach (var entry in model) Current[entry.Key] = entry.Value?.DeepClone();
        }

        public void ChangeReceipt(Action<JsonObject> change)
        {
            var receipt = JsonNode.Parse(SecureConfigurationFiles.ReadText(ReceiptPath, requirePrivate: true))!.AsObject();
            change(receipt);
            SecureConfigurationFiles.WriteAtomic(ReceiptPath, receipt.ToJsonString());
        }

        public void Dispose()
        {
            for (var index = 0; index < Variables.Length; index++) Environment.SetEnvironmentVariable(Variables[index], previous[index]);
            Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable, previousSource);
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
