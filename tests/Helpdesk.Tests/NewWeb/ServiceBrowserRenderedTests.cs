extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Helpdesk.Shared.DTOs.Service;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.DTOs.Orchestration;
using MudBlazor;
using NSubstitute;
using Explorer = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.ServiceExplorer;
using Builder = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.RequestFormBuilder;
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

    [Fact]
    public async Task Service_explorer_opens_the_existing_form_with_an_explicit_discard_boundary()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(new ExplorerClient());
        var dialogs = Substitute.For<IDialogService>();
        var dialog = Substitute.For<IDialogReference>();
        dialog.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        dialogs.ShowAsync<Builder>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>()).Returns(dialog);
        context.Services.AddSingleton(dialogs);
        var page = context.Render<Explorer>(p => p.Add(x => x.ServiceId, "synthetic-service"));
        page.WaitForAssertion(() => Assert.Contains("Synthetic request", page.Markup));
        page.Find("button[aria-label='Edit Synthetic request']").Click();
        await dialogs.Received(1).ShowAsync<Builder>("Request Form Builder",
            Arg.Is<DialogParameters>(p => Equals(p[nameof(Builder.ServiceId)], "synthetic-service") && Equals(p[nameof(Builder.RequestFormId)], "synthetic-form")),
            Arg.Is<DialogOptions>(o => o.BackdropClick == false && o.CloseOnEscapeKey == false && o.FullWidth == true));
    }

    private sealed class ExplorerClient : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("https://synthetic.example.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object response = path switch
            {
                "/api/v1/organizations" => new[] { new OrganizationDto { Id = "synthetic-org", Name = "Synthetic organization" } },
                "/api/v1/services/synthetic-service" => new ServiceDto { Id = "synthetic-service", Name = "Synthetic service", Depth = 1 },
                "/api/v1/service-items/synthetic-service" => new[] { new ServiceItemDto { Id = "synthetic-form", Name = "Synthetic request", ItemType = ServiceItemType.RequestForm } },
                "/api/v1/services/synthetic-service/breadcrumb" => new[] { new { Id = "synthetic-service", Name = "Synthetic service" } },
                _ => Array.Empty<AutomationBindingDto>()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        }
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
