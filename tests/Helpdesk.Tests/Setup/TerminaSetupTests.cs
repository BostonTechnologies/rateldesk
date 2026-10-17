using Helpdesk.AgentClient;
using Helpdesk.Setup;
using R3;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Helpdesk.Tests.Setup;

public sealed class TerminaSetupTests
{
    [Theory]
    [InlineData(SetupTransport.Cli)]
    [InlineData(SetupTransport.Stdio)]
    [InlineData(SetupTransport.Http)]
    public async Task First_page_focuses_one_web_url_and_paste_enter_normalizes_resource(SetupTransport transport)
    {
        var workflow = new FakeWorkflow();
        await using var terminal = await Wizard.StartAsync(workflow, new(transport));
        await terminal.WaitStepAsync(SetupStep.Instance);
        Assert.Contains("RatelDesk Web URL", terminal.RecordedRender, StringComparison.Ordinal);
        Assert.Contains("Advanced", terminal.RecordedRender, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile name", terminal.RecordedRender, StringComparison.Ordinal);
        Assert.DoesNotContain("Export directory", terminal.RecordedRender, StringComparison.Ordinal);
        terminal.Input.EnqueueString("https://desk.example:8443/mcp/");
        terminal.Input.EnqueueKey(ConsoleKey.Enter);
        await terminal.WaitStepAsync(SetupStep.Authentication);
        Assert.Equal("https://desk.example:8443", terminal.Model.Instance.Value);
        Assert.Equal(transport, terminal.Model.Transport);
        Assert.Equal(1, workflow.StorageCheckCount);
        Assert.Equal(1, workflow.DiscoveryCount);
    }

    [Fact]
    public async Task Explicit_options_seed_first_url_and_advanced_identity_fields()
    {
        var seed = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://seed.example", AuthentikTokenUrl = "https://auth.example/token", AuthentikClientId = "seed-client", AuthentikUsername = "seed-agent" };
        await using var terminal = await Wizard.StartAsync(new FakeWorkflow(), new(SetupTransport.Stdio, Profile: "custom",
            WebUrl: "https://explicit.example/mcp", InstanceLabel: "custom-instance", Authentication: "authentik", SeedConfiguration: seed));
        Assert.Equal("https://explicit.example/mcp", terminal.Model.Instance.Value);
        Assert.Equal("custom", terminal.Model.Profile.Value);
        Assert.Equal("custom-instance", terminal.Model.InstanceLabel.Value);
        Assert.Equal("seed-client", terminal.Model.ClientId.Value);
        Assert.Equal("authentik", terminal.Model.Authentication);
        terminal.Input.EnqueueKey(ConsoleKey.Enter);
        await terminal.WaitStepAsync(SetupStep.Authentication);
        Assert.Equal("authentik", terminal.Model.Authentication);
        Assert.Equal("seed-agent", terminal.Model.Username.Value);
    }

    [Fact]
    public async Task Known_identity_is_retained_and_storage_failure_precedes_browser_request()
    {
        var stored = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://desk.example", CredentialMode = "integration", IntegrationCredentialFile = "old.secret" };
        var workflow = new FakeWorkflow { Stored = stored, Existing = true, PairingAvailable = true, StorageFailure = true };
        await using var terminal = await Wizard.StartAsync(workflow);
        Assert.Equal("existing", terminal.Model.Authentication);
        terminal.Input.EnqueueKey(ConsoleKey.Enter);
        await terminal.WaitAsync(() => terminal.Model.Status.Value == "Private durable storage is unavailable.");
        Assert.Equal(SetupStep.Instance, terminal.Model.Step.Value);
        Assert.Equal(0, workflow.DiscoveryCount);
        Assert.Equal(0, workflow.StartCount);
    }

    [Fact]
    public async Task Advanced_existing_native_private_API_preserves_target_and_never_sends_identity_to_Web_origin()
    {
        var stored = AgentClientConfiguration.Empty with { ApiBaseUrl = "http://api.internal:8222/api/v1", CredentialMode = "integration", IntegrationCredential = "rdk_existing_private" };
        var workflow = new FakeWorkflow { Stored = stored, Existing = true };
        await using var terminal = await Wizard.StartAsync(workflow, new(SetupTransport.Stdio));
        Assert.Equal("", terminal.Model.Instance.Value);
        Assert.Equal(stored.ApiBaseUrl, terminal.Model.NativeApiTarget.Value);
        await terminal.Application.InvokeAsync(() => terminal.Model.ToggleAdvanced());
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Authentication);
        Assert.Equal(0, workflow.DiscoveryCount);
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Review);
        Assert.Contains(stored.ApiBaseUrl, terminal.Model.Detail.Value, StringComparison.Ordinal);
        await terminal.Application.InvokeAsync(() => terminal.Model.NextAsync(overwriteConfirmed: true));
        await terminal.WaitStepAsync(SetupStep.Complete);
        Assert.Equal(stored.ApiBaseUrl, workflow.LastSaved!.ApiBaseUrl);
        Assert.Equal("rdk_existing_private", workflow.LastSaved.IntegrationCredential);
        Assert.DoesNotContain("rdk_existing_private", terminal.RecordedRender, StringComparison.Ordinal);
        Assert.Equal(0, workflow.StartCount);
    }

    [Fact]
    public async Task Keyboard_navigation_rejects_bad_url_preserves_input_and_cancels_without_saving()
    {
        var workflow = new FakeWorkflow();
        await using var terminal = await Wizard.StartAsync(workflow);
        await terminal.AdvanceToInstanceAsync();
        terminal.Input.EnqueueString("http://insecure.example");
        terminal.Input.EnqueueKey(ConsoleKey.Enter);
        await terminal.WaitAsync(() => terminal.Model.Status.Value.StartsWith("RatelDesk Web URL must be HTTPS", StringComparison.Ordinal));
        Assert.Equal(SetupStep.Instance, terminal.Model.Step.Value);
        Assert.Equal("http://insecure.example", terminal.Model.Instance.Value);
        Assert.Contains("http://insecure.example", terminal.RecordedRender, StringComparison.Ordinal);
        terminal.Input.EnqueueKey(ConsoleKey.Escape);
        await terminal.Run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, workflow.SaveCount);
    }

    [Fact]
    public async Task Password_is_masked_across_resize_and_manual_save_supports_reentry()
    {
        var workflow = new FakeWorkflow();
        await using (var terminal = await Wizard.StartAsync(workflow))
        {
            await terminal.AdvanceToAuthenticationAsync();
            terminal.Input.EnqueueKey(ConsoleKey.Tab);
            terminal.Input.EnqueueString("rdk_never_render_this");
            await terminal.WaitAsync(() => terminal.Model.ManualSecret.Value == "rdk_never_render_this");
            Assert.DoesNotContain("rdk_never_render_this", terminal.RecordedRender, StringComparison.Ordinal);
            Assert.True(terminal.RecordedRender.Count(character => character == '*') >= 5);
            terminal.Screen.Resize(120, 32);
            terminal.Input.EnqueueResize(120, 32);
            await terminal.WaitAsync(() => terminal.Model.TerminalWidth.Value == 120);
            Assert.DoesNotContain("rdk_never_render_this", terminal.RecordedRender, StringComparison.Ordinal);
            terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
            await terminal.WaitStepAsync(SetupStep.Review);
            Assert.DoesNotContain("rdk_never_render_this", terminal.Model.Detail.Value, StringComparison.Ordinal);
            terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
            await terminal.WaitStepAsync(SetupStep.Complete);
            Assert.Equal(1, workflow.SaveCount);
            Assert.Equal("api", workflow.LastSaved!.CredentialPurpose);
        }
        await using var reentry = await Wizard.StartAsync(workflow);
        await reentry.AdvanceToInstanceAsync();
        Assert.Equal("https://desk.example", reentry.Model.Instance.Value);
        Assert.Equal("credential.secret", reentry.Model.SecretFile.Value);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("expired")]
    public async Task Browser_pending_denied_or_expired_preserves_previous_profile_and_cancels_delivery(string status)
    {
        var old = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://desk.example", CredentialMode = "integration", IntegrationCredential = "rdk_previous_working" };
        var workflow = new FakeWorkflow { Stored = old, Existing = true, PairingAvailable = true, PollStatus = status };
        await using var terminal = await Wizard.StartAsync(workflow);
        await terminal.AdvanceToAuthenticationAsync();
        await terminal.Application.InvokeAsync(() => terminal.Model.SelectAuthentication("browser"));
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Approval);
        Assert.Contains("Short code: TRUST-CODE", terminal.Model.Detail.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("private-device-capability", terminal.RecordedRender, StringComparison.Ordinal);
        Assert.Equal(0, workflow.OpenBrowserCount);
        await terminal.WaitAsync(() => terminal.Model.Status.Value.Contains($"Approval {status}", StringComparison.Ordinal));
        terminal.Input.EnqueueKey(ConsoleKey.Escape);
        await terminal.Run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, workflow.CancelCount);
        Assert.Equal(0, workflow.SaveCount);
        Assert.Equal(old, workflow.Stored);
    }

    [Fact]
    public async Task Approval_displays_actual_grants_confirms_overwrite_saves_then_acknowledges()
    {
        var workflow = new FakeWorkflow { Existing = true, PairingAvailable = true, PollStatus = "issued",
            Stored = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://desk.example", CredentialMode = "integration", IntegrationCredentialFile = "old.secret" } };
        await using var terminal = await Wizard.StartAsync(workflow);
        await terminal.AdvanceToAuthenticationAsync();
        await terminal.Application.InvokeAsync(() => terminal.Model.SelectAuthentication("browser"));
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Review);
        Assert.Contains("org-enabled-one", terminal.Model.Detail.Value, StringComparison.Ordinal);
        Assert.Contains("ticket.view", terminal.Model.Detail.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("rdk_browser_private", terminal.Model.Detail.Value, StringComparison.Ordinal);
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitAsync(() => terminal.Model.ConfirmOverwrite.Value);
        Assert.Equal(0, workflow.SaveCount);
        terminal.Input.EnqueueKey(ConsoleKey.DownArrow);
        terminal.Input.EnqueueKey(ConsoleKey.Enter);
        await terminal.WaitStepAsync(SetupStep.Complete);
        Assert.Equal(new[] { "stage", "ack", "activate", "check" }, workflow.Events);
        Assert.Equal("org-enabled-one", workflow.LastSaved!.CredentialOrganizationId);
        Assert.Equal(new[] { "ticket.view" }, workflow.LastSaved.CredentialPermissions);
    }

    [Fact]
    public async Task Stale_discovery_result_after_url_edit_is_fenced()
    {
        var completion = new TaskCompletionSource<PairingCapabilities?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeWorkflow { Discovery = completion.Task };
        await using var terminal = await Wizard.StartAsync(workflow);
        await terminal.AdvanceToInstanceAsync();
        terminal.Input.EnqueueString("https://first.example");
        terminal.Input.EnqueueKey(ConsoleKey.Enter);
        await terminal.WaitAsync(() => workflow.DiscoveryCount == 1);
        terminal.Input.EnqueueKey(ConsoleKey.A, control: true);
        terminal.Input.EnqueueString("https://second.example");
        await terminal.WaitAsync(() => terminal.Model.Instance.Value == "https://second.example");
        completion.SetResult(new("RatelDesk", 1, true, "https://first.example", "https://first.example/account/integration-credentials/approve", 30, 90));
        await terminal.WaitAsync(() => !terminal.Model.Busy.Value);
        Assert.Equal(SetupStep.Instance, terminal.Model.Step.Value);
        Assert.Equal(0, workflow.SaveCount);
        Assert.Null(terminal.Model.BrowserUrl);
    }

    [Fact]
    public async Task Managed_environment_fields_are_readonly_and_new_profile_does_not_copy_secrets()
    {
        var workflow = new FakeWorkflow
        {
            Managed = true,
            Effective = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://desk.example", CredentialMode = "integration", IntegrationCredential = "rdk_managed_private" }
        };
        await using var terminal = await Wizard.StartAsync(workflow);
        await terminal.AdvanceToInstanceAsync();
        Assert.Contains("externally managed", terminal.RecordedRender, StringComparison.Ordinal);
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Authentication);
        await terminal.Application.InvokeAsync(() => terminal.Model.SelectAuthentication("existing"));
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Review);
        Assert.Contains("Externally managed sources remain required", terminal.Model.Detail.Value, StringComparison.Ordinal);
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Complete);
        Assert.Equal("https://desk.example", workflow.LastSaved!.ApiBaseUrl);
        Assert.Null(workflow.LastSaved.IntegrationCredential);
        Assert.Null(workflow.LastSaved.IntegrationCredentialFile);
        Assert.DoesNotContain("rdk_managed_private", terminal.RecordedRender, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lost_acknowledgement_with_consumed_poll_finishes_without_second_save()
    {
        var workflow = new FakeWorkflow { PairingAvailable = true, PollStatus = "issued", LoseAcknowledgementResponse = true };
        await using var terminal = await Wizard.StartAsync(workflow);
        await terminal.AdvanceToAuthenticationAsync();
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Review);
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Complete);
        Assert.Equal(1, workflow.SaveCount);
        Assert.Equal(0, workflow.RollbackCount);
    }

    [Fact]
    public async Task Ordinary_client_rejects_temporary_verifier_delivery_without_saving_profile()
    {
        var workflow = new FakeWorkflow { PairingAvailable = true, PollStatus = "issued", DeliveredGrantKind = "setup-verification" };
        await using var terminal = await Wizard.StartAsync(workflow);
        await terminal.AdvanceToAuthenticationAsync();
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitAsync(() => terminal.Model.Status.Value.Contains("Approved credential purpose/resource", StringComparison.Ordinal));
        Assert.Equal(SetupStep.Approval, terminal.Model.Step.Value);
        Assert.Equal(0, workflow.SaveCount);
        Assert.Null(workflow.LastSaved);
    }

    [Fact]
    public async Task Recovery_does_not_replace_edited_target_and_cancel_removes_unacknowledged_delivery()
    {
        var session = new PairingSession("private-capability", "TRUST-CODE", "https://original.example/account/integration-credentials/approve", DateTimeOffset.UtcNow.AddMinutes(5), 1);
        var pending = new PendingPairingProfile("/private/default.json", "/private/default.candidate", "", "candidate", "https://original.example", session, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2));
        var workflow = new FakeWorkflow { Pending = pending };
        await using var terminal = await Wizard.StartAsync(workflow, new(WebUrl: "https://edited.example"));
        terminal.Input.EnqueueKey(ConsoleKey.Enter);
        await terminal.WaitAsync(() => terminal.Model.Status.Value.Contains("pending approval belongs to", StringComparison.Ordinal));
        Assert.Equal("https://edited.example", terminal.Model.Instance.Value);
        Assert.Equal(0, workflow.DiscoveryCount);
        Assert.Equal(0, workflow.SaveCount);
        terminal.Input.EnqueueKey(ConsoleKey.Escape);
        await terminal.Run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, workflow.CancelCount);
        Assert.Equal(1, workflow.DiscardCount);
    }

    [Fact]
    public async Task External_http_OAuth_saves_nonsecret_metadata_and_preserves_imported_public_client_id()
    {
        var workflow = new FakeWorkflow();
        await using var terminal = await Wizard.StartAsync(workflow, new(SetupTransport.Http));
        await terminal.AdvanceToAuthenticationAsync();
        await terminal.Application.InvokeAsync(() => { terminal.Model.SelectAuthentication("oauth"); terminal.Model.ClientId.Value = "existing-public-client"; });
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Review);
        Assert.Contains("external MCP client", terminal.Model.Detail.Value, StringComparison.Ordinal);
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Complete);
        Assert.Equal("oauth", workflow.LastSaved!.HttpClientAuthentication);
        Assert.Equal("existing-public-client", workflow.LastSaved.OAuthClientId);
        Assert.Null(workflow.LastSaved.IntegrationCredential);
        Assert.Null(workflow.LastSaved.AuthentikAppPassword);
        Assert.Null(workflow.LastSaved.AuthentikTokenUrl);
        Assert.Contains("client-managed, unverified", terminal.Model.Detail.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupTransport.Cli)]
    [InlineData(SetupTransport.Stdio)]
    public async Task Native_client_does_not_adopt_HTTP_OAuth_authentication(SetupTransport transport)
    {
        var workflow = new FakeWorkflow { Stored = AgentClientConfiguration.Empty with
        { ApiBaseUrl = "https://desk.example", ClientTransport = "http", HttpClientAuthentication = "oauth", McpResourceUri = "https://desk.example/mcp", OAuthAuthorizationServers = ["https://auth.example/issuer/"] } };
        await using var terminal = await Wizard.StartAsync(workflow, new(transport));
        Assert.Equal("browser", terminal.Model.Authentication);
        await terminal.AdvanceToAuthenticationAsync();
        Assert.Equal("manual", terminal.Model.Authentication);
        await terminal.Application.InvokeAsync(() => terminal.Model.SelectAuthentication("oauth"));
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitAsync(() => terminal.Model.Status.Value.Contains("require HTTP MCP transport", StringComparison.Ordinal));
        Assert.Equal(SetupStep.Authentication, terminal.Model.Step.Value);
        Assert.Equal(0, workflow.SaveCount);
    }

    [Fact]
    public async Task Existing_API_profile_cannot_be_reused_for_HTTP_MCP_client()
    {
        var workflow = new FakeWorkflow { Existing = true, Stored = AgentClientConfiguration.Empty with
        { ApiBaseUrl = "https://desk.example", CredentialPurpose = "api", CredentialMode = "integration", IntegrationCredentialFile = "api-only.secret" } };
        await using var terminal = await Wizard.StartAsync(workflow, new(SetupTransport.Http));
        await terminal.AdvanceToAuthenticationAsync();
        await terminal.Application.InvokeAsync(() => terminal.Model.SelectAuthentication("existing"));
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitAsync(() => terminal.Model.Status.Value.Contains("API-purpose", StringComparison.Ordinal));
        Assert.Equal(SetupStep.Authentication, terminal.Model.Step.Value);
        Assert.Equal(0, workflow.SaveCount);
    }

    [Theory]
    [InlineData(SetupTransport.Cli)]
    [InlineData(SetupTransport.Stdio)]
    public async Task Existing_HTTP_MCP_credential_cannot_be_reused_for_native_API_client(SetupTransport transport)
    {
        var workflow = new FakeWorkflow { Existing = true, Stored = AgentClientConfiguration.Empty with
        { ApiBaseUrl = "https://desk.example", CredentialPurpose = "mcp", ClientTransport = "http", McpResourceUri = "https://desk.example/mcp", CredentialMode = "integration", IntegrationCredential = "rdk_http_private" } };
        await using var terminal = await Wizard.StartAsync(workflow, new(transport));
        await terminal.AdvanceToAuthenticationAsync();
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitAsync(() => terminal.Model.Status.Value.Contains("require an API-purpose credential", StringComparison.Ordinal));
        Assert.Equal(SetupStep.Authentication, terminal.Model.Step.Value);
        Assert.Equal(0, workflow.SaveCount);
        Assert.DoesNotContain("rdk_http_private", terminal.RecordedRender, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("authentik")]
    [InlineData("integration")]
    public async Task Hosting_reload_retains_masked_identity_and_requires_deployment_sources_before_approval(string mode)
    {
        var temporary = Directory.CreateTempSubdirectory("rateldesk-setup-host-");
        try
        {
            var outbound = mode == "integration"
                ? AgentClientConfiguration.Empty with { ApiBaseUrl = "http://api:8222/", CredentialMode = "integration", IntegrationCredential = "rdk_existing_private" }
                : new AgentClientConfiguration("http://api:8222/", "https://auth.example/token", "client", "agent", "existing_private_password", "openid", null) { CredentialMode = "authentik" };
            var settings = new HttpMcpHostingSettings
            {
                PublicOrigin = "https://desk.example", AuthenticationMode = "authentik", AllowedOrigins = ["https://desk.example"],
                AuthentikAuthority = "https://auth.example/application/o/mcp/", AuthentikAudience = "https://desk.example/mcp",
                RequiredScopes = ["mcp"], RequiredGroups = ["rateldesk-mcp"], Outbound = outbound
            };
            HttpMcpHostingCoordinator.Export(settings, temporary.FullName);
            using var workflow = new FakeWorkflow();
            using var model = new SetupViewModel(workflow, new(Hosting: true, OutputDirectory: temporary.FullName));
            model.LoadHosting(); model.Listener.Value = "http://+:8323"; model.McpBackend.Value = "http://mcp-http:8323/";
            await model.NextAsync();
            Assert.Equal(SetupStep.Hosting, model.Step.Value);
            await model.NextAsync();
            Assert.Equal(SetupStep.Hosting, model.Step.Value);
            Assert.Contains("HOST_SOURCE_REQUIRED", model.Status.Value, StringComparison.Ordinal);
            Assert.Null(model.BrowserUrl);
            Assert.DoesNotContain("existing_private_password", model.Detail.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("rdk_existing_private", model.Detail.Value, StringComparison.Ordinal);
            var loaded = HttpMcpHostingCoordinator.Load(temporary.FullName);
            Assert.Equal(settings.Listener, loaded.Listener);
            Assert.Equal(mode, loaded.Outbound!.CredentialMode);
            var identity = loaded.Outbound.Resolve();
            Assert.Equal(mode == "integration" ? "rdk_existing_private" : "existing_private_password", mode == "integration" ? identity.IntegrationCredential : identity.AuthentikAppPassword);
        }
        finally { temporary.Delete(recursive: true); }
    }

    [Fact]
    public async Task Compact_client_approval_retains_controls_with_long_details_errors_and_resize()
    {
        var workflow = new FakeWorkflow { PairingAvailable = true,
            VerificationUrl = "https://" + new string('a', 60) + "." + new string('b', 60) + ".example/account/integration-credentials/approve" };
        await using var terminal = await Wizard.StartAsync(workflow, new(Monochrome: true));
        await terminal.AdvanceToAuthenticationAsync();
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Approval);
        await terminal.Application.InvokeAsync(() => terminal.Model.Detail.Value = string.Join('\n', Enumerable.Repeat("Synthetic redacted deployment detail", 300)));
        await terminal.WaitAsync(() => terminal.CurrentScreen.Contains("TRUST-CODE", StringComparison.Ordinal));
        Assert.Contains("Client access approval", terminal.CurrentScreen);
        Assert.Contains("Open browser", terminal.CurrentScreen);
        Assert.Contains("Copy URL and code", terminal.CurrentScreen);
        Assert.Contains("Details", terminal.CurrentScreen);
        Assert.Contains("Cancel", terminal.CurrentScreen);
        Assert.DoesNotContain("Synthetic redacted", terminal.CurrentScreen);
        terminal.Input.EnqueueKey(ConsoleKey.D, control: true);
        await terminal.WaitAsync(() => terminal.Model.DetailsVisible.Value && terminal.CurrentScreen.Contains("Details ·", StringComparison.Ordinal));
        Assert.Contains("TRUST-CODE", terminal.CurrentScreen);
        Assert.Contains("Open browser", terminal.CurrentScreen);
        Assert.Contains("Back", terminal.CurrentScreen);
        terminal.Input.EnqueueKey(ConsoleKey.Tab); // Move focus off the independently scrollable/copyable URL.
        terminal.Input.EnqueueKey(ConsoleKey.PageDown);
        await terminal.Application.InvokeAsync(() => terminal.Model.Status.Value = string.Concat(Enumerable.Repeat("Safe recoverable error. ", 100)));
        Assert.Contains("TRUST-CODE", terminal.CurrentScreen);
        terminal.Screen.Resize(120, 32); terminal.Input.EnqueueResize(120, 32);
        await terminal.WaitAsync(() => terminal.Model.TerminalWidth.Value == 120);
        Assert.Contains("TRUST-CODE", terminal.CurrentScreen);
        terminal.Input.EnqueueKey(ConsoleKey.Escape);
        await terminal.WaitAsync(() => !terminal.Model.DetailsVisible.Value);
        Assert.Equal(SetupStep.Approval, terminal.Model.Step.Value);
        terminal.Input.EnqueueKey(ConsoleKey.O, control: true);
        await terminal.WaitAsync(() => workflow.OpenBrowserCount == 1);
        terminal.Input.EnqueueKey(ConsoleKey.B, control: true);
        await terminal.WaitStepAsync(SetupStep.Authentication);
        Assert.Equal("https://desk.example", terminal.Model.Instance.Value);
        Assert.Equal("default", terminal.Model.Profile.Value);
        Assert.Equal("browser", terminal.Model.Authentication);
        Assert.Null(terminal.Model.BrowserUrl);
        Assert.Equal(1, workflow.CancelCount);
    }

    [Fact]
    public async Task Client_Back_fences_delayed_old_approval_and_does_not_overwrite_new_session()
    {
        var oldPoll = new TaskCompletionSource<PairingPollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new FakeWorkflow { PairingAvailable = true, PollTask = oldPoll.Task };
        await using var terminal = await Wizard.StartAsync(workflow);
        await terminal.AdvanceToAuthenticationAsync();
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitAsync(() => workflow.PollCount == 1);
        terminal.Input.EnqueueKey(ConsoleKey.B, control: true);
        await terminal.WaitStepAsync(SetupStep.Authentication);
        workflow.PollTask = null;
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Approval);
        Assert.Equal(2, workflow.StartCount);
        oldPoll.SetResult(new("issued", 1, new(Guid.NewGuid(), "rdk_prefix", "rdk_obsolete", "api", "org-old", ["Incident.Read"], DateTimeOffset.UtcNow.AddDays(1), null)));
        await terminal.Application.InvokeAsync(() => { });
        await Task.Delay(50);
        Assert.Equal(SetupStep.Approval, terminal.Model.Step.Value);
        Assert.Equal(0, workflow.SaveCount);
        Assert.DoesNotContain("org-old", terminal.Model.Detail.Value);
    }

    [Fact]
    public async Task Host_Back_preserves_draft_and_obsolete_approval_cannot_advance_edited_proposal()
    {
        var previous = Environment.GetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable);
        var directory = Directory.CreateTempSubdirectory("rateldesk-host-navigation-");
        var source = Path.Combine(directory.FullName, "source.json");
        SecureConfigurationFiles.WriteAtomic(source, HttpMcpEffectiveDeploymentTests.Complete().ToJsonString());
        Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable, source);
        var hosts = new List<FakeHostWorkflow>();
        try
        {
            await using var terminal = await Wizard.StartAsync(new FakeWorkflow(), new(Hosting: true, OutputDirectory: Path.Combine(directory.FullName, "output")),
                hostFactory: _ => { var host = new FakeHostWorkflow(); hosts.Add(host); return host; });
            terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
            await terminal.WaitStepAsync(SetupStep.Hosting);
            terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
            await terminal.WaitStepAsync(SetupStep.Approval);
            await terminal.WaitAsync(() => hosts[0].PollCount == 1);
            var originalHash = hosts[0].Proposal!.ProposalHash;
            Assert.Contains("Host deployment approval", terminal.CurrentScreen);
            Assert.Contains("HOST-CODE", terminal.CurrentScreen);
            terminal.Input.EnqueueKey(ConsoleKey.B, control: true);
            await terminal.WaitStepAsync(SetupStep.Hosting);
            Assert.True(terminal.Model.IsHosting);
            Assert.Equal(HttpMcpEffectiveDeploymentTests.Settings.PublicOrigin, terminal.Model.HostPublicOrigin.Value);
            Assert.Equal("gateway", terminal.Model.HostAuthentication);
            Assert.Equal(Path.Combine(directory.FullName, "output"), terminal.Model.OutputDirectory.Value);
            await terminal.Application.InvokeAsync(() => { terminal.Model.Listener.Value = "http://+:8323"; terminal.Model.McpBackend.Value = "http://mcp-http:8323/"; });
            terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
            await terminal.WaitStepAsync(SetupStep.Approval);
            Assert.Equal(2, hosts.Count);
            Assert.NotEqual(originalHash, hosts[1].Proposal!.ProposalHash);
            hosts[0].CompleteApproved();
            await Task.Delay(50);
            Assert.Equal(SetupStep.Approval, terminal.Model.Step.Value);
            Assert.Equal(SetupApprovalContext.Host, terminal.Model.ApprovalContext);
            Assert.Equal("http://+:8323", terminal.Model.Listener.Value);
            Assert.Equal(1, hosts[0].CancelCount);
            Assert.Equal(0, hosts.Sum(host => host.SaveCount));
        }
        finally { Environment.SetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable, previous); directory.Delete(true); }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("denied")]
    [InlineData("expired")]
    public async Task Verifier_Back_returns_to_applied_checkpoint_and_cleans_only_temporary_consent(string status)
    {
        var verifiers = new List<FakeVerificationWorkflow>();
        var client = new FakeWorkflow();
        await using var terminal = await Wizard.StartAsync(client, new() { VerificationDirectory = "/private/host" },
            verificationFactory: _ => { var verifier = new FakeVerificationWorkflow(status); verifiers.Add(verifier); return verifier; });
        await terminal.WaitStepAsync(SetupStep.Approval);
        Assert.Equal(SetupApprovalContext.Verification, terminal.Model.ApprovalContext);
        Assert.Contains("Temporary status verification", terminal.CurrentScreen);
        Assert.Contains("VERIFY-CODE", terminal.CurrentScreen);
        await terminal.WaitAsync(() => verifiers[0].PollCount == 1);
        terminal.Input.EnqueueKey(ConsoleKey.B, control: true);
        await terminal.WaitStepAsync(SetupStep.Verification);
        Assert.Equal(1, verifiers[0].DisposeCount);
        Assert.Contains("Applied—verify pending", terminal.Model.Status.Value);
        Assert.Null(terminal.Model.BrowserUrl);
        terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
        await terminal.WaitStepAsync(SetupStep.Approval);
        Assert.Equal(2, verifiers.Count);
        Assert.Equal(0, client.StartCount);
        Assert.Equal(0, client.SaveCount);
        terminal.Input.EnqueueKey(ConsoleKey.Escape);
        await terminal.Run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, verifiers[1].DisposeCount);
        Assert.Equal(130, terminal.Model.ExitCode);
    }

    [Fact]
    public async Task Verifier_Back_waits_for_real_delayed_protocol_poll_before_cleanup_and_new_consent()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-verifier-navigation-");
        var adapters = new List<DelayedVerificationWorkflow>();
        try
        {
            await using var terminal = await Wizard.StartAsync(new FakeWorkflow(), new() { VerificationDirectory = directory.FullName },
                verificationFactory: _ => { var adapter = new DelayedVerificationWorkflow(Path.Combine(directory.FullName, "cleanup.json")); adapters.Add(adapter); return adapter; });
            await terminal.WaitStepAsync(SetupStep.Approval);
            await adapters[0].PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            terminal.Input.EnqueueKey(ConsoleKey.B, control: true);
            await terminal.WaitStepAsync(SetupStep.Verification);
            Assert.Equal(0, adapters[0].DisposeCount);
            terminal.Input.EnqueueKey(ConsoleKey.N, control: true);
            await terminal.WaitAsync(() => terminal.Model.Busy.Value);
            Assert.Single(adapters);
            adapters[0].ReleasePoll();
            await terminal.WaitAsync(() => adapters.Count == 2 && terminal.Model.Step.Value == SetupStep.Approval && !terminal.Model.Busy.Value);
            Assert.Equal(1, adapters[0].DisposeCount);
            Assert.Equal(1, adapters[0].Cancellations);
            Assert.Equal(0, adapters[0].RunCount);
            Assert.DoesNotContain("rdk_delayed_verifier", terminal.RecordedRender);
            terminal.Input.EnqueueKey(ConsoleKey.Escape);
            await terminal.Run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, adapters[1].DisposeCount);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "cleanup.json")));
        }
        finally { directory.Delete(true); }
    }

    private sealed class DelayedVerificationWorkflow : IHostVerificationWorkflow
    {
        private readonly SetupVerificationSession _session;
        private readonly TaskCompletionSource<HttpResponseMessage> _poll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Origin => "https://desk.example";
        public bool Supported => true;
        public int DisposeCount { get; private set; }
        public int Cancellations { get; private set; }
        public int RunCount { get; private set; }
        public DelayedVerificationWorkflow(string cleanup)
        { _session = new(Origin, cleanup, new HttpClient(new VerificationTransport(this))); }
        public Task<PairingSession> StartAsync(CancellationToken ct) => _session.StartAsync(ct);
        public Task<PairingPollResult> PollAsync(CancellationToken ct) => _session.PollAsync(ct);
        public Task<HostVerificationResult> RunAsync(CancellationToken ct) { RunCount++; throw new InvalidOperationException("Obsolete verification must not run checks."); }
        public async ValueTask DisposeAsync() { await _session.DisposeAsync(); DisposeCount++; }
        public void ReleasePoll() => _poll.SetResult(Json(new PairingPollResult("issued", 1,
            new(Guid.NewGuid(), "rdk_prefix", "rdk_delayed_verifier", "mcp", "synthetic-org", [], DateTimeOffset.UtcNow.AddMinutes(4), Origin + "/mcp") { GrantKind = "setup-verification" })));
        private static HttpResponseMessage Json(object value) => new(System.Net.HttpStatusCode.OK)
        { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))) };
        private sealed class VerificationTransport(DelayedVerificationWorkflow owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("capabilities", StringComparison.Ordinal)) return Task.FromResult(Json(new PairingCapabilities("RatelDesk", 1, true, owner.Origin, owner.Origin + "/account/integration-credentials/approve", 30, 90) { SetupVerificationSupported = true }));
                if (path.EndsWith("start", StringComparison.Ordinal)) return Task.FromResult(Json(new PairingSession(new string('c', 64), "VERIFY-CODE", owner.Origin + "/account/integration-credentials/approve", DateTimeOffset.UtcNow.AddMinutes(5), 1)));
                if (path.EndsWith("poll", StringComparison.Ordinal)) { owner.PollStarted.TrySetResult(); return owner._poll.Task; }
                if (path.EndsWith("cancel", StringComparison.Ordinal)) { owner.Cancellations++; return Task.FromResult(Json(new { })); }
                throw new InvalidOperationException("Unexpected verification fixture route.");
            }
        }
    }

    private sealed class FakeHostWorkflow : IHostDeploymentWorkflow
    {
        private readonly TaskCompletionSource<Helpdesk.Shared.DTOs.Auth.HostDeploymentApprovalStatus> _poll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpMcpDeploymentProposal? Proposal { get; private set; }
        public int PollCount { get; private set; }
        public int CancelCount { get; private set; }
        public int SaveCount { get; private set; }
        public Task<PairingSession> StartAsync(HttpMcpDeploymentProposal proposal, CancellationToken ct)
        { Proposal = proposal; return Task.FromResult(new PairingSession(new string('a', 64), "HOST-CODE", proposal.Settings.PublicOrigin + "/account/integration-credentials/approve?kind=host", DateTimeOffset.UtcNow.AddMinutes(5), 1)); }
        public Task<Helpdesk.Shared.DTOs.Auth.HostDeploymentApprovalStatus> PollAsync(PairingSession session, CancellationToken ct) { PollCount++; return _poll.Task; }
        public void CompleteApproved() => _poll.TrySetResult(new("approved", Proposal!.ProposalHash, Proposal.SourceHash, "synthetic-admin", DateTimeOffset.UtcNow));
        public Task CancelAsync(PairingSession session, CancellationToken ct) { CancelCount++; return Task.CompletedTask; }
        public Task<HttpMcpHostingExport> SaveApprovedAsync(HttpMcpDeploymentProposal proposal, PairingSession session, string directory, bool overwrite, CancellationToken ct) { SaveCount++; throw new InvalidOperationException("No export expected in navigation tests."); }
        public void Dispose() { }
    }

    private sealed class FakeVerificationWorkflow(string status) : IHostVerificationWorkflow
    {
        public string Origin => "https://desk.example";
        public bool Supported => true;
        public int DisposeCount { get; private set; }
        public int PollCount { get; private set; }
        public Task<PairingSession> StartAsync(CancellationToken ct) => Task.FromResult(new PairingSession(new string('b', 64), "VERIFY-CODE", Origin + "/account/integration-credentials/approve", DateTimeOffset.UtcNow.AddMinutes(5), 1));
        public Task<PairingPollResult> PollAsync(CancellationToken ct) { PollCount++; return Task.FromResult(new PairingPollResult(status, 1, null)); }
        public Task<HostVerificationResult> RunAsync(CancellationToken ct) => throw new InvalidOperationException("No verifier checks expected before approval.");
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class Wizard : IAsyncDisposable
    {
        private readonly TextWriter _priorOutput;
        private readonly StringWriter _captured = new();
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
        private readonly SetupRuntime _runtime;
        public VirtualInputSource Input { get; } = new();
        public VirtualTerminal Screen { get; } = new(80, 24);
        // Inspect the actual emitted render traffic, including style escapes,
        // as well as the VirtualTerminal dimensions used for resize assertions.
        public string RecordedRender
        {
            get
            {
                string rendered = "";
                Application.InvokeAsync(() => rendered = string.Concat(Screen.RawOutput)).GetAwaiter().GetResult();
                return System.Text.RegularExpressions.Regex.Replace(rendered, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
            }
        }
        public string CurrentScreen
        {
            get { string screen = ""; Application.InvokeAsync(() => screen = string.Join('\n', Screen.GetAllLines())).GetAwaiter().GetResult(); return screen; }
        }
        public SetupViewModel Model => _runtime.ViewModel;
        public Termina.TerminaApplication Application => _runtime.Application;
        public Task Run { get; }
        private Wizard(FakeWorkflow workflow, SetupOptions options, Func<string, IHostDeploymentWorkflow>? hostFactory, Func<string, IHostVerificationWorkflow>? verificationFactory)
        {
            _priorOutput = Console.Out; Console.SetOut(_captured);
            _runtime = SetupRunner.CreateHost(options, workflow, Input, Screen, hostFactory, verificationFactory);
            Assert.Same(Screen, _runtime.Host.Services.GetRequiredService<IAnsiTerminal>());
            Run = _runtime.Host.RunAsync(_deadline.Token);
        }
        public static async Task<Wizard> StartAsync(FakeWorkflow workflow, SetupOptions? options = null,
            Func<string, IHostDeploymentWorkflow>? hostFactory = null, Func<string, IHostVerificationWorkflow>? verificationFactory = null)
        {
            var wizard = new Wizard(workflow, options ?? new(), hostFactory, verificationFactory);
            await wizard.Application.InvokeAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(3));
            return wizard;
        }
        public async Task AdvanceToInstanceAsync()
        {
            await WaitStepAsync(SetupStep.Instance);
        }
        public async Task AdvanceToAuthenticationAsync()
        {
            await AdvanceToInstanceAsync();
            if (Model.Instance.Value.Length == 0) Input.EnqueueString("https://desk.example");
            Input.EnqueueKey(ConsoleKey.N, control: true); await WaitStepAsync(SetupStep.Authentication);
        }
        public Task WaitStepAsync(SetupStep step) => WaitAsync(() => Model.Step.Value == step && !Model.Busy.Value);
        public async Task WaitAsync(Func<bool> predicate)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate()) await Task.Delay(10, deadline.Token);
            await Application.InvokeAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                Application.Shutdown(); _deadline.Cancel();
                await Run.WaitAsync(TimeSpan.FromSeconds(3));
                await Model.CleanupAsync();
                _runtime.Host.Dispose();
            }
            finally { Console.SetOut(_priorOutput); _captured.Dispose(); _deadline.Dispose(); }
        }
    }

    private sealed class FakeWorkflow : ISetupWorkflow
    {
        public AgentClientConfiguration Stored { get; set; } = AgentClientConfiguration.Empty;
        public AgentClientConfiguration? Effective { get; init; }
        public AgentClientConfiguration? LastSaved { get; private set; }
        public bool Existing { get; set; }
        public bool Managed { get; init; }
        public bool PairingAvailable { get; init; }
        public string PollStatus { get; init; } = "pending";
        public string? VerificationUrl { get; init; }
        public Task<PairingPollResult>? PollTask { get; set; }
        public int PollCount { get; private set; }
        public string DeliveredGrantKind { get; init; } = "ordinary";
        public PendingPairingProfile? Pending { get; init; }
        public Task<PairingCapabilities?>? Discovery { get; init; }
        public bool LoseAcknowledgementResponse { get; init; }
        private bool _acknowledged;
        private AgentClientConfiguration? _candidate;
        public int SaveCount { get; private set; }
        public int CancelCount { get; private set; }
        public int RollbackCount { get; private set; }
        public int DiscoveryCount { get; private set; }
        public int OpenBrowserCount { get; private set; }
        public int StorageCheckCount { get; private set; }
        public bool StorageFailure { get; init; }
        public int StartCount { get; private set; }
        public int DiscardCount { get; private set; }
        public List<string> Events { get; } = [];
        public SetupProfile Load(string profile, SetupTransport transport) => new(new(Effective ?? Stored,
            Managed ? [new("apiBaseUrl", "environment", true, false, "https://desk.example"), new("credentialMode", "environment", true, false, "integration"), new("integrationCredential", "environment", true, true, "[REDACTED]")] :
                Stored.ApiBaseUrl is null ? [] : [new("apiBaseUrl", "file", false, false, Stored.ApiBaseUrl)],
            "/private/" + profile + ".json", Existing ? "revision" : null, true), Stored, Pending, Pending is null ? null : Stored);
        public Task<PairingCapabilities?> DiscoverAsync(string origin, CancellationToken ct)
        { DiscoveryCount++; return Discovery ?? Task.FromResult<PairingCapabilities?>(PairingAvailable ? new("RatelDesk", 1, true, origin, origin.TrimEnd('/') + "/account/integration-credentials/approve", 30, 90) : null); }
        public Task<OAuthProtectedResourceMetadata> DiscoverOAuthAsync(string origin, IEnumerable<string>? expectedAuthorities, CancellationToken ct) =>
            Task.FromResult(new OAuthProtectedResourceMetadata(origin.TrimEnd('/') + "/mcp", ["https://auth.example/application/o/mcp/"]));
        public void PreflightStorage(string profile, SetupTransport transport)
        { StorageCheckCount++; if (StorageFailure) throw new AgentClientValidationException("Private durable storage is unavailable."); }
        public Task<PairingSession> StartAsync(string origin, string profile, SetupTransport transport, CancellationToken ct)
        { StartCount++; return Task.FromResult(new PairingSession("private-device-capability", "TRUST-CODE", VerificationUrl ?? origin.TrimEnd('/') + "/account/integration-credentials/approve", DateTimeOffset.UtcNow.AddMinutes(5), 1)); }
        public Task<PairingPollResult> PollAsync(string origin, PairingSession session, CancellationToken ct)
        { PollCount++; return PollTask ?? Task.FromResult(
            _acknowledged ? new PairingPollResult("consumed", 1, null) : new(PollStatus, 1, PollStatus == "issued" ? new(Guid.NewGuid(), "rdk_prefix", "rdk_browser_private", "api", "org-enabled-one", ["ticket.view"], DateTimeOffset.UtcNow.AddDays(30), null) { GrantKind = DeliveredGrantKind } : null, DateTimeOffset.UtcNow.AddMinutes(2))); }
        public Task CancelAsync(string origin, PairingSession session, CancellationToken ct) { CancelCount++; return Task.CompletedTask; }
        public Task AcknowledgeAsync(string origin, PairingSession session, Guid id, CancellationToken ct)
        { Events.Add("ack"); _acknowledged = true; if (LoseAcknowledgementResponse) throw new HttpRequestException(); return Task.CompletedTask; }
        public ConfigurationProfileSaveResult Save(string profile, SetupTransport transport, AgentClientConfiguration configuration, string revision, CancellationToken ct)
        {
            SaveCount++; Events.Add("save"); LastSaved = configuration;
            Stored = configuration.IntegrationCredential is null ? configuration : configuration with { IntegrationCredential = null, IntegrationCredentialFile = "credential.secret" };
            Existing = true; return new("/private/" + profile + ".json", "saved-revision", Stored);
        }
        public void Rollback(ConfigurationProfileSaveResult saved, SetupProfile previous) { RollbackCount++; Stored = previous.Stored; }
        public PendingPairingProfile Stage(string profile, SetupTransport transport, AgentClientConfiguration configuration, string revision,
            string origin, PairingSession session, Guid credentialId, DateTimeOffset deadline, CancellationToken ct)
        {
            Events.Add("stage"); LastSaved = configuration;
            _candidate = configuration with { IntegrationCredential = null, IntegrationCredentialFile = "credential.secret" };
            return new("/private/" + profile + ".json", "/private/" + profile + ".candidate", revision, "candidate-revision", origin, session, credentialId, deadline);
        }
        public PendingPairingProfile MarkAcknowledged(PendingPairingProfile pending) => pending with { Acknowledged = true };
        public ConfigurationProfileSaveResult Activate(PendingPairingProfile pending, CancellationToken ct)
        { Events.Add("activate"); SaveCount++; Stored = _candidate!; Existing = true; return new(pending.TargetPath, "saved-revision", Stored); }
        public void Discard(PendingPairingProfile pending) { DiscardCount++; _candidate = null; }
        public Task<IReadOnlyList<SetupCheck>> CheckAsync(ConfigurationResolution resolution, SetupTransport transport, CancellationToken ct, string? instance = null)
        { Events.Add("check"); return Task.FromResult<IReadOnlyList<SetupCheck>>([new("authentication", "passed", "Read-only access accepted.")]); }
        public Task OpenBrowserAsync(string url, CancellationToken ct) { OpenBrowserCount++; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
