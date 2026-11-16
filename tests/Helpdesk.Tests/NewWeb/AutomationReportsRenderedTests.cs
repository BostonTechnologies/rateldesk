extern alias NewWeb;
using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using AutomationReports = NewWeb::HelpDesk.NewWeb.Components.Shared.AutomationReports;
using TicketActivityTabs = NewWeb::HelpDesk.NewWeb.Components.Shared.TicketActivityTabs;

namespace Helpdesk.Tests.NewWeb;

public sealed class AutomationReportsRenderedTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void Staff_only_or_inaccessible_reports_do_not_show_a_loading_failure(HttpStatusCode status)
    {
        using var api = new Api(status);
        using var context = Context(api);
        var component = context.Render<AutomationReports>(p => p.Add(x => x.TicketId, "synthetic-incident"));
        component.WaitForAssertion(() =>
        {
            Assert.Equal(1, api.Calls);
            Assert.DoesNotContain("Automation reports could not be loaded", component.Markup);
            Assert.Empty(component.FindAll("button"));
        });
    }

    [Fact]
    public void Server_failure_remains_visible_and_retry_can_return_an_empty_report_list()
    {
        using var api = new Api(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        using var context = Context(api);
        var component = context.Render<AutomationReports>(p => p.Add(x => x.TicketId, "synthetic-incident"));
        component.WaitForAssertion(() => Assert.Contains("Automation reports could not be loaded", component.Markup));
        component.Find("button").Click();
        component.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.Calls);
            Assert.Empty(component.FindAll("[role='alert']"));
        });
    }

    [Theory]
    [InlineData("incidents")]
    [InlineData("requests")]
    public void Staff_reports_load_only_after_selecting_the_automation_tab(string type)
    {
        using var api = new TabApi();
        using var context = Context(api);
        var component = context.Render<TicketActivityTabs>(p => p.Add(x => x.TicketId, "demo")
            .Add(x => x.TicketType, type).Add(x => x.CanViewAutomationReports, true));
        var tab = component.FindAll("[role=tab]").Single(x => x.TextContent.Trim() == "Automation");
        Assert.Equal(0, api.ReportReads);
        tab.Click();
        component.WaitForAssertion(() =>
        {
            Assert.Equal(1, api.ReportReads);
            Assert.Equal("true", tab.GetAttribute("aria-selected"));
            Assert.Contains("No automation reports for this ticket", component.Markup);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Public_or_nonstaff_activity_has_no_internal_automation_tab(bool publicMode, bool canView)
    {
        using var api = new TabApi();
        using var context = Context(api);
        var component = context.Render<TicketActivityTabs>(p => p.Add(x => x.TicketId, "demo")
            .Add(x => x.PublicMode, publicMode).Add(x => x.CanViewAutomationReports, canView));
        Assert.DoesNotContain(component.FindAll("[role=tab]"), x => x.TextContent.Trim() == "Automation");
        Assert.Equal(0, api.ReportReads);
    }

    private static BunitContext Context(IHttpClientFactory api)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IHttpClientFactory>(api);
        return context;
    }

    private sealed class TabApi : HttpMessageHandler, IHttpClientFactory
    {
        public int ReportReads { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://desk.example.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/count", StringComparison.Ordinal) ? "0" : "{\"description\":\"\",\"items\":[],\"totalCount\":0}";
            if (path.EndsWith("/automation-reports", StringComparison.Ordinal)) { ReportReads++; json = "[]"; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Api(params HttpStatusCode[] statuses) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://desk.example.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/tickets/synthetic-incident/automation-reports", request.RequestUri!.AbsolutePath);
            var status = statuses[Calls++];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
