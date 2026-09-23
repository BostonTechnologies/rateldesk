using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Timeline;
using Helpdesk.Application.Timeline;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class TimelineEndpointsAuthorizationTests
{
    [Fact]
    public async Task Authenticated_non_administrator_cannot_read_or_retry_global_email_deliveries()
    {
        await using var harness = await TimelineEndpointsHarness.CreateAsync();

        var pendingCount = await harness.Client.GetAsync("/api/v1/timeline/pending-count");
        var failed = await harness.Client.GetAsync("/api/v1/timeline/failed");
        var retryAll = await harness.Client.PostAsync("/api/v1/timeline/retry-all", null);
        var retryOne = await harness.Client.PostAsync($"/api/v1/timeline/{Guid.NewGuid()}/retry", null);
        var previewCurrent = await harness.Client.GetAsync($"/api/v1/timeline/{Guid.NewGuid()}/outgoing-retry-preview");
        var retryCurrent = await harness.Client.PostAsJsonAsync($"/api/v1/timeline/{Guid.NewGuid()}/retry-current-outgoing",
            new ConfirmMailboxOutgoingRetryRequest(2, true));

        Assert.Equal(HttpStatusCode.Forbidden, pendingCount.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, retryAll.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, retryOne.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, previewCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, retryCurrent.StatusCode);
    }

    private sealed class TimelineEndpointsHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TimelineEndpointsHarness(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<TimelineEndpointsHarness> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<ITimelineService>(Substitute.For<ITimelineService>());
            builder.Services.AddSingleton<IRepository<TicketTimelineEvent>>(Substitute.For<IRepository<TicketTimelineEvent>>());
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("HelpdeskAdmin", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin);
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapTimelineEndpoints();
            await app.StartAsync();

            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "ordinary-user");
            return new TimelineEndpointsHarness(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "ordinary-user"),
                new Claim(ClaimTypes.Name, "Ordinary User")
            ],
            Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
