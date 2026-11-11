extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Helpdesk.Shared.DTOs.Dashboard;
using Helpdesk.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Overview = NewWeb::HelpDesk.NewWeb.Components.Pages.Home;

namespace Helpdesk.Tests.NewWeb;

public sealed class OperationsOverviewRenderedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_feed_is_visible_preserves_other_feeds_and_retries_only_the_affected_query(bool malformed)
    {
        await using var context = Context("HelpdeskAdmin", out var api);
        api.FailedPath = "/api/v1/incidents";
        api.Malformed = malformed;
        var page = context.Render<Overview>();
        page.WaitForAssertion(() => Assert.Contains("Recent incidents could not be refreshed.", page.Markup));
        Assert.Equal("Operations Overview", page.Find("h1").TextContent);
        Assert.Equal(3, page.FindAll("[data-testid='operations-recent-panel']").Count);
        Assert.Equal(2, page.FindAll("tbody tr").Count);
        Assert.Contains("Example request", page.Markup);
        Assert.Contains("Example change", page.Markup);
        Assert.DoesNotContain("No recent incidents.", page.Markup);
        Assert.DoesNotContain("Live operational", page.Markup);
        var before = api.Paths.ToArray();
        api.FailedPath = null;
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Retry incidents").Click();
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("tbody tr").Count));
        Assert.DoesNotContain("Recent incidents could not be refreshed.", page.Markup);
        Assert.Equal(before.Count(path => path == "/api/v1/incidents") + 1, api.Paths.Count(path => path == "/api/v1/incidents"));
        Assert.Equal(before.Count(path => path == "/api/v1/requests"), api.Paths.Count(path => path == "/api/v1/requests"));
        Assert.Equal(before.Count(path => path.EndsWith("admin-summary", StringComparison.Ordinal)), api.Paths.Count(path => path.EndsWith("admin-summary", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Dynamic_workload_links_and_all_recent_feeds_retain_exact_destinations_and_accessible_record_links()
    {
        await using var context = Context("HelpdeskAdmin", out var api);
        var page = context.Render<Overview>();
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("tbody tr").Count));
        var links = page.FindAll(".dashboard-kpi-card");
        Assert.Contains(links, link => link.GetAttribute("href") == "/incidents?state=InProgress" && link.TextContent.Contains("In progress"));
        Assert.Contains(links, link => link.GetAttribute("href") == "/requests?state=OnHold" && link.TextContent.Contains("On hold"));
        Assert.Contains(links, link => link.GetAttribute("href") == "/changes?lifecycleState=PendingApproval" && link.TextContent.Contains("Pending approval"));
        Assert.Contains(links, link => link.GetAttribute("href") == "/changes" && link.TextContent.Contains("Pending changes"));
        foreach (var kind in new[] { "incidents", "requests", "changes" })
        {
            var panel = page.Find($"section[aria-label='Recent {kind}']");
            Assert.Equal($"/{kind}", panel.QuerySelector("header a")!.GetAttribute("href"));
            Assert.Equal($"/{kind}/example-{kind}", panel.QuerySelector("tbody a")!.GetAttribute("href"));
            Assert.Contains("2026-11-11T09:00:00", panel.QuerySelector("time")!.GetAttribute("title"));
        }
        Assert.Equal(4, api.Paths.Count);
        Assert.All(api.Queries.Where(query => query.Key != "/api/v1/dashboard/admin-summary"), query => Assert.Equal("?page=1&pageSize=5", query.Value));
    }

    [Theory]
    [InlineData("SelfService.User", "Welcome to Helpdesk")]
    [InlineData("Tenant.Roles.Assign", "Tenant administration")]
    [InlineData("Incident.Read", "Welcome to Helpdesk")]
    [InlineData("No.Modules", "No module access assigned")]
    public async Task Existing_home_roles_do_not_load_administrator_datasets(string role, string expectedHeading)
    {
        await using var context = Context(role, out var api);
        var page = context.Render<Overview>();
        page.WaitForAssertion(() => Assert.Contains(expectedHeading, page.Markup));
        Assert.Empty(page.FindAll("[data-testid='operations-overview']"));
        Assert.Empty(api.Paths);
    }

    private static BunitContext Context(string role, out FixtureApi api)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var authorization = context.AddAuthorization();
        authorization.SetAuthorized("Example operator");
        authorization.SetRoles(role);
        api = new FixtureApi();
        context.Services.AddSingleton<IHttpClientFactory>(api);
        return context;
    }

    private sealed class FixtureApi : HttpMessageHandler, IHttpClientFactory
    {
        public string? FailedPath { get; set; }
        public bool Malformed { get; set; }
        public List<string> Paths { get; } = [];
        public Dictionary<string, string> Queries { get; } = new(StringComparer.Ordinal);
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://example.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path); Queries[path] = request.RequestUri.Query;
            if (path == FailedPath) return Task.FromResult(new HttpResponseMessage(Malformed ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Synthetic invalid response; never expose this body.") });
            object result = path.EndsWith("admin-summary", StringComparison.Ordinal)
                ? new AdminDashboardDto
                {
                    IncidentCountsByState = new() { [TicketState.New] = 2, [TicketState.InProgress] = 3 },
                    RequestCountsByState = new() { ["New"] = 2, ["OnHold"] = 1 },
                    ChangeCountsByState = new() { ["Draft"] = 1, ["PendingApproval"] = 2 }, PendingChanges = 2
                }
                : new { items = new[] { new { id = "example-" + path.Split('/').Last(), trackingId = "EXAMPLE-001", subject = "Example incident", title = path.EndsWith("requests", StringComparison.Ordinal) ? "Example request" : "Example change", state = 3, lifecycleState = 2, updatedAt = "2026-11-11T09:00:00Z" } }, totalCount = 1, total = 1 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}
