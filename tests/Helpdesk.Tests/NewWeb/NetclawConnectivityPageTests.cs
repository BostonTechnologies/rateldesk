extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
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
                    SecretState = "configured"
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
        draft.Enabled = true;
        draft.Endpoint = "https://netclaw.example.test/hub/session";
        draft.DeviceToken = "synthetic-unsaved-manual-token";
        SetField(page, "pairingCode", "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        Assert.NotNull(capturedRequest);
        var submitted = capturedRequest.Value;
        Assert.Equal("synthetic-one-time-code", submitted.GetProperty("pairingCode").GetString());
        Assert.Equal("https://netclaw.example.test/hub/session", submitted.GetProperty("endpoint").GetString());
        Assert.DoesNotContain(submitted.EnumerateObject(), property => property.Name == "deviceToken");
        Assert.DoesNotContain("synthetic-unsaved-manual-token", submitted.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, GetField<string>(page, "pairingCode"));
        Assert.Null(GetField<UpdateNetclawConnectivitySettingsDto>(page, "draft").DeviceToken);
        Assert.Equal(1, GetField<NetclawConnectivitySettingsDto>(page, "settings")!.Revision);

        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Netclaw paired and settings saved", html);
        Assert.Contains("Revision", html);
        Assert.Contains("Configured (protected)", html);
        Assert.DoesNotContain("synthetic-one-time-code", html, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-unsaved-manual-token", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pairing_with_unbound_sessions_uses_api_preflight_and_preserves_code_and_draft_on_conflict(bool serverReportsOwnershipConflict)
    {
        var legacy = Session(Guid.NewGuid(), "tenant-a", "INC-400", "incidents");
        var legacyReads = 0;
        JsonElement? capturedRequest = null;
        var api = new NetclawApi(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Json(new NetclawConnectivitySettingsDto
                {
                    Revision = 4,
                    Enabled = true,
                    Instance = "dev",
                    Endpoint = "https://current-provider.example.test/hub/session",
                    HasDeviceToken = true
                });
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
            {
                legacyReads++;
                return Json(new[] { legacy });
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
            {
                using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
                capturedRequest = body.RootElement.Clone();
                return serverReportsOwnershipConflict
                    ? Json(new { code = "unbound_legacy_sessions", message = "Never render this server detail." }, HttpStatusCode.Conflict)
                    : Json(new NetclawConnectivitySettingsDto
                    {
                        Revision = 5,
                        Enabled = true,
                        Instance = "dev",
                        Endpoint = "https://current-provider.example.test/hub/session",
                        HasDeviceToken = true
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
        SetField(page, "pairingCode", "synthetic-one-time-code");
        if (serverReportsOwnershipConflict)
        {
            draft.Endpoint = "https://changed-provider.example.test/hub/session";
            draft.DeviceToken = "synthetic-unsaved-manual-token";
            SetField(page, "isDirty", true);
        }

        Assert.True((bool)typeof(NetclawConnectivityPage)
            .GetProperty("CanPairAndSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!);
        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        Assert.NotNull(capturedRequest);
        Assert.Equal("synthetic-one-time-code", capturedRequest.Value.GetProperty("pairingCode").GetString());
        Assert.DoesNotContain(capturedRequest.Value.EnumerateObject(), property => property.Name == "deviceToken");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("INC-400", html);
        if (serverReportsOwnershipConflict)
        {
            var retainedDraft = GetField<UpdateNetclawConnectivitySettingsDto>(page, "draft");
            Assert.Equal("https://changed-provider.example.test/hub/session", retainedDraft.Endpoint);
            Assert.Equal("synthetic-unsaved-manual-token", retainedDraft.DeviceToken);
            Assert.Equal("synthetic-one-time-code", GetField<string>(page, "pairingCode"));
            Assert.Equal(2, legacyReads);
            Assert.Contains("historical ownership confirmation", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Never render this server detail", html);
        }
        else
        {
            Assert.Equal(string.Empty, GetField<string>(page, "pairingCode"));
            Assert.Equal(5, GetField<NetclawConnectivitySettingsDto>(page, "settings")!.Revision);
            Assert.Contains("Netclaw paired and settings saved", html);
        }
    }

    [Fact]
    public async Task Uncertain_pairing_reloads_profile_without_discarding_the_unsaved_draft()
    {
        var api = new NetclawApi(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw")
                return Task.FromResult(Json(new NetclawConnectivitySettingsDto { Revision = 0, Instance = "dev" }));
            if (request.Method == HttpMethod.Get && path == "/api/v1/admin/netclaw/legacy-sessions/unbound")
                return Task.FromResult(Json(Array.Empty<NetclawUnboundLegacySessionDto>()));
            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/pair-and-save")
                return Task.FromResult(Json(new { code = "pairing_outcome_uncertain", message = "Never show this untrusted detail." }, HttpStatusCode.BadGateway));

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
        SetField(page, "isDirty", true);
        SetField(page, "pairingCode", "synthetic-one-time-code");

        await InvokeHandlerAsync(renderer, page, "PairAndSaveAsync");

        var retainedDraft = GetField<UpdateNetclawConnectivitySettingsDto>(page, "draft");
        Assert.Equal("https://draft-provider.example.test/hub/session", retainedDraft.Endpoint);
        Assert.Equal("synthetic-unsaved-manual-token", retainedDraft.DeviceToken);
        Assert.Equal(string.Empty, GetField<string>(page, "pairingCode"));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Pairing outcome is uncertain", html);
        Assert.DoesNotContain("Never show this untrusted detail", html);
        Assert.DoesNotContain("synthetic-one-time-code", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_and_owner_confirmation_keep_the_draft_and_require_explicit_selected_history(bool confirmationConflicts)
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

            if (request.Method == HttpMethod.Post && path == "/api/v1/admin/netclaw/test-draft")
                return Json(new NetclawConnectivityTestResultDto
                {
                    Success = true,
                    Message = "Authenticated draft session negotiation succeeded; no settings were saved or applied."
                });

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
        Assert.Contains("tenant-a", initialHtml);
        Assert.Contains("INC-100", initialHtml);
        Assert.Contains("REQ-200", initialHtml);
        Assert.Contains("Historical Netclaw session endpoint", initialHtml);
        Assert.Contains("I confirm the selected sessions used this historical endpoint and instance", initialHtml);
        Assert.DoesNotContain("synthetic-legacy-session", initialHtml);

        var page = Assert.IsType<NetclawConnectivityPage>(harness.Page);
        var draft = GetField<Helpdesk.Shared.DTOs.Orchestration.UpdateNetclawConnectivitySettingsDto>(page, "draft");
        draft.Enabled = true;
        draft.Endpoint = "https://draft-provider.example.test/hub/session";
        draft.DeviceToken = "synthetic-unsaved-draft-token";
        SetField(page, "pairingCode", "synthetic-one-time-code");
        Assert.True((bool)typeof(NetclawConnectivityPage)
            .GetProperty("CanPairAndSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!);
        SetField(page, "isDirty", true);
        await InvokeHandlerAsync(renderer, page, "TestDraftAsync");

        var afterTestHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Unbound legacy sessions still need explicit ownership confirmation", afterTestHtml);

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
