extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using ConsentPage = NewWeb::HelpDesk.NewWeb.Components.Pages.ServiceLinkConsent;

namespace Helpdesk.Tests.NewWeb;

/// <summary>Real rendered page/event lifecycle with an explicit unit API fixture, not peer provisioning.</summary>
public sealed class ServiceLinkConsentPageTests
{
    [Fact]
    public async Task Responder_details_follow_the_selected_organization_customer_and_narrowed_permissions()
    {
        using var api = new ConsentApi(Status(withSummary: false));
        await using var services = Services(api, responder: true);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        var page = reference.Page!;

        await ChangeAsync(renderer, page, "OrganizationChanged", "organization-a");
        await ChangeAsync(renderer, page, "CustomerChanged", "customer-a");
        await ChangeAsync(renderer, page, "OrganizationChanged", "organization-b");
        var changedOrganization = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Organization: organization-b", VisibleText(changedOrganization));
        Assert.Contains("Customers: None granted", VisibleText(changedOrganization));

        await ChangeAsync(renderer, page, "CustomerChanged", "customer-b");
        await ChangeAsync(renderer, page, "ScopesChanged", new[] { "rateldesk.incident-receipts.read" });
        // Exercise the same generated checkbox assignment through ComponentBase's
        // event lifecycle; static HTML rendering has no interactive DOM dispatcher.
        await renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)page).HandleEventAsync(
            new EventCallbackWorkItem((Action)(() => typeof(ConsentPage).GetField("outboundInvoke", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, false))), null));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        var text = VisibleText(html);
        Assert.Contains("Caller tenant: 17 · target tenant: organization-b", text);
        Assert.Contains("Caller tenant: organization-b · target tenant: 17", text);
        Assert.Contains("Customers: customer-b", text);
        Assert.Contains("Resources: resource-pinned", text);
        Assert.Contains("Request definitions: definition-pinned", text);
        Assert.Equal(new[] { "Scopes: netratel.orchestration.read", "Scopes: rateldesk.incident-receipts.read" }, ScopeParagraphs(html));
        Assert.Contains("The receiver source namespace remains pending", text);
        Assert.DoesNotContain("Stable receiver source namespace:", text);
        Assert.Equal(new[] { "rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.orchestration.callback" }, api.Status.Descriptor.RequestedGrants[0].Scopes);
        Assert.Equal(new[] { "netratel.orchestration.read", "netratel.orchestration.invoke" }, api.Status.Descriptor.RequestedGrants[1].Scopes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Initiator_proposal_and_stored_summaries_retain_their_exact_grant_details(bool withSummary, bool responder)
    {
        using var api = new ConsentApi(Status(withSummary));
        await using var services = Services(api, responder);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        await ChangeAsync(renderer, reference.Page!, "OrganizationChanged", "organization-b");
        await ChangeAsync(renderer, reference.Page!, "CustomerChanged", "customer-b");
        await ChangeAsync(renderer, reference.Page!, "ScopesChanged", new[] { "rateldesk.incident-receipts.read" });
        var text = VisibleText(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        Assert.Contains("Organization: organization-approved", text);
        Assert.Contains("Customers: customer-approved", text);
        Assert.Contains("Resources: resource-pinned", text);
        Assert.Contains("netratel.orchestration.invoke", text);
        Assert.DoesNotContain("Organization: organization-b", text);
        Assert.DoesNotContain("Customers: customer-b", text);
    }

    private static Task ChangeAsync(ConsentRenderer renderer, ConsentPage page, string method, object value)
    {
        var handler = typeof(ConsentPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = (Action)(() => handler.Invoke(page, [value]));
        return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem(callback), null));
    }

    private static string VisibleText(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ");
    private static string[] ScopeParagraphs(string html) => Regex.Matches(html, @"<p\b[^>]*>(.*?)</p>", RegexOptions.Singleline)
        .Select(match => VisibleText(match.Groups[1].Value).Trim()).Where(text => text.StartsWith("Scopes: ", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    private static ServiceLinkAdminStatus Status(bool withSummary)
    {
        ServiceLinkGrant[] grants =
        [
            new() { DirectionId = "initiator_to_responder", CallerProduct = "netratel", CallerTenantId = "17", TargetProduct = "rateldesk", TargetTenantId = "organization-approved",
                Scopes = ["rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.orchestration.callback"],
                ResourceConstraints = new() { OrganizationId = "organization-approved", CustomerIds = ["customer-approved"] } },
            new() { DirectionId = "responder_to_initiator", CallerProduct = "rateldesk", CallerTenantId = "organization-approved", TargetProduct = "netratel", TargetTenantId = "17",
                Scopes = ["netratel.orchestration.read", "netratel.orchestration.invoke"],
                ResourceConstraints = new() { TenantId = "17", ResourceIds = ["resource-pinned"], RequestDefinitionIds = ["definition-pinned"] } }
        ];
        var descriptor = new ServiceLinkRequestDescriptor
        {
            AttemptId = "attempt-ui", RequestedGrants = grants,
            InitiatorEndpointSnapshot = new() { Product = "netratel", WebBaseUrl = "https://nr.example.test", InstanceId = "nr-install" },
            ResponderEndpointSnapshot = new() { Product = "rateldesk", WebBaseUrl = "https://rd.example.test", InstanceId = "rd-install" }
        };
        return new("attempt-ui", withSummary ? "link-ui" : null, 1, withSummary ? "approved" : "awaiting_approval", "organization-approved", "nr-install", "17",
            "undecided", null, null, descriptor, withSummary ? new() { Grants = grants } : null, false, false, false, false, false, null, false, []);
    }

    private static ServiceProvider Services(ConsentApi api, bool responder)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IHttpClientFactory>(api);
        services.AddSingleton<NavigationManager>(new ConsentNavigation(responder));
        services.AddSingleton(Substitute.For<IJSRuntime>());
        services.AddSingleton<AntiforgeryStateProvider>(new UnitAntiforgeryState());
        return services.BuildServiceProvider();
    }

    private sealed class ConsentApi(ServiceLinkAdminStatus status) : HttpMessageHandler, IHttpClientFactory
    {
        public ServiceLinkAdminStatus Status { get; } = status;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://rd.example.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/admin/service-links/attempts/attempt-ui" => Status,
                "/api/v1/admin/service-links" => Array.Empty<ServiceLinkAdminStatus>(),
                "/api/v1/organizations" => new[] { new OrganizationDto { Id = "organization-a", Name = "First", IsEnabled = true }, new OrganizationDto { Id = "organization-b", Name = "Selected", IsEnabled = true } },
                "/api/v1/customers" => new[] { new CustomerDto { Id = "customer-a", OrganizationId = "organization-a", Name = "First", IsEnabled = true }, new CustomerDto { Id = "customer-b", OrganizationId = "organization-b", Name = "Selected", IsEnabled = true } },
                _ => throw new InvalidOperationException("Unexpected unit-fixture API path.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
    }
    private sealed class ConsentNavigation : NavigationManager
    {
        public ConsentNavigation(bool responder) => Initialize("https://rd.example.test/", "https://rd.example.test/account/integration-credentials/link/" + (responder ? "respond" : "review") + "/attempt-ui");
    }
    private sealed class UnitAntiforgeryState : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new("synthetic-unit-token", "__RequestVerificationToken");
    }
    private sealed class ConsentRenderer(IServiceProvider services, ILoggerFactory loggers) : StaticHtmlRenderer(services, loggers)
    {
        protected override IComponent ResolveComponentForRenderMode(Type type, int? parent, IComponentActivator activator, IComponentRenderMode mode) => activator.CreateInstance(type);
        public async Task<HtmlRootComponent> RenderAsync<T>(ParameterView parameters) where T : IComponent
        {
            var root = BeginRenderingComponent(typeof(T), parameters);
            await root.QuiescenceTask;
            return root;
        }
    }
    public sealed class PageReference { public ConsentPage? Page { get; set; } }
    public sealed class PageHarness : ComponentBase
    {
        [Parameter] public PageReference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ConsentPage>(0);
            builder.AddAttribute(1, nameof(ConsentPage.AttemptId), "attempt-ui");
            builder.AddComponentReferenceCapture(2, component => Reference.Page = (ConsentPage)component);
            builder.CloseComponent();
        }
    }
}
