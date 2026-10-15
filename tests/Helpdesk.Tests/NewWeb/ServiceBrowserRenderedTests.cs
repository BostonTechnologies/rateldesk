extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Helpdesk.Shared.DTOs.Service;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Browser = NewWeb::HelpDesk.NewWeb.Components.Pages.SelfService.ServiceBrowser;

namespace Helpdesk.Tests.NewWeb;

public sealed class ServiceBrowserRenderedTests
{
    [Fact]
    public async Task Search_filters_real_cards_and_clear_restores_catalogue()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(new FixtureClient());
        var page = context.Render<Browser>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".self-service-catalog-card").Count));
        var search = page.Find("input");
        Assert.Contains("Search services and requests", page.Find("label").TextContent);
        search.Input("hardware");
        page.WaitForAssertion(() => Assert.Equal("Hardware", Assert.Single(page.FindAll(".self-service-card-title")).TextContent));
        page.Find("input").Input("unmatched synthetic term");
        page.WaitForAssertion(() => Assert.Contains("No items found.", page.Markup));
        page.Find("button[aria-label='Clear']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".self-service-catalog-card").Count));
        Assert.Contains("3 requests", page.Markup);
    }

    private sealed class FixtureClient : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("https://synthetic.example/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] {
                new ServiceItemDto { Id = "hardware", Name = "Hardware", ItemType = ServiceItemType.Service, AvailableRequestCount = 2 },
                new ServiceItemDto { Id = "access", Name = "Access request", ItemType = ServiceItemType.RequestForm }
            }) });
    }
}
