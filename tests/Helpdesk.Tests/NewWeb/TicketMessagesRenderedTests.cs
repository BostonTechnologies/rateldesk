extern alias NewWeb;
using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using TicketMessages = NewWeb::HelpDesk.NewWeb.Components.Shared.TicketMessages;

namespace Helpdesk.Tests.NewWeb;

public class TicketMessagesRenderedTests
{
    [Fact]
    public async Task TicketChangeCancelsOldRequestAndLateResponseCannotReplaceNewContent()
    {
        using var context = Context(out var api);
        var component = context.Render<TicketMessages>(p => p.Add(x => x.TicketType, "requests").Add(x => x.TicketId, "first"));
        component.WaitForAssertion(() => Assert.Contains("Loading content", component.Markup));
        component.Render(p => p.Add(x => x.TicketType, "requests").Add(x => x.TicketId, "second"));
        Assert.True(api.Calls[0].Token.IsCancellationRequested);
        await component.InvokeAsync(() => api.Calls[1].Response.SetResult(Json("Second request")));
        component.WaitForAssertion(() => Assert.Contains("Second request", component.Markup));
        await component.InvokeAsync(() => api.Calls[0].Response.SetResult(Json("Old request")));
        component.WaitForAssertion(() => { Assert.Contains("Second request", component.Markup); Assert.DoesNotContain("Old request", component.Markup); });
    }

    [Fact]
    public async Task ErrorRetryEmptyAndDisposalHaveDistinctBehavior()
    {
        using var context = Context(out var api);
        var component = context.Render<TicketMessages>(p => p.Add(x => x.TicketType, "requests").Add(x => x.TicketId, "first"));
        await component.InvokeAsync(() => api.Calls[0].Response.SetResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        component.WaitForAssertion(() => Assert.Contains("Unable to load this content", component.Markup));
        component.Find("button").Click();
        await component.InvokeAsync(() => api.Calls[1].Response.SetResult(Json("")));
        component.WaitForAssertion(() => Assert.Contains("No content available", component.Markup));
        component.Render(p => p.Add(x => x.TicketType, "requests").Add(x => x.TicketId, "second"));
        component.Instance.Dispose();
        Assert.True(api.Calls[2].Token.IsCancellationRequested);
        await component.InvokeAsync(() => api.Calls[2].Response.SetCanceled());
    }

    private static BunitContext Context(out DeferredApi api)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        api = new DeferredApi();
        var client = new HttpClient(api) { BaseAddress = new Uri("https://desk.example.test/") };
        var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient("HelpdeskApi").Returns(client);
        context.Services.AddSingleton(factory);
        return context;
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { description = text }), System.Text.Encoding.UTF8, "application/json") };
    private sealed class DeferredApi : HttpMessageHandler
    {
        public List<(CancellationToken Token, TaskCompletionSource<HttpResponseMessage> Response)> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((cancellationToken, response));
            return response.Task;
        }
    }
}
