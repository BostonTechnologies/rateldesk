extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using Bunit;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Polly.CircuitBreaker;
using Polly.Timeout;
using RequestsPage = NewWeb::HelpDesk.NewWeb.Components.Pages.SelfService.MyRequests;

namespace Helpdesk.Tests.NewWeb;

public sealed class MyRequestsRenderedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resilience_failure_retains_the_last_loaded_page_and_has_a_working_retry(bool openCircuit)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        using var api = new FixtureClient();
        context.Services.AddSingleton<IHttpClientFactory>(api);
        context.Render<MudPopoverProvider>();
        var page = context.Render<RequestsPage>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("a[aria-label='Open request REQ-OWN: Own request']")));
        api.Failure = openCircuit ? new BrokenCircuitException("Sensitive synthetic transport details")
            : new TimeoutRejectedException("Sensitive synthetic transport details");
        page.Find("button[aria-label='Refresh requests']").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Requests could not be loaded", page.Markup);
            Assert.Contains("Showing the last loaded result", page.Markup);
            Assert.Single(page.FindAll("a[aria-label='Open request REQ-OWN: Own request']"));
            Assert.DoesNotContain("Sensitive synthetic transport details", page.Markup);
            Assert.False(page.Find("button[aria-label='Refresh requests']").HasAttribute("disabled"));
        });
        api.Failure = null;
        page.Find("button[aria-label='Refresh requests']").Click();
        page.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Requests could not be loaded", page.Markup);
            Assert.Contains("1 matching requests", page.Markup);
        });
        Assert.Equal(3, api.ReadCount);
        Assert.Equal(0, api.WriteCount);
    }

    private sealed class FixtureClient : HttpMessageHandler, IHttpClientFactory
    {
        public Exception? Failure { get; set; }
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://synthetic.example.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) WriteCount++;
            else ReadCount++;
            if (Failure is not null) return Task.FromException<HttpResponseMessage>(Failure);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResponse<MyRequestListItemDto>
                {
                    Page = 1, PageSize = 25, TotalCount = 1,
                    Items = [new MyRequestListItemDto { Id = "own", TrackingId = "REQ-OWN", Title = "Own request", CreatedAt = DateTimeOffset.UtcNow }]
                })
            });
        }
    }
}
