extern alias NewWeb;
using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using AutomationReports = NewWeb::HelpDesk.NewWeb.Components.Shared.AutomationReports;

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

    private static BunitContext Context(Api api)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IHttpClientFactory>(api);
        return context;
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
