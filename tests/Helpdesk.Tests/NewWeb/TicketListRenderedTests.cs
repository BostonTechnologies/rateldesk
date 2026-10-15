extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using IncidentList = NewWeb::HelpDesk.NewWeb.Components.Pages.Incidents.IncidentList;
using RequestList = NewWeb::HelpDesk.NewWeb.Components.Pages.Requests.RequestList;
using ChangeList = NewWeb::HelpDesk.NewWeb.Components.Pages.Changes.ChangeList;

namespace Helpdesk.Tests.NewWeb;

public sealed class TicketListRenderedTests
{
    [Theory]
    [InlineData("incidents", typeof(IncidentList))]
    [InlineData("requests", typeof(RequestList))]
    [InlineData("changes", typeof(ChangeList))]
    public void Lists_render_shared_controls_without_obsolete_AI_filters_and_restore_query(string module, Type component)
    {
        using var context = new BunitContext(); context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization(); auth.SetAuthorized("Synthetic reader"); auth.SetRoles("Incident.Read", "Request.Read", "Change.Read");
        var handler = new FixtureClient(); context.Services.AddSingleton<IHttpClientFactory>(handler);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/{module}?search=synthetic&customerOrgId=org&priority=High&aiInvolved=true&aiOnly=true");
        var page = context.Render<DynamicComponent>(p => p.Add(x => x.Type, component));
        page.WaitForAssertion(() => Assert.Contains(handler.Requests, url => url.Contains("q=synthetic") && url.Contains("customerOrgId=org") && url.Contains("priority=High")));
        Assert.DoesNotContain("AI involved only", page.Markup); Assert.DoesNotContain("AI origin only", page.Markup);
        Assert.All(handler.Requests, url => { Assert.DoesNotContain("aiInvolved", url); Assert.DoesNotContain("aiOnly", url); });
        Assert.Contains("Filters (2)", page.Markup); Assert.Contains("No tickets match these filters.", page.Markup);
        page.WaitForAssertion(() => Assert.Contains("Remove Customer: Named organization", page.Markup));
        page.FindAll("button").Single(button => button.TextContent.Contains("Clear all")).Click();
        page.WaitForAssertion(() => Assert.Contains("Filters (0)", page.Markup));
        Assert.DoesNotContain("customerOrgId", navigation.Uri); Assert.DoesNotContain("search=", navigation.Uri);
    }
    private sealed class FixtureClient : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("https://synthetic.example/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath.EndsWith("/filter-options"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { items = new[] { new { id = "org", label = "Named organization" } }, hasMore = false }) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { items = Array.Empty<object>(), totalCount = 0 }) });
        }
    }
}
