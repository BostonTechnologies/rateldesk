extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using NetclawConnectivityPage = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Orchestration.NetclawConnectivity;

namespace Helpdesk.Tests.NewWeb;

public sealed class NetclawConnectivityPageTests
{
    [Fact]
    public async Task First_run_shows_only_the_daemon_address_and_pairing_code_as_primary_setup()
    {
        var api = new NetclawApi(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Task.FromResult(Json(new NetclawConnectivitySettingsDto { Revision = 0, Instance = "dev" }));
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Task.FromResult(Json(Array.Empty<NetclawUnboundLegacySessionDto>()));

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));

        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        var formStart = html.IndexOf("data-testid=\"netclaw-pair-form\"", StringComparison.Ordinal);
        Assert.True(formStart >= 0, "The first-run page should render the primary pairing form.");
        var formEnd = html.IndexOf("</form>", formStart, StringComparison.Ordinal);
        Assert.True(formEnd > formStart, "The primary pairing form should have a complete boundary.");
        var primaryForm = html[formStart..formEnd];
        Assert.Contains("Netclaw address", primaryForm);
        Assert.Contains("Pairing code", primaryForm);
        Assert.Contains("Pair &amp; connect", primaryForm);
        Assert.Equal(2, primaryForm.Split("<input", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Enable native Netclaw chat", primaryForm);
        Assert.DoesNotContain("Test draft", primaryForm);
        Assert.DoesNotContain("Save settings", primaryForm);
        Assert.False(GetField<bool>(page, "diagnosticsExpanded"));
        Assert.DoesNotContain("netclaw-legacy-ownership", html);
    }

    [Fact]
    public async Task Pairing_sends_only_the_code_and_draft_then_clears_code_and_token_after_saved_redacted_profile()
    {
        JsonElement? capturedRequest = null;
        var api = new NetclawApi(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Json(new NetclawConnectivitySettingsDto { Revision = 0, Instance = "dev" });
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Json(Array.Empty<NetclawUnboundLegacySessionDto>());
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
            {
                using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
                capturedRequest = body.RootElement.Clone();
                return Json(new NetclawConnectivitySettingsDto
                {
                    Revision = 1,
                    Enabled = true,
                    Instance = "dev",
                    Endpoint = "https://netclaw.example.test/hub/session",
                    HasDeviceToken = true,
                    SecretState = "configured",
                    LastTestSucceeded = true,
                    LastTestedAtUtc = DateTimeOffset.Parse("2026-09-27T10:00:00+00:00")
                });
            }

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));
        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        var draft = GetField<UpdateNetclawConnectivitySettingsDto>(page, "draft");
        draft.IdleMinutes = 31;
        draft.ConnectionCapacity = 42;
        SetField(page, "turnTimeoutText", "00:07:00");
        SetField(page, "heartbeatText", "00:00:20");
        SetField(page, "pairingAddress", "https://netclaw.example.test/hub/session");
        SetField(page, "pairingCode", "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        Assert.NotNull(capturedRequest);
        var submitted = capturedRequest.Value;
        Assert.Equal("synthetic-one-time-code", submitted.GetProperty("pairingCode").GetString());
        Assert.Equal("https://netclaw.example.test/hub/session", submitted.GetProperty("endpoint").GetString());
        Assert.Equal(0, submitted.GetProperty("expectedRevision").GetInt32());
        Assert.Null(submitted.GetProperty("legacyOwnershipReviewToken").GetString());
        Assert.Equal(31, submitted.GetProperty("idleMinutes").GetInt32());
        Assert.Equal(42, submitted.GetProperty("connectionCapacity").GetInt32());
        Assert.Equal("00:07:00", submitted.GetProperty("turnInactivityTimeout").GetString());
        Assert.Equal("00:00:20", submitted.GetProperty("activityHeartbeatInterval").GetString());
        Assert.Equal(string.Empty, GetField<string>(page, "pairingCode"));
        Assert.Null(GetField<UpdateNetclawConnectivitySettingsDto>(page, "draft").DeviceToken);
        Assert.Equal(1, GetField<NetclawConnectivitySettingsDto>(page, "settings")!.Revision);

        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Connected to Netclaw", html);
        Assert.Contains("Authenticated SignalR verified", html);
        Assert.DoesNotContain("synthetic-one-time-code", html, StringComparison.Ordinal);
        var summaryStart = html.IndexOf("data-testid=\"netclaw-connected-summary\"", StringComparison.Ordinal);
        var summaryEnd = summaryStart < 0 ? -1 : html.IndexOf("</section>", summaryStart, StringComparison.Ordinal);
        Assert.True(summaryStart >= 0 && summaryEnd > summaryStart, "A verified profile should render its connected summary.");
        Assert.DoesNotContain("Paired-device token", html[summaryStart..summaryEnd]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_server_legacy_confirmation_reuses_the_code_and_refreshes_the_prompt_on_a_race(bool confirmationConflicts)
    {
        var legacy = Session(Guid.NewGuid(), "tenant-a", "INC-400", "incidents");
        var pairRequests = new List<JsonElement>();
        var pairAttempts = 0;
        var api = new NetclawApi(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Json(new NetclawConnectivitySettingsDto
                {
                    Revision = 4,
                    Instance = "dev"
                });
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Json(new[] { legacy });
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
            {
                using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
                pairRequests.Add(body.RootElement.Clone());
                pairAttempts++;
                if (pairAttempts == 1)
                {
                    return Json(new NetclawLegacySessionReviewConflictDto(
                        "legacy_ownership_confirmation_required",
                        "This untrusted detail must never render.",
                        "http://10.23.45.67/hub/session",
                        "http://10.23.45.67:5199",
                        1,
                        "synthetic-review-token-1"), HttpStatusCode.Conflict);
                }

                return confirmationConflicts
                    ? Json(new NetclawLegacySessionReviewConflictDto(
                        "legacy_session_conflict",
                        "This untrusted conflict detail must never render.",
                        "http://10.23.45.67/hub/session",
                        "http://10.23.45.67:5199",
                        3,
                        "synthetic-review-token-2"), HttpStatusCode.Conflict)
                    : Json(new NetclawConnectivitySettingsDto
                    {
                        Revision = 5,
                        Enabled = true,
                        Instance = "dev",
                        Endpoint = "http://10.23.45.67:5199/hub/session",
                        HasDeviceToken = true,
                        LastTestSucceeded = true,
                        LastTestedAtUtc = DateTimeOffset.Parse("2026-09-27T10:00:00+00:00")
                    });
            }

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));
        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        SetField(page, "pairingAddress", "http://10.23.45.67:5199");
        SetField(page, "pairingCode", "synthetic-one-time-code");

        Assert.True((bool)typeof(NetclawConnectivityPage)
            .GetProperty("CanPairAndSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!);
        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        Assert.Single(pairRequests);
        Assert.Equal("synthetic-one-time-code", pairRequests[0].GetProperty("pairingCode").GetString());
        Assert.Null(pairRequests[0].GetProperty("legacyOwnershipReviewToken").GetString());
        Assert.True(GetField<bool>(page, "pairingNeedsLegacyConfirmation"));
        Assert.Equal("synthetic-one-time-code", GetField<string>(page, "pairingCode"));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("1 previous AI Assistant conversation", html);
        Assert.Contains("http://10.23.45.67:5199", html);
        Assert.DoesNotContain("INC-400", html);
        Assert.DoesNotContain(legacy.ConversationId.ToString(), html);
        Assert.DoesNotContain("This untrusted detail must never render", html);

        await InvokeHandlerAsync(renderer, page, "ContinuePairingAfterLegacyReviewAsync");

        Assert.Equal(2, pairRequests.Count);
        Assert.Equal("synthetic-one-time-code", pairRequests[1].GetProperty("pairingCode").GetString());
        Assert.Equal("synthetic-review-token-1", pairRequests[1].GetProperty("legacyOwnershipReviewToken").GetString());
        if (confirmationConflicts)
        {
            Assert.Equal("synthetic-one-time-code", GetField<string>(page, "pairingCode"));
            Assert.Equal(3, GetField<int>(page, "legacyConversationCount"));
            Assert.Equal("synthetic-review-token-2", GetField<string>(page, "legacyOwnershipReviewToken"));
            var refreshedHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
            Assert.Contains("3 previous AI Assistant conversations", refreshedHtml);
            Assert.Contains("The older-conversation list changed", refreshedHtml);
            Assert.DoesNotContain("synthetic-one-time-code", refreshedHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("This untrusted conflict detail must never render", refreshedHtml);
        }
        else
        {
            Assert.Equal(string.Empty, GetField<string>(page, "pairingCode"));
            Assert.Equal(5, GetField<NetclawConnectivitySettingsDto>(page, "settings")!.Revision);
            var connectedHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
            Assert.Contains("Connected to Netclaw", connectedHtml);
            Assert.DoesNotContain("synthetic-one-time-code", connectedHtml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Empty_legacy_race_returns_to_pair_form_with_the_unused_code()
    {
        var pairRequests = new List<JsonElement>();
        var pairAttempts = 0;
        var api = new NetclawApi(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Json(new NetclawConnectivitySettingsDto { Revision = 4, Instance = "dev" });
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Json(Array.Empty<NetclawUnboundLegacySessionDto>());
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
            {
                using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
                pairRequests.Add(body.RootElement.Clone());
                pairAttempts++;
                return pairAttempts switch
                {
                    1 => Json(new NetclawLegacySessionReviewConflictDto(
                        "legacy_ownership_confirmation_required", "", "http://10.23.45.67/hub/session",
                        "http://10.23.45.67:5199", 1, "synthetic-review-token-1"), HttpStatusCode.Conflict),
                    2 => Json(new NetclawLegacySessionReviewConflictDto(
                        "legacy_session_conflict", "", "http://10.23.45.67/hub/session",
                        "http://10.23.45.67:5199", 0, "synthetic-review-token-2"), HttpStatusCode.Conflict),
                    _ => Json(new NetclawConnectivitySettingsDto
                    {
                        Revision = 5,
                        Enabled = true,
                        Instance = "dev",
                        Endpoint = "http://10.23.45.67:5199/hub/session",
                        DaemonAddress = "http://10.23.45.67:5199",
                        HasDeviceToken = true,
                        LastTestSucceeded = true,
                        LastTestedAtUtc = DateTimeOffset.Parse("2026-09-27T10:00:00+00:00")
                    })
                };
            }

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));
        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        SetField(page, "pairingAddress", "http://10.23.45.67:5199");
        SetField(page, "pairingCode", "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");
        await InvokeHandlerAsync(renderer, page, "ContinuePairingAfterLegacyReviewAsync");

        Assert.False(GetField<bool>(page, "pairingNeedsLegacyConfirmation"));
        Assert.Equal(0, GetField<int>(page, "legacyConversationCount"));
        Assert.Null(GetField<string?>(page, "legacyOwnershipReviewToken"));
        Assert.Equal("synthetic-one-time-code", GetField<string>(page, "pairingCode"));
        var refreshedHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("No older conversations remain unbound", refreshedHtml);
        Assert.DoesNotContain("netclaw-legacy-confirmation", refreshedHtml);
        AssertPairingCodeRetainedOnlyAsMaskedInput(refreshedHtml, "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        Assert.Equal(3, pairRequests.Count);
        Assert.Equal("synthetic-one-time-code", pairRequests[2].GetProperty("pairingCode").GetString());
        Assert.Null(pairRequests[2].GetProperty("legacyOwnershipReviewToken").GetString());
        var connectedHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Connected to Netclaw", connectedHtml);
    }

    [Fact]
    public async Task Saved_verification_failure_can_recover_by_testing_the_saved_token_without_repairing()
    {
        var profileReads = 0;
        var pairRequests = 0;
        var api = new NetclawApi(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
            {
                profileReads++;
                return Task.FromResult(Json(profileReads == 1
                    ? new NetclawConnectivitySettingsDto { Revision = 0, Instance = "dev" }
                    : new NetclawConnectivitySettingsDto
                    {
                        Revision = 1,
                        Enabled = true,
                        Instance = "dev",
                        Endpoint = "https://netclaw.example.test/hub/session",
                        DaemonAddress = "https://netclaw.example.test",
                        HasDeviceToken = true,
                        SecretState = "configured",
                        LastTestSucceeded = profileReads >= 3,
                        LastTestedAtUtc = profileReads >= 3 ? DateTimeOffset.Parse("2026-09-27T10:00:00+00:00") : null
                    }));
            }
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Task.FromResult(Json(Array.Empty<NetclawUnboundLegacySessionDto>()));
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
            {
                pairRequests++;
                return Task.FromResult(Json(new { code = "pairing_saved_verification_failed", message = "Do not render this untrusted message." }, HttpStatusCode.BadGateway));
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/test")
                return Task.FromResult(Json(new NetclawConnectivityTestResultDto { Success = true, Message = "Authenticated SignalR verified." }));

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));
        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        SetField(page, "pairingAddress", "https://netclaw.example.test");
        SetField(page, "pairingCode", "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        Assert.Equal(1, pairRequests);
        Assert.Equal(string.Empty, GetField<string>(page, "pairingCode"));
        Assert.True(GetField<bool>(page, "requiresSavedStateInspection"));
        var savedStateHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Review the saved connection", savedStateHtml);
        Assert.Contains("Test saved connection", savedStateHtml);
        Assert.DoesNotContain("Do not render this untrusted message", savedStateHtml);

        await InvokeHandlerAsync(renderer, page, "TestAsync");

        Assert.Equal(1, pairRequests);
        Assert.False(GetField<bool>(page, "requiresSavedStateInspection"));
        Assert.True((bool)typeof(NetclawConnectivityPage)
            .GetProperty("IsConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!);
        var connectedHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Connected to Netclaw", connectedHtml);
        Assert.Contains("Authenticated SignalR verified", connectedHtml);
        Assert.DoesNotContain("synthetic-one-time-code", connectedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairing_timeout_keeps_inspection_when_the_saved_connected_revision_is_unchanged()
    {
        var api = new NetclawApi(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Task.FromResult(Json(new NetclawConnectivitySettingsDto
                {
                    Revision = 4,
                    Enabled = true,
                    Instance = "dev",
                    Endpoint = "https://netclaw.example.test/hub/session",
                    DaemonAddress = "https://netclaw.example.test",
                    HasDeviceToken = true,
                    SecretState = "configured",
                    LastTestSucceeded = true,
                    LastTestedAtUtc = DateTimeOffset.Parse("2026-09-27T10:00:00+00:00")
                }));
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Task.FromResult(Json(Array.Empty<NetclawUnboundLegacySessionDto>()));
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
                return Task.FromResult(Json(new { code = "pairing_timeout", message = "Never show this untrusted detail." }, HttpStatusCode.GatewayTimeout));
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/test")
                return Task.FromResult(Json(new NetclawConnectivityTestResultDto { Success = true, Message = "Authenticated SignalR verified." }));

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));
        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        SetField(page, "isEditingConnection", true);
        SetField(page, "pairingAddress", "https://netclaw.example.test");
        SetField(page, "pairingCode", "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        Assert.True(GetField<bool>(page, "requiresSavedStateInspection"));
        Assert.True(GetField<bool>(page, "savedStateReloaded"));
        Assert.Equal(4, GetField<NetclawConnectivitySettingsDto>(page, "settings")!.Revision);
        Assert.Equal(string.Empty, GetField<string>(page, "pairingCode"));
        Assert.Equal("Needs attention", typeof(NetclawConnectivityPage)
            .GetProperty("ConnectionStatusLabel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page));
        var reviewHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Review the saved connection", reviewHtml);
        Assert.Contains("Test saved connection", reviewHtml);
        Assert.DoesNotContain("Connected to Netclaw", reviewHtml);
        Assert.DoesNotContain("Never show this untrusted detail", reviewHtml);

        await InvokeHandlerAsync(renderer, page, "TestAsync");

        Assert.False(GetField<bool>(page, "requiresSavedStateInspection"));
        Assert.Contains("Connected to Netclaw", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
    }

    [Theory]
    [InlineData("invalid_session_limits", 400, "Review the session limits in Advanced settings")]
    [InlineData("netclaw_runtime_unavailable", 503, "runtime is not available on this server")]
    public async Task Pairing_preflight_errors_keep_the_code_and_show_only_safe_guidance(string responseCode, int statusCode, string expectedMessage)
    {
        var api = new NetclawApi(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Task.FromResult(Json(new NetclawConnectivitySettingsDto { Revision = 2, Instance = "dev" }));
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Task.FromResult(Json(Array.Empty<NetclawUnboundLegacySessionDto>()));
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
                return Task.FromResult(Json(new { code = responseCode, message = "Never show this untrusted detail." }, (HttpStatusCode)statusCode));

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));
        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        SetField(page, "pairingAddress", "http://192.168.1.20:5199");
        SetField(page, "pairingCode", "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        Assert.Equal("synthetic-one-time-code", GetField<string>(page, "pairingCode"));
        Assert.False(GetField<bool>(page, "requiresSavedStateInspection"));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains(expectedMessage, html);
        Assert.DoesNotContain("Never show this untrusted detail", html);
        AssertPairingCodeRetainedOnlyAsMaskedInput(html, "synthetic-one-time-code");
    }

    [Theory]
    [InlineData("pairing_outcome_uncertain", 502, "Pairing outcome is uncertain")]
    [InlineData("pairing_saved_verification_failed", 502, "Pairing succeeded and was saved")]
    [InlineData("pairing_saved_verification_uncertain", 502, "Pairing may have been saved")]
    [InlineData("pairing_verification_superseded", 409, "The saved profile changed")]
    [InlineData("pairing_service_error", 502, "The pairing response is uncertain")]
    [InlineData(null, 502, "The pairing response is uncertain")]
    [InlineData("unrecognized_pairing_error", 502, "The pairing response is uncertain")]
    [InlineData(null, 408, "The pairing response is uncertain")]
    public async Task Consumed_or_ambiguous_pairing_responses_require_saved_state_inspection_without_discarding_the_unsaved_draft(string? responseCode, int statusCode, string expectedMessage)
    {
        var api = new NetclawApi(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Task.FromResult(Json(new NetclawConnectivitySettingsDto { Revision = 0, Instance = "dev" }));
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Task.FromResult(Json(Array.Empty<NetclawUnboundLegacySessionDto>()));
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
                return Task.FromResult(Json(new { code = responseCode, message = "Never show this untrusted detail." }, (HttpStatusCode)statusCode));

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));
        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        var draft = GetField<UpdateNetclawConnectivitySettingsDto>(page, "draft");
        draft.Enabled = true;
        draft.Endpoint = "https://draft-provider.example.test/hub/session";
        draft.DeviceToken = "synthetic-unsaved-manual-token";
        SetField(page, "pairingAddress", "https://draft-provider.example.test");
        SetField(page, "isDirty", true);
        SetField(page, "pairingCode", "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        var retainedDraft = GetField<UpdateNetclawConnectivitySettingsDto>(page, "draft");
        Assert.Equal("https://draft-provider.example.test/hub/session", retainedDraft.Endpoint);
        Assert.Equal("synthetic-unsaved-manual-token", retainedDraft.DeviceToken);
        Assert.Equal(string.Empty, GetField<string>(page, "pairingCode"));
        Assert.True(GetField<bool>(page, "requiresSavedStateInspection"));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains(expectedMessage, html);
        Assert.Contains("Review the saved connection", html);
        Assert.DoesNotContain("Pair &amp; connect", html);
        Assert.DoesNotContain("Never show this untrusted detail", html);
        Assert.DoesNotContain("synthetic-one-time-code", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_legacy_review_is_hidden_until_chosen_and_keeps_per_session_confirmation(bool confirmationConflicts)
    {
        var selectedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var newlyUnboundId = Guid.NewGuid();
        var legacyReads = 0;
        ConfirmationRequest? capturedConfirmation = null;
        var initialSessions = new[]
        {
            Session(selectedId, "tenant-a", "INC-100", "incidents"),
            Session(otherId, "tenant-b", "REQ-200", "requests")
        };
        var api = new NetclawApi(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Json(new NetclawConnectivitySettingsDto { Revision = 0, Instance = "dev" });

            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
            {
                legacyReads++;
                NetclawUnboundLegacySessionDto[] currentSessions = legacyReads == 1
                    ? initialSessions
                    : confirmationConflicts
                        ? [initialSessions[1], Session(newlyUnboundId, "tenant-c", "INC-300", "incidents")]
                        : [initialSessions[1]];
                return Json(currentSessions);
            }

            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
                return Json(new NetclawLegacySessionReviewConflictDto(
                    "legacy_ownership_confirmation_required",
                    "Do not render this server detail.",
                    "https://netclaw.example.test/hub/session",
                    "https://netclaw.example.test",
                    2,
                    "synthetic-review-token"), HttpStatusCode.Conflict);

            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/legacy-sessions/confirm-owner")
            {
                using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
                var root = body.RootElement;
                capturedConfirmation = new ConfirmationRequest(
                    root.GetProperty("historicalInstance").GetString(),
                    root.GetProperty("historicalEndpoint").GetString(),
                    root.GetProperty("allowPrivateHttp").GetBoolean(),
                    root.GetProperty("expectedEligibleConversations").GetInt32(),
                    root.GetProperty("conversationIds").EnumerateArray().Select(item => item.GetGuid()).ToArray(),
                    root.TryGetProperty("deviceToken", out _));
                if (confirmationConflicts)
                    return Json(new { code = "legacy_session_conflict", message = "This untrusted detail must never render." }, HttpStatusCode.Conflict);
                return Json(new NetclawLegacySessionConfirmationDto(1, "synthetic-fingerprint"));
            }

            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        });
        await using var services = Services(api);
        await using var renderer = new NetclawHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new PageHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = harness })));

        var initialHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.DoesNotContain("tenant-a", initialHtml);
        Assert.DoesNotContain("INC-100", initialHtml);
        Assert.DoesNotContain("REQ-200", initialHtml);
        Assert.DoesNotContain("Historical Netclaw session endpoint", initialHtml);
        Assert.DoesNotContain("synthetic-legacy-session", initialHtml);

        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        var draft = GetField<Helpdesk.Shared.DTOs.Orchestration.UpdateNetclawConnectivitySettingsDto>(page, "draft");
        draft.Enabled = true;
        draft.Endpoint = "https://draft-provider.example.test/hub/session";
        draft.DeviceToken = "synthetic-unsaved-draft-token";
        SetField(page, "pairingAddress", "https://draft-provider.example.test");
        SetField(page, "pairingCode", "synthetic-one-time-code");
        Assert.True((bool)typeof(NetclawConnectivityPage)
            .GetProperty("CanPairAndSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!);
        SetField(page, "isDirty", true);
        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        var confirmationHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("2 previous AI Assistant conversations", confirmationHtml);
        Assert.DoesNotContain("INC-100", confirmationHtml);
        await InvokeHandlerAsync(renderer, page, "ReviewLegacySessionsSeparately");

        var manualReviewHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("tenant-a", manualReviewHtml);
        Assert.Contains("INC-100", manualReviewHtml);
        Assert.Contains("REQ-200", manualReviewHtml);
        Assert.Contains("Historical Netclaw session endpoint", manualReviewHtml);
        Assert.Contains("I confirm the selected sessions used this historical endpoint and instance", manualReviewHtml);

        GetField<HashSet<Guid>>(page, "selectedLegacySessionIds").Add(selectedId);
        SetField(page, "historicalInstance", "dev");
        SetField(page, "historicalEndpoint", "http://10.23.45.67/hub/session");
        SetField(page, "historicalAllowPrivateHttp", true);
        SetField(page, "historicalOwnershipAcknowledged", true);
        await InvokeHandlerAsync(renderer, page, "ConfirmLegacySessionOwnersAsync");

        Assert.NotNull(capturedConfirmation);
        Assert.Equal("dev", capturedConfirmation.Instance);
        Assert.Equal("http://10.23.45.67/hub/session", capturedConfirmation.Endpoint);
        Assert.True(capturedConfirmation.AllowPrivateHttp);
        Assert.Equal(1, capturedConfirmation.ExpectedCount);
        Assert.Equal(new[] { selectedId }, capturedConfirmation.ConversationIds);
        Assert.False(capturedConfirmation.ContainsDeviceToken);
        Assert.True(GetField<bool>(page, "isDirty"));
        Assert.Equal("https://draft-provider.example.test/hub/session", draft.Endpoint);
        Assert.Equal("synthetic-unsaved-draft-token", draft.DeviceToken);
        Assert.False(GetField<bool>(page, "historicalOwnershipAcknowledged"));

        var afterConfirmationHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("REQ-200", afterConfirmationHtml);
        Assert.DoesNotContain("INC-100", afterConfirmationHtml);
        Assert.DoesNotContain("This untrusted detail must never render", afterConfirmationHtml);
        Assert.Contains(confirmationConflicts
            ? "The legacy session list changed. Review it again"
            : "Ownership confirmed for 1 selected legacy session(s)", afterConfirmationHtml);
    }

    private static NetclawUnboundLegacySessionDto Session(Guid id, string organization, string ticket, string type)
        => new(id, organization, ticket, type, DateTimeOffset.Parse("2026-09-27T10:00:00+00:00"));

    private static Task InvokeHandlerAsync(NetclawHtmlRenderer renderer, NetclawConnectivityPage page, string methodName)
    {
        var method = typeof(NetclawConnectivityPage).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = (Func<Task>)method.CreateDelegate(typeof(Func<Task>), page);
        return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem(callback), null));
    }

    private static T GetField<T>(NetclawConnectivityPage page, string name)
        => (T)typeof(NetclawConnectivityPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static void AssertPairingCodeRetainedOnlyAsMaskedInput(string html, string code)
    {
        Assert.Single(Regex.Matches(html, Regex.Escape(code)).Cast<Match>());
        var codeInput = Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .SingleOrDefault(match => match.Value.Contains($"value=\"{code}\"", StringComparison.Ordinal));
        Assert.NotNull(codeInput);
        Assert.Contains("type=\"password\"", codeInput.Value, StringComparison.OrdinalIgnoreCase);

        var renderedText = Regex.Replace(html, "<[^>]+>", " ");
        Assert.DoesNotContain(code, renderedText, StringComparison.Ordinal);
    }

    private static void SetField<T>(NetclawConnectivityPage page, string name, T value)
        => typeof(NetclawConnectivityPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static ServiceProvider Services(IHttpClientFactory api)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton(api);
        services.AddSingleton<NavigationManager>(new TestNavigation());
        services.AddSingleton(Substitute.For<IJSRuntime>());
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = JsonContent.Create(value) };

    private sealed class NetclawApi(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(this, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://127.0.0.1/", "http://127.0.0.1/admin/automation/integration/netclaw");
    }

    private sealed class NetclawHtmlRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode) => componentActivator.CreateInstance(componentType);

        public async Task<HtmlRootComponent> RenderComponentAsync<T>(ParameterView? parameters = null) where T : IComponent
        {
            var result = BeginRenderingComponent(typeof(T), parameters ?? ParameterView.Empty);
            await result.QuiescenceTask;
            return result;
        }
    }

    public sealed class PageHarnessReference
    {
        public NetclawConnectivityPage? Page { get; set; }
    }

    public sealed class PageHarness : ComponentBase
    {
        [Parameter] public PageHarnessReference Reference { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<NetclawConnectivityPage>(0);
            builder.AddComponentReferenceCapture(1, component => Reference.Page = (NetclawConnectivityPage)component);
            builder.CloseComponent();
        }
    }

    private sealed record ConfirmationRequest(
        string? Instance,
        string? Endpoint,
        bool AllowPrivateHttp,
        int ExpectedCount,
        Guid[] ConversationIds,
        bool ContainsDeviceToken);
}
