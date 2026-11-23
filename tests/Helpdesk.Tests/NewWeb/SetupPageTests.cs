extern alias NewWeb;

using System.Net;
using System.Reflection;
using Helpdesk.ServiceDefaults.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using SetupPage = NewWeb::HelpDesk.NewWeb.Components.Pages.Setup;

namespace Helpdesk.Tests.NewWeb;

public class SetupPageTests
{
    [Theory]
    [InlineData(404, "not found")]
    [InlineData(503, "starting")]
    [InlineData(200, "not valid JSON")]
    [InlineData(200, "null")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"state\":\"Unsupported\"}")]
    public async Task Unavailable_or_incompatible_api_hides_unlock_and_retry_loads_real_setup(int status, string body)
    {
        using var api = new SetupApi(call => call == 1
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }
            : Json("Unconfigured"));
        await using var services = Services(api);
        await using var renderer = new SetupHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new SetupHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SetupHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(SetupHarness.Reference)] = harness })));

        var unavailable = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("data-testid=\"setup-status-unavailable\"", unavailable);
        Assert.Contains("Check connection again", unavailable);
        Assert.Contains("Web and API containers use the same RatelDesk release", unavailable);
        Assert.DoesNotContain("data-testid=\"setup-code-input\"", unavailable);
        Assert.Equal(1, api.Calls);

        // HtmlRenderer produces static markup. Dispatch the same handler as the Retry button
        // through ComponentBase so its normal event lifecycle performs the rerender.
        await InvokeHandlerAsync(renderer, harness.Page!, "ReadSetupStatusAsync");

        var available = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.DoesNotContain("data-testid=\"setup-status-unavailable\"", available);
        // MudStepper registers its steps after insertion; its presence confirms the
        // gated wizard is restored while the initial-render tests exercise its fields.
        Assert.Contains("setup-stepper", available);
        Assert.Equal(2, api.Calls);
    }

    [Fact]
    public async Task Transport_timeout_hides_unlock_instead_of_requesting_a_nonexistent_code()
    {
        using var api = new SetupApi(_ => throw new TaskCanceledException("The local fixture simulates the status timeout."));
        await using var services = Services(api);
        await using var renderer = new SetupHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SetupPage>());
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);

        Assert.Contains("data-testid=\"setup-status-unavailable\"", html);
        Assert.DoesNotContain("data-testid=\"setup-code-input\"", html);
    }

    [Fact]
    public async Task Recovery_required_explains_storage_recovery_without_an_unlock_form()
    {
        using var api = new SetupApi(_ => Json("RecoveryRequired"));
        await using var services = Services(api);
        await using var renderer = new SetupHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SetupPage>());
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);

        Assert.Contains("This instance needs storage recovery", html);
        Assert.DoesNotContain("data-testid=\"setup-status-unavailable\"", html);
        Assert.DoesNotContain("data-testid=\"setup-code-input\"", html);
    }

    [Theory]
    [InlineData(401, "", "The setup code was not accepted.")]
    [InlineData(403, "", "The setup code was not accepted.")]
    [InlineData(429, "", "Too many setup attempts.")]
    [InlineData(503, "", "The setup service is unavailable.")]
    [InlineData(200, "not valid JSON", "The setup service returned an invalid response.")]
    [InlineData(200, "null", "The setup service returned an incomplete response.")]
    [InlineData(200, "{}", "The setup service returned an incomplete response.")]
    public async Task Unlock_distinguishes_invalid_code_from_service_failures(int status, string body, string expectedMessage)
    {
        using var api = new SetupApi(call => call == 1
            ? Json("Unconfigured")
            : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        await using var services = Services(api);
        await using var renderer = new SetupHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new SetupHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SetupHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(SetupHarness.Reference)] = harness })));
        typeof(SetupPage).GetField("_setupCode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(harness.Page, "local-fixture-code");

        await InvokeHandlerAsync(renderer, harness.Page!, "UnlockAsync");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);

        Assert.Contains(expectedMessage, html);
        Assert.Contains("data-testid=\"setup-code-input\"", html);
        if (status is not (401 or 403)) Assert.DoesNotContain("The setup code was not accepted.", html);
        Assert.Equal(2, api.Calls);
    }

    [Fact]
    public async Task Unlock_conflict_reloads_setup_state_before_asking_for_another_code()
    {
        using var api = new SetupApi(call => call switch
        {
            1 => Json("Unconfigured"),
            2 => new HttpResponseMessage(HttpStatusCode.Conflict),
            _ => Json("RecoveryRequired")
        });
        await using var services = Services(api);
        await using var renderer = new SetupHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new SetupHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SetupHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(SetupHarness.Reference)] = harness })));
        typeof(SetupPage).GetField("_setupCode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(harness.Page, "local-fixture-code");

        await InvokeHandlerAsync(renderer, harness.Page!, "UnlockAsync");
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);

        Assert.Contains("This instance needs storage recovery", html);
        Assert.DoesNotContain("data-testid=\"setup-code-input\"", html);
        Assert.Equal(3, api.Calls);
    }

    [Theory]
    [InlineData("UnlockAsync", "_setupCode", "The setup service took too long to respond.")]
    [InlineData("PrepareStorageAsync", "_session", "Storage preparation took too long to respond.")]
    [InlineData("InitializeAsync", "_session", "Initialization took too long to respond.")]
    public async Task Setup_operation_timeouts_keep_the_page_usable(string handler, string field, string expectedMessage)
    {
        using var api = new SetupApi(call => call == 1
            ? Json("Unconfigured")
            : throw new TaskCanceledException("The local fixture simulates an API timeout."));
        await using var services = Services(api);
        await using var renderer = new SetupHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var harness = new SetupHarnessReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SetupHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(SetupHarness.Reference)] = harness })));
        typeof(SetupPage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(harness.Page, "local-fixture-value");

        await InvokeHandlerAsync(renderer, harness.Page!, handler);
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);

        Assert.Contains(expectedMessage, html);
        Assert.Contains("data-testid=\"setup-wizard\"", html);
        Assert.Equal(2, api.Calls);
    }

    private static Task InvokeHandlerAsync(SetupHtmlRenderer renderer, SetupPage page, string methodName)
    {
        var method = typeof(SetupPage).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = (Func<Task>)method.CreateDelegate(typeof(Func<Task>), page);
        return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem(callback), null));
    }

    private static HttpResponseMessage Json(string state) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"state":"{{state}}"}""")
    };

    private static ServiceProvider Services(IHttpClientFactory api)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton(api);
        services.AddSingleton(new PublicIngressIdentity(new EphemeralDataProtectionProvider()));
        services.AddScoped<NewWeb::HelpDesk.NewWeb.Services.BrowserRequestOrigin>();
        services.AddSingleton<NavigationManager>(new SetupNavigation());
        services.AddSingleton(Substitute.For<IJSRuntime>());
        return services.BuildServiceProvider();
    }

    private sealed class SetupApi(Func<int, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains(request.RequestUri!.AbsolutePath, new[] { "/api/v1/setup/status", "/api/v1/setup/session", "/api/v1/setup/storage", "/api/v1/setup/initialize" });
            return Task.FromResult(respond(++Calls));
        }
    }

    private sealed class SetupNavigation : NavigationManager
    {
        public SetupNavigation() => Initialize("http://127.0.0.1/", "http://127.0.0.1/setup");
    }

    private sealed class SetupHtmlRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : StaticHtmlRenderer(services, loggerFactory)
    {
        // Render the real interactive route in-process without a SignalR connection.
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode) => componentActivator.CreateInstance(componentType);

        public async Task<HtmlRootComponent> RenderComponentAsync<T>(ParameterView? parameters = null) where T : IComponent
        {
            var result = BeginRenderingComponent(typeof(T), parameters ?? ParameterView.Empty);
            await result.QuiescenceTask;
            return result;
        }
    }

    public sealed class SetupHarnessReference
    {
        public SetupPage? Page { get; set; }
    }

    public sealed class SetupHarness : ComponentBase
    {
        [Parameter] public SetupHarnessReference Reference { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<SetupPage>(0);
            builder.AddComponentReferenceCapture(1, component => Reference.Page = (SetupPage)component);
            builder.CloseComponent();
        }
    }
}
