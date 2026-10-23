extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using ApprovalPage = NewWeb::HelpDesk.NewWeb.Components.Pages.IntegrationCredentialApproval;

namespace Helpdesk.Tests.NewWeb;

public sealed class IntegrationCredentialApprovalPageTests
{
    [Fact]
    public async Task Code_review_renders_accessible_consent_without_preselected_grants_or_secrets()
    {
        using var api = new Api(HttpStatusCode.OK);
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        typeof(ApprovalPage).GetField("humanCode", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(reference.Page, "ABCD-EFGH");
        await InvokeAsync(renderer, reference.Page!, "ReviewAsync");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Connection consent", html);
        Assert.Contains("ABCD-EFGH", html);
        Assert.Contains("Choose an organization", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.DoesNotContain("name=\"permissions\"", html);
        await InvokeAsync(renderer, reference.Page!, "OrganizationChanged", new ChangeEventArgs { Value = "org-a" });
        html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Incident.Read", html);
        Assert.DoesNotContain("name=\"permissions\"", html);
        await InvokeAsync(renderer, reference.Page!, "PermissionsChanged", new[] { "Incident.Read" });
        html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("name=\"permissions\" value=\"Incident.Read\"", html);
        Assert.Contains("I approve this client connection", html);
        Assert.Contains("Deny connection", html);
        Assert.DoesNotContain("name=\"consent\" checked", html);
        Assert.DoesNotContain("rdk_", html);
        Assert.DoesNotContain("deviceCode", html);
        Assert.Equal(1, api.Calls);
    }
    [Fact]
    public async Task Unavailable_code_renders_recovery_without_consent()
    {
        using var api = new Api(HttpStatusCode.NotFound);
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        await InvokeAsync(renderer, reference.Page!, "ReviewAsync");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("code is unavailable or expired", html);
        Assert.DoesNotContain("integration-pairing-consent-form", html);
        Assert.Equal(1, api.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_review_or_timeout_keeps_the_page_recoverable(bool timeout)
    {
        using var api = new Api(HttpStatusCode.OK, invalidJson: !timeout, timeout: timeout);
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        await InvokeAsync(renderer, reference.Page!, "ReviewAsync");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("connection service is unavailable", html);
        Assert.Contains("Review client", html);
        Assert.DoesNotContain("integration-pairing-consent-form", html);
    }
    [Fact]
    public async Task Temporary_verifier_review_has_no_ordinary_grant_or_lifetime_controls()
    {
        using var api = new Api(HttpStatusCode.OK, verifier: true);
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        await InvokeAsync(renderer, reference.Page!, "ReviewAsync");
        await InvokeAsync(renderer, reference.Page!, "OrganizationChanged", new ChangeEventArgs { Value = "org-a" });
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Temporary verification client", html);
        Assert.Contains("authenticated status check only", html);
        Assert.Contains("five minutes", html);
        Assert.DoesNotContain("credential-permission-selector", html);
        Assert.DoesNotContain("name=\"permissions\"", html);
        Assert.DoesNotContain("name=\"lifetimeDays\"", html);
        Assert.Contains("name=\"consent\"", html);
    }

    [Fact]
    public async Task Owner_scope_is_explicit_defaults_to_ordinary_and_requires_new_consent()
    {
        using var api = new Api(HttpStatusCode.OK, ownerAvailable: true);
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        await InvokeAsync(renderer, reference.Page!, "ReviewAsync");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Owner administration across all organizations", html);
        Assert.DoesNotContain("name=\"ownerAdministrationConsent\"", html);
        Assert.DoesNotContain("value=\"HelpdeskAdmin\"", html);
        await InvokeAsync(renderer, reference.Page!, "GrantKindChanged", new ChangeEventArgs { Value = "owner-administration" });
        await InvokeAsync(renderer, reference.Page!, "OrganizationChanged", new ChangeEventArgs { Value = "org-a" });
        html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("name=\"ownerAdministrationConsent\" value=\"on\"", html);
        Assert.Contains("name=\"permissions\" value=\"HelpdeskAdmin\"", html);
        Assert.Contains("I authorize this agent to administer the instance and all organizations on my behalf.", html);
        Assert.DoesNotContain("name=\"consent\" checked", html);
        Assert.DoesNotContain("credential-permission-selector", html);
    }

    [Fact]
    public async Task Unauthorized_host_review_is_neither_visible_nor_requested_and_code_is_not_switched()
    {
        using var api = new Api(HttpStatusCode.OK);
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        await InvokeAsync(renderer, reference.Page!, "ReviewHostAsync");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.DoesNotContain("Host code", html);
        Assert.Contains("authorized administrator", html);
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task Changing_organization_clears_selected_grants_and_consent()
    {
        using var api = new Api(HttpStatusCode.OK);
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        await InvokeAsync(renderer, reference.Page!, "ReviewAsync");
        await InvokeAsync(renderer, reference.Page!, "OrganizationChanged", new ChangeEventArgs { Value = "org-a" });
        await InvokeAsync(renderer, reference.Page!, "PermissionsChanged", new[] { "Incident.Read" });
        typeof(ApprovalPage).GetField("consent", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(reference.Page, true);
        await InvokeAsync(renderer, reference.Page!, "OrganizationChanged", new ChangeEventArgs { Value = "" });
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.DoesNotContain("name=\"permissions\"", html);
        Assert.False((bool)typeof(ApprovalPage).GetField("consent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reference.Page)!);
        Assert.Equal(1, api.Calls);
    }

    [Theory]
    [InlineData("https://desk.example", true)]
    [InlineData("https://another.example", false)]
    public async Task Host_review_is_separate_and_bound_to_the_current_public_origin(string origin, bool valid)
    {
        using var api = new Api(HttpStatusCode.OK, host: true, hostOrigin: origin);
        await using var services = Services(api, admin: true);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Review host", html); Assert.Contains("Review client", html); Assert.Equal(0, api.Calls);
        await InvokeAsync(renderer, reference.Page!, "HostCodeChanged", new ChangeEventArgs { Value = " abcd efgh " });
        await InvokeAsync(renderer, reference.Page!, "HostKeyDown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        if (valid)
        {
            Assert.Contains("Host deployment consent", html);
            Assert.Contains("ABCD-EFGH", html);
            Assert.Contains("host-consent", html);
            Assert.Contains("name=\"proposalHash\"", html);
            Assert.Contains("name=\"sourceHash\"", html);
            Assert.Contains("AllowedOrigins", html); Assert.Contains("Compose environment", html); Assert.Contains("RequestTimeoutSeconds", html);
            Assert.DoesNotContain("name=\"permissions\"", html);
            Assert.DoesNotContain("name=\"lifetimeDays\"", html);
        }
        else
        { Assert.Contains("different public RatelDesk origin", html); Assert.DoesNotContain("host-consent", html); }
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task A_review_remains_stable_and_duplicate_review_cannot_replace_current_selection()
    {
        using var api = new Api(HttpStatusCode.OK);
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        await InvokeAsync(renderer, reference.Page!, "ReviewAsync");
        await InvokeAsync(renderer, reference.Page!, "OrganizationChanged", new ChangeEventArgs { Value = "org-a" });
        await InvokeAsync(renderer, reference.Page!, "PermissionsChanged", new[] { "Incident.Read" });
        await InvokeAsync(renderer, reference.Page!, "ReviewAsync");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("name=\"permissions\" value=\"Incident.Read\"", html);
        Assert.Contains("Connection consent", html); Assert.Equal(1, api.Calls);
    }

    private static Task InvokeAsync(Renderer renderer, ApprovalPage page, string method, object? arg = null) =>
        renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(async () =>
        {
            var invoked = typeof(ApprovalPage).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, arg is null ? null : [arg]);
            if (invoked is Task task) await task;
        })), null));
    private static ServiceProvider Services(IHttpClientFactory api, bool admin = false)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddMudServices();
        services.AddSingleton(api); services.AddSingleton<NavigationManager>(new Navigation());
        services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(admin));
        services.AddSingleton(Substitute.For<IJSRuntime>()); services.AddSingleton<AntiforgeryStateProvider>(new Antiforgery());
        return services.BuildServiceProvider();
    }
    private sealed class TestAuthentication(bool admin) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity(admin ? [new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, "HelpdeskAdmin")] : [new Claim(ClaimTypes.NameIdentifier, "owner")], "fixture"))));
    }
    private sealed class Antiforgery : AntiforgeryStateProvider
    { public override AntiforgeryRequestToken GetAntiforgeryToken() => new("test-antiforgery", "__RequestVerificationToken"); }
    private sealed class Navigation : NavigationManager
    { public Navigation() => Initialize("https://desk.example/", "https://desk.example/account/integration-credentials/approve"); }
    private sealed class Api(HttpStatusCode status, bool invalidJson = false, bool timeout = false, bool verifier = false, bool host = false, string hostOrigin = "https://desk.example", bool ownerAvailable = false) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://desk.example/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (timeout) throw new TaskCanceledException("Synthetic timeout.");
            if (invalidJson) return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("malformed-json") });
            Calls++; Assert.Equal(HttpMethod.Post, request.Method);
            if (host)
            {
                Assert.Equal("/api/v1/integration-credentials/host-proposals/review", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(new HostDeploymentReview("ABCD-EFGH",
                    new("synthetic host", new string('a', 64), new string('b', 64), hostOrigin, hostOrigin + "/mcp", "private-office", "gateway", "http://api:8080", "http://mcp-http:8080", ["Use the reviewed gateway configuration"]) { ReviewSettings = [new("AllowedOrigins", "https://desk.example", "Compose environment"), new("RequestTimeoutSeconds", "45", "appsettings.json")] }, DateTimeOffset.UtcNow.AddMinutes(5))) });
            }
            Assert.Equal("/api/v1/integration-credentials/pairing/review", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(new IntegrationPairingReview(
                "ABCD-EFGH", "laptop", ownerAvailable ? "mcp" : "api", ownerAvailable ? "https://desk.example/mcp" : null, DateTimeOffset.UtcNow.AddMinutes(5), [new("org-a", "Organization A", ["Incident.Read"])]) { GrantKind = verifier ? "setup-verification" : "ordinary", OwnerAdministrationAvailable = ownerAvailable }) });
        }
    }
    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggers) : StaticHtmlRenderer(services, loggers)
    {
        public async Task<HtmlRootComponent> RenderAsync<T>(ParameterView parameters) where T : IComponent
        { var component = BeginRenderingComponent(typeof(T), parameters); await component.QuiescenceTask; return component; }
    }
    public sealed class Reference { public ApprovalPage? Page { get; set; } }
    public sealed class Harness : ComponentBase
    {
        [Parameter] public Reference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        { builder.OpenComponent<ApprovalPage>(0); builder.AddComponentReferenceCapture(1, c => Reference.Page = (ApprovalPage)c); builder.CloseComponent(); }
    }
}
