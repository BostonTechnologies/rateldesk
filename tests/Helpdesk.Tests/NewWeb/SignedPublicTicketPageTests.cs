extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.PublicAccess;
using Helpdesk.Shared.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.NewWeb;

public sealed class SignedPublicTicketPageTests
{
    [Fact]
    public async Task Anonymous_link_exchange_sets_a_scoped_cookie_and_refreshes_without_staff_credentials()
    {
        using var api = new PublicApi();
        using var baseFactory = WebAuthRoutesTests.CreateFactory(localAuthentication: true);
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("PublicAccessApi").ConfigurePrimaryHttpMessageHandler(() => api)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://desk.example.test")
        });
        using var preview = await client.GetAsync("/public/link");
        Assert.Equal(0, api.Exchanges);
        Assert.True(preview.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(preview.Headers.GetValues("Referrer-Policy")));
        var html = await preview.Content.ReadAsStringAsync();
        var field = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(field.Success);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = field.Groups[1].Value, ["launch"] = new string('a', 43)
        });
        using var exchange = await client.PostAsync("/public/link", form);
        Assert.Equal(HttpStatusCode.SeeOther, exchange.StatusCode);
        var path = exchange.Headers.Location!.OriginalString;
        Assert.Equal($"/public/access/{api.AccessId:D}/ticket", path);
        var cookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"), item => item.StartsWith("__Secure-RatelDeskPublicGrant="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"path=/public/access/{api.AccessId:D}", cookie, StringComparison.OrdinalIgnoreCase);

        for (var reload = 0; reload < 2; reload++)
        {
            using var response = await client.GetAsync(path);
            var markup = await response.Content.ReadAsStringAsync();
            Assert.Contains("Synthetic public ticket", markup);
            Assert.Contains("Public worklog", markup);
            Assert.Contains(">Helpdesk<", markup);
            Assert.Contains(">You<", markup);
            Assert.Contains($"/public/access/{api.AccessId:D}/asset/fixture", markup);
            Assert.DoesNotContain("synthetic-credential", markup);
        }
        Assert.Equal(1, api.Exchanges);
        Assert.Equal(2, api.Views);
        Assert.Equal(2, api.Timelines);

        using var otherVisitor = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = client.BaseAddress! });
        var unauthorized = await otherVisitor.GetStringAsync(path);
        Assert.DoesNotContain("Synthetic public ticket", unauthorized);
        Assert.Contains("session is unavailable", unauthorized);
        Assert.Equal(2, api.Views);
    }

    private sealed class PublicApi : HttpMessageHandler
    {
        private const string Grant = "synthetic-credential";
        internal Guid AccessId { get; } = Guid.NewGuid();
        internal int Exchanges { get; private set; }
        internal int Views { get; private set; }
        internal int Timelines { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Empty(request.RequestUri!.Query);
            var path = request.RequestUri.AbsolutePath;
            Assert.StartsWith(PublicAccessProtocol.ApiPrefix + "/", path);
            if (path == PublicAccessProtocol.ApiPrefix + "/exchange")
            {
                Exchanges++;
                return Reply(new PublicLinkExchangeResponse(AccessId, PublicLinkPurpose.Ticket, Grant, DateTimeOffset.UtcNow.AddMinutes(10)));
            }
            Assert.Equal(Grant, Assert.Single(request.Headers.GetValues(PublicAccessProtocol.GrantHeader)));
            if (path.EndsWith("/ticket", StringComparison.Ordinal))
            {
                Views++;
                return Reply(new PublicTicketViewDto { TrackingId = "INC-PUBLIC", Title = "Synthetic public ticket" });
            }
            Assert.EndsWith("/timeline", path);
            Timelines++;
            return Reply(new[]
            {
                new PublicTicketTimelineItemDto(DateTimeOffset.UtcNow, false, TimelineEventType.Worklog,
                    $"<p>Public worklog</p><img src='/public/access/{AccessId:D}/asset/fixture'>", "Public worklog", null),
                new PublicTicketTimelineItemDto(DateTimeOffset.UtcNow, true, TimelineEventType.CustomerReply, null, "My reply", null)
            });
        }

        private static Task<HttpResponseMessage> Reply<T>(T payload) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
    }
}
