extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using Page = NewWeb::HelpDesk.NewWeb.Components.Pages.ViewTicket;
using ITimelineApiClient = NewWeb::HelpDesk.NewWeb.Services.ITimelineApiClient;
using TimelineStreamService = NewWeb::HelpDesk.NewWeb.Services.TimelineStreamService;

namespace Helpdesk.Tests.NewWeb;

public sealed class SignedPublicTicketPageTests
{
    [Fact]
    public async Task AnonymousSignedLinkAndReloadShowOnlyPublicHistoryWithoutSessionExchange()
    {
        using var api = new Api();
        for (var reload = 0; reload < 2; reload++)
        {
            await using var context = Context(api);
            context.Services.GetRequiredService<NavigationManager>().NavigateTo("/view-ticket?trackingId=INC-PUBLIC&email=customer%40example.test&token=signed-fixture");
            var page = context.Render<Page>();
            page.WaitForAssertion(() =>
            {
                Assert.Contains("Signed public ticket", page.Markup);
                Assert.Contains("Public worklog", page.Markup);
                Assert.DoesNotContain("Internal-only note", page.Markup);
            });
            Assert.Equal(0, api.PrivateSessionRequests);
            Assert.Equal(0, api.PrivateMarkSeenRequests);
        }
        Assert.Equal(2, api.PublicViewRequests);
        Assert.Equal(2, api.PublicTimelineRequests);
    }

    [Fact]
    public async Task RejectedSignatureDoesNotFallBackToPrivateSessionOrRevealTicket()
    {
        using var api = new Api { SignatureAccepted = false };
        await using var context = Context(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/view-ticket?trackingId=INC-PUBLIC&email=customer%40example.test&token=wrong-signature");
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Contains("Ticket Not Found", page.Markup, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Signed public ticket", page.Markup);
        Assert.Equal(0, api.PrivateSessionRequests);
        Assert.Equal(0, api.PublicTimelineRequests);
    }

    private static BunitContext Context(Api api)
    {
        var context = new BunitContext();context.Services.AddMudServices();context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(api);
        context.Services.AddSingleton<AuthenticationStateProvider>(new Anonymous());
        context.Services.AddSingleton(Substitute.For<ITimelineApiClient>());
        context.Services.AddSingleton(new TimelineStreamService(api, NullLogger<TimelineStreamService>.Instance));
        return context;
    }
    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
    private sealed class Api : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> clients = [];
        public bool SignatureAccepted { get; set; } = true;
        public int PrivateSessionRequests { get; set; }
        public int PrivateMarkSeenRequests { get; set; }
        public int PublicViewRequests { get; set; }
        public int PublicTimelineRequests { get; set; }
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new Boundary(this, name)) { BaseAddress = new Uri("https://desk.example.test/") };
            clients.Add(client);return client;
        }
        public void Dispose() { foreach (var client in clients) client.Dispose(); }
    }
    private sealed class Boundary(Api api, string clientName) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (clientName != "SystemApiNoAuth")
            {
                api.PrivateSessionRequests++;
                throw new HttpRequestException("Synthetic anonymous session cannot be exchanged.");
            }
            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.AbsolutePath.EndsWith("mark-as-seen", StringComparison.Ordinal)) api.PrivateMarkSeenRequests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith("/api/v1/tickets/public/", request.RequestUri.AbsolutePath);
            if (!api.SignatureAccepted) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (request.RequestUri.AbsolutePath.EndsWith("/view", StringComparison.Ordinal))
            {
                api.PublicViewRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PublicTicketViewDto { Id = "public-ticket", TrackingId = "INC-PUBLIC", Title = "Signed public ticket" }) });
            }
            api.PublicTimelineRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[]
            {
                new TicketTimelineEventDto { EventType = TimelineEventType.Worklog, MessageText = "Public worklog", CreatedUtc = DateTimeOffset.UtcNow },
                new TicketTimelineEventDto { EventType = TimelineEventType.InternalNote, MessageText = "Internal-only note", CreatedUtc = DateTimeOffset.UtcNow }
            }) });
        }
    }
}
