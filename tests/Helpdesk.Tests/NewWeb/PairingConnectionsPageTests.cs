extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.Pairing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using AccountPage = NewWeb::HelpDesk.NewWeb.Components.Pages.IntegrationCredentials;
using PairingPanel = NewWeb::HelpDesk.NewWeb.Components.Shared.PairingConnectionsPanel;
using ConnectionApi = NewWeb::HelpDesk.NewWeb.Services.Pairing.SystemConnectionApi;

namespace Helpdesk.Tests.NewWeb;

public sealed class PairingConnectionsPageTests
{
    private const string PairId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly PairingMetadata Peer = new(PairingContract.Version, "netratel", "11111111-1111-1111-1111-111111111111", "Office NetRatel", "https://netratel.example.test", "https://api.netratel.example.test", "22222222-2222-2222-2222-222222222222");
    private static readonly PairingSetupDirectory Directory = new([new("42", "Office monitoring")], [new("organization-7", "Support team")], [new("customer-9", "Incident customer", "organization-7")]);
    private static PairingConnectionDto Paired(PairingMapping? mapping = null, string status = "paired") => new(mapping?.Id ?? PairId, PairId, mapping, Peer, status, null);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Actual_account_parent_pairs_with_two_fields_and_saves_exact_mapping_and_independent_capabilities(bool incidents, bool automation)
    {
        var api = new FixtureApi();
        await using var view = await RenderAsync(api);
        var html = await view.HtmlAsync();
        Assert.Contains("System connections", html);
        Assert.Contains("API &amp; MCP credentials", html);
        Assert.Contains("Generate a Pairing Code", html);
        Assert.DoesNotContain("system-connection-finalization", html);
        await view.InvokeAsync("OpenCreate");
        html = await view.HtmlAsync();
        var form = Form(html, "system-pairing-form");
        Assert.Equal(2, Regex.Matches(form, "<input\\b").Count);
        Assert.Contains("Address", form);
        Assert.Contains("Pairing code", form);
        Assert.Contains("Pair &amp; connect", form);
        Assert.DoesNotContain("tenant", form, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checkbox", form, StringComparison.OrdinalIgnoreCase);
        Assert.False(Property<bool>(view.Panel, "CanPair"));
        Field(view.Panel, "address", "http://192.168.1.20:5000///");
        Field(view.Panel, "pairingCode", "ABCD-EFGH");
        Assert.True(Property<bool>(view.Panel, "CanPair"));
        await view.InvokeAsync("PairAsync");

        var submittedPair = Assert.Single(api.PairRequests);
        Assert.Equal("http://192.168.1.20:5000", submittedPair.Address);
        Assert.Equal("ABCD-EFGH", submittedPair.PairingCode);
        Assert.True(Guid.TryParse(submittedPair.OperationId, out _));
        Assert.Equal("", ReadField<string>(view.Panel, "pairingCode"));
        html = await view.HtmlAsync();
        Assert.DoesNotContain("system-pairing-form", html);
        Assert.Contains("Systems paired", html);
        Assert.Contains("NetRatel tenant", html);
        Assert.Contains("RatelDesk organization", html);
        var choices = ReadField<PairingSetupDirectory>(view.Panel, "directory");
        Assert.Equal(new PairingChoice("42", "Office monitoring"), Assert.Single(choices.NetRatelTenants));
        Assert.Equal(new PairingChoice("organization-7", "Support team"), Assert.Single(choices.RatelDeskOrganizations));
        Assert.DoesNotContain("I accept", html);
        var draft = ReadField<object>(view.Panel, "draft");
        Assert.False(Property<bool>(draft, "RunAutomation"));
        SetProperty(draft, "NetRatelTenantId", "42");
        SetProperty(draft, "RatelDeskOrganizationId", "organization-7");
        SetProperty(draft, "RatelDeskCustomerId", "customer-9");
        SetProperty(draft, "Name", "  Office monitoring to support  ");
        SetProperty(draft, "CreateIncidents", incidents);
        SetProperty(draft, "RunAutomation", automation);
        Assert.True(Property<bool>(view.Panel, "CanSave"));
        await view.InvokeAsync("OrganizationChanged", "organization-7");
        html = await view.HtmlAsync();
        Assert.Contains("Office monitoring", html);
        Assert.Contains("Support team", html);
        await view.InvokeAsync("SaveAsync");

        var mapping = Assert.Single(api.Saves);
        Assert.Equal("42", mapping.NetRatelTenantId);
        Assert.Equal("organization-7", mapping.RatelDeskOrganizationId);
        Assert.Equal(incidents ? "customer-9" : null, mapping.RatelDeskCustomerId);
        Assert.Equal("Office monitoring to support", mapping.Name);
        Assert.Equal(incidents, mapping.CreateIncidents);
        Assert.Equal(automation, mapping.RunAutomation);
        Assert.Equal(0, api.TestCount);
        html = await view.HtmlAsync();
        Assert.Contains("Connected. The selected mapping and capabilities are active.", html);
        Assert.Contains("Office monitoring to support", html);
        Assert.DoesNotContain("system-connection-save-form", html);
        Assert.DoesNotContain("ABCD-EFGH", html);
    }

    [Fact]
    public async Task Generate_has_no_mapping_prerequisite_and_returns_the_readable_code_and_expiry()
    {
        var api = new FixtureApi();
        await using var view = await RenderAsync(api);
        await view.InvokeAsync("GenerateAsync");
        var html = await view.HtmlAsync();
        Assert.Equal(1, api.GenerateCount);
        Assert.Contains("JKLM-NPQR", html);
        Assert.Contains("Expires", html);
        Assert.Contains("Copy code", html);
        Assert.DoesNotContain("system-pairing-form", html);
        Assert.DoesNotContain("system-connection-save-form", html);
        Assert.Empty(api.Saves);
    }

    [Fact]
    public async Task Failed_save_keeps_the_same_nonsecret_draft_and_refresh_restores_it_for_correction()
    {
        var mapping = new PairingMapping("33333333-3333-3333-3333-333333333333", PairId, "Office draft", "42", "organization-7", "customer-9", true, false);
        var api = new FixtureApi { Rows = [Paired(mapping)], FailSave = true };
        await using (var view = await RenderAsync(api))
        {
            await view.InvokeAsync("SaveAsync");
            var retained = ReadField<object>(view.Panel, "draft");
            Assert.Equal(mapping.Id, Property<string>(retained, "Id"));
            Assert.Equal(mapping.Name, Property<string>(retained, "Name"));
            var html = await view.HtmlAsync();
            Assert.Contains("Choose a customer that belongs to the selected organization.", html);
            Assert.Contains("Reference: pairing-test-ref", html);
            Assert.Contains("system-connection-save-form", html);
            Assert.Single(ReadField<List<PairingConnectionDto>>(view.Panel, "connections"));
        }
        await using var restored = await RenderAsync(api);
        Assert.Equal(mapping.Id, Property<string>(ReadField<object>(restored.Panel, "draft"), "Id"));
        Assert.Contains("Office draft", await restored.HtmlAsync());
    }

    [Fact]
    public async Task Lost_pair_response_retries_the_same_operation_and_never_creates_another_mapping()
    {
        var api = new FixtureApi { LoseFirstPairResponse = true };
        await using var view = await RenderAsync(api);
        await view.InvokeAsync("OpenCreate");
        Field(view.Panel, "address", "netratel.example.test:443/");
        Field(view.Panel, "pairingCode", "ABCD-EFGH");
        await view.InvokeAsync("PairAsync");
        Assert.Contains("connection-service-unavailable", await view.HtmlAsync());
        await view.InvokeAsync("PairAsync");
        Assert.Equal(2, api.PairRequests.Count);
        Assert.Equal(api.PairRequests[0].OperationId, api.PairRequests[1].OperationId);
        Assert.Single(ReadField<List<PairingConnectionDto>>(view.Panel, "connections"));
        Assert.Empty(api.Saves);
    }

    [Fact]
    public async Task Test_and_delete_target_the_selected_mapping_and_delete_removes_the_row_without_another_test()
    {
        var selected = new PairingMapping("33333333-3333-3333-3333-333333333333", PairId, "First mapping", "42", "organization-7", "customer-9", true, false);
        var other = selected with { Id = "44444444-4444-4444-4444-444444444444", Name = "Second mapping" };
        var api = new FixtureApi { Rows = [Paired(selected, "connected"), Paired(other, "connected")] };
        await using var view = await RenderAsync(api);
        await view.InvokeAsync("TestAsync", api.Rows[0]);
        Assert.Equal(1, api.TestCount);
        Assert.Contains("Current mapping and permissions verified", await view.HtmlAsync());
        await view.InvokeAsync("DeleteAsync", api.Rows[0]);
        Assert.Equal(selected.Id, Assert.Single(api.DeletedMappingIds));
        var remaining = Assert.Single(ReadField<List<PairingConnectionDto>>(view.Panel, "connections"));
        Assert.Equal(other.Id, remaining.Mapping!.Id);
        Assert.Equal(1, api.TestCount);
        Assert.Empty(api.Saves);
        var html = await view.HtmlAsync();
        Assert.Contains("Local access is revoked", html);
        Assert.DoesNotContain("First mapping", html);
        Assert.Contains("Second mapping", html);
    }

    [Theory]
    [InlineData("host.lan:5100/", "https://host.lan:5100")]
    [InlineData("https://desk.lan///", "https://desk.lan")]
    [InlineData("http://10.0.1.3:5000/", "http://10.0.1.3:5000")]
    [InlineData("http://[fd00::2]:5000/", "http://[fd00::2]:5000")]
    [InlineData("https://user:password@desk.lan", null)]
    [InlineData("https://desk.lan/arbitrary-route", null)]
    public void Address_form_normalizes_supported_origins_and_rejects_credentials_and_arbitrary_routes(string address, string? expected)
        => Assert.Equal(expected, ConnectionApi.NormalizeAddress(address));

    private static string Form(string html, string testId) => Assert.Single(Regex.Matches(html, "<form\\b[^>]*data-testid=\"" + testId + "\"[^>]*>.*?</form>", RegexOptions.Singleline).Cast<Match>()).Value;
    private static void Field(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static T ReadField<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetProperty(object instance, string name, object? value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);

    private static async Task<RenderedAccount> RenderAsync(FixtureApi api)
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddMudServices();
        services.AddSingleton<IHttpClientFactory>(api);
        services.AddSingleton<NavigationManager>(new Navigation());
        services.AddSingleton<AuthenticationStateProvider>(new AdminAuthentication());
        services.AddSingleton(Substitute.For<IJSRuntime>());
        var capture = new ComponentCapture(); services.AddSingleton<IComponentActivator>(capture);
        var provider = services.BuildServiceProvider();
        var renderer = new AccountRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync());
        return new(provider, renderer, root, capture.Panel!);
    }
    private sealed class AdminAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "operator"), new(ClaimTypes.Role, "HelpdeskAdmin")], "fixture"))));
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://127.0.0.1/", "http://127.0.0.1/account/integration-credentials");
    }
    private sealed class ComponentCapture : IComponentActivator
    {
        public PairingPanel? Panel { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is PairingPanel panel) Panel = panel;
            return component;
        }
    }
    private sealed class AccountRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) => componentActivator.CreateInstance(componentType);
        public async Task<HtmlRootComponent> RenderAsync()
        {
            var root = BeginRenderingComponent(typeof(AccountPage), ParameterView.Empty);
            await root.QuiescenceTask;
            return root;
        }
    }
    private sealed class RenderedAccount(ServiceProvider services, AccountRenderer renderer, HtmlRootComponent root, PairingPanel panel) : IAsyncDisposable
    {
        public PairingPanel Panel => panel;
        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        public Task InvokeAsync(string methodName, object? argument = null)
        {
            var method = typeof(PairingPanel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
            Func<Task> callback = () => method.Invoke(panel, argument is null ? null : [argument]) is Task task ? task : Task.CompletedTask;
            return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)panel).HandleEventAsync(new EventCallbackWorkItem(callback), null));
        }
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await services.DisposeAsync(); }
    }
    private sealed class FixtureApi : HttpMessageHandler, IHttpClientFactory
    {
        public List<PairingConnectionDto> Rows { get; set; } = [];
        public List<PairingConnectRequest> PairRequests { get; } = [];
        public List<PairingMapping> Saves { get; } = [];
        public List<string> DeletedMappingIds { get; } = [];
        public int GenerateCount, TestCount;
        public bool FailSave, LoseFirstPairResponse;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/organizations") return Json(new[] { new { id = "organization-7", name = "Support team" } });
            if (path == "/api/v1/integration-credentials/") return Json(Array.Empty<object>());
            if (path == "/api/v1/admin/system-connections/") return Json(Rows);
            if (path.EndsWith("/code", StringComparison.Ordinal)) { GenerateCount++; return Json(new PairingCodeDto("JKLM-NPQR", DateTimeOffset.UtcNow.AddMinutes(5))); }
            if (path.EndsWith("/directory", StringComparison.Ordinal)) return Json(Directory);
            if (path.EndsWith("/pair", StringComparison.Ordinal))
            {
                PairRequests.Add((await request.Content!.ReadFromJsonAsync<PairingConnectRequest>(ct))!);
                if (LoseFirstPairResponse && PairRequests.Count == 1) throw new HttpRequestException("Synthetic lost success response");
                return Json(Paired());
            }
            if (path.EndsWith("/test", StringComparison.Ordinal)) { TestCount++; return Json(new PairingTestResult(true, "Current mapping and permissions verified", DateTimeOffset.UtcNow)); }
            if (request.Method == HttpMethod.Put)
            {
                var mapping = (await request.Content!.ReadFromJsonAsync<PairingMapping>(ct))!; Saves.Add(mapping);
                return FailSave ? Json(new { message = "Choose a customer that belongs to the selected organization.", reference = "pairing-test-ref" }, HttpStatusCode.BadRequest) : Json(Paired(mapping, "connected"));
            }
            if (request.Method == HttpMethod.Delete) { DeletedMappingIds.Add(path.Split('/').Last()); return new(HttpStatusCode.NoContent); }
            throw new InvalidOperationException($"Unexpected connection request: {request.Method} {path}");
        }
        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
    }
}
