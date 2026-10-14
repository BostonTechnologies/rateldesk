extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
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
        Assert.Contains("name=\"permissions\" value=\"Incident.Read\"", html);
        Assert.Contains("I approve this client connection", html);
        Assert.Contains("Deny connection", html);
        Assert.DoesNotContain("checked", html);
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
        Assert.Contains("Review connection", html);
        Assert.DoesNotContain("integration-pairing-consent-form", html);
    }
    private static Task InvokeAsync(Renderer renderer, ApprovalPage page, string method, object? arg = null) =>
        renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(async () =>
        {
            var invoked = typeof(ApprovalPage).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, arg is null ? null : [arg]);
            if (invoked is Task task) await task;
        })), null));
    private static ServiceProvider Services(IHttpClientFactory api)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddMudServices();
        services.AddSingleton(api); services.AddSingleton<NavigationManager>(new Navigation());
        services.AddSingleton(Substitute.For<IJSRuntime>()); services.AddSingleton<AntiforgeryStateProvider>(new Antiforgery());
        return services.BuildServiceProvider();
    }
    private sealed class Antiforgery : AntiforgeryStateProvider
    { public override AntiforgeryRequestToken GetAntiforgeryToken() => new("test-antiforgery", "__RequestVerificationToken"); }
    private sealed class Navigation : NavigationManager
    { public Navigation() => Initialize("https://desk.example/", "https://desk.example/account/integration-credentials/approve"); }
    private sealed class Api(HttpStatusCode status, bool invalidJson = false, bool timeout = false) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://desk.example/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (timeout) throw new TaskCanceledException("Synthetic timeout.");
            if (invalidJson) return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("malformed-json") });
            Calls++; Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/integration-credentials/pairing/review", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(new IntegrationPairingReview(
                "ABCD-EFGH", "laptop", "api", null, DateTimeOffset.UtcNow.AddMinutes(5), [new("org-a", "Organization A", ["Incident.Read"])])) });
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
