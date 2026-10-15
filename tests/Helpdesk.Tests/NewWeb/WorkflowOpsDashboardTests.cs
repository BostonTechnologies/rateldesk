extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using Dashboard = NewWeb::HelpDesk.NewWeb.Components.Pages.Ops.WorkflowOpsDashboard;
using ConsolePage = NewWeb::HelpDesk.NewWeb.Components.Pages.Ops.OperationsConsole;

namespace Helpdesk.Tests.NewWeb;

public sealed class WorkflowOpsDashboardTests
{
    [Theory]
    [InlineData("critical")]
    [InlineData("overdue")]
    [InlineData("escalated")]
    [InlineData("retries")]
    [InlineData("callbacks")]
    [InlineData("bindings")]
    public async Task Each_category_renders_one_focused_table(string category)
    {
        await using var context = Context("HelpdeskAdmin", out var handler);
        var page = context.Render<Dashboard>(p => p.Add(x => x.Category, category));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("table")));
        Assert.Equal(6, page.FindAll(".workflow-categories button").Count);
        Assert.Contains("Retries pending", page.Find(".workflow-categories").TextContent);
        Assert.Contains("Binding issues", page.Find(".workflow-categories").TextContent);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Contains("Refreshed", page.Markup);
    }

    [Fact]
    public async Task Non_admin_does_not_load_or_offer_retry_diagnostics()
    {
        await using var context = Context("Request.Read", out var handler);
        var page = context.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("table")));
        Assert.Equal(5, page.FindAll(".workflow-categories button").Count);
        Assert.DoesNotContain(handler.Requests, path => path.Contains("retries-pending"));
        Assert.DoesNotContain("Retry now", page.Markup);
    }

    [Fact]
    public async Task Failed_category_is_unavailable_not_a_healthy_zero()
    {
        await using var context = Context("HelpdeskAdmin", out var handler);
        handler.Failure = true;
        var page = context.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("Unavailable", page.Markup));
        Assert.Empty(page.FindAll("table"));
        Assert.Contains("Refresh to retry", page.Markup);
    }

    [Theory]
    [InlineData("Incident.Manager", "/ops", "reports/sla", false)]
    [InlineData("Request.Read", "/ops", "ops/tasks", false)]
    [InlineData("HelpdeskAdmin", "/ops/hangfire", "ops/hangfire/settings", true)]
    [InlineData("Incident.Manager", "/ops/workflows", null, false)]
    [InlineData("Request.Read", "/ops/slas", null, false)]
    [InlineData("Incident.Manager", "/ops/hangfire", null, false)]
    public async Task Console_loads_only_permitted_active_view(string role, string route, string? endpoint, bool jobs)
    {
        await using var context = Context(role, out var handler);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        var page = context.Render<ConsolePage>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[role='tab']").Count));
        if (endpoint is null) Assert.Empty(handler.Requests);
        else page.WaitForAssertion(() => Assert.Contains(handler.Requests, path => path.Contains(endpoint)));
        Assert.Equal(jobs, page.Markup.Contains("Open Dashboard"));
        if (!jobs) Assert.DoesNotContain(handler.Requests, path => path.Contains("hangfire"));
    }

    private static BunitContext Context(string role, out FixtureClient handler)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("Synthetic Operator");
        auth.SetRoles(role);
        handler = new FixtureClient();
        context.Services.AddSingleton<IHttpClientFactory>(handler);
        return context;
    }

    private sealed class FixtureClient : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];
        public bool Failure { get; set; }
        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("https://synthetic.example/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(Failure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) {
                Content = request.RequestUri.AbsolutePath.Contains("lookup") ? JsonContent.Create(Array.Empty<object>()) : JsonContent.Create(new { items = Array.Empty<object>(), totalCount = 0 })
            });
        }
    }
}
