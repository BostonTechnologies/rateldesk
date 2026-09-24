using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Timeline;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Administrator_sees_durable_smtp_recipient_results_on_failed_delivery()
    {
        await using var harness = await TimelineEndpointsHarness.CreateAsync();
        var delivery = new TicketTimelineEvent
        {
            TicketId = "ticket-a", EventType = TimelineEventType.EmailDelivery,
            EmailStatus = EmailDeliveryStatus.Failed, EmailRecipient = "accepted@example.test",
            RetryError = "SmtpPartialRecipientAcceptance"
        };
        await harness.SeedAsync(delivery, new MailboxOutboxEffect
        {
            Kind = MailboxEffectKind.Email, EffectKey = "recipient-result",
            DeliveryEventId = delivery.Id, Payload = "{}", State = MailboxEffectState.NeedsReview,
            LastErrorCode = "SmtpPartialRecipientAcceptance",
            RecipientOutcomeJson = JsonSerializer.Serialize(new MailboxRecipientOutcome(
                ["accepted@example.test"], ["rejected@example.test"]))
        });
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "admin-user");

        var response = await harness.Client.GetAsync("/api/v1/timeline/failed");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = Assert.Single((await response.Content.ReadFromJsonAsync<TicketTimelineEventDto[]>())!);
        Assert.Equal("SmtpPartialRecipientAcceptance", result.DeliveryErrorCode);
        Assert.Equal(["accepted@example.test"], result.AcceptedRecipients);
        Assert.Equal(["rejected@example.test"], result.RejectedRecipients);
    }

    private sealed class TimelineEndpointsHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly SqliteConnection connection;

        private TimelineEndpointsHarness(WebApplication app, HttpClient client, SqliteConnection connection)
        {
            _app = app;
            Client = client;
            this.connection = connection;
        }

        public HttpClient Client { get; }

        public static async Task<TimelineEndpointsHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<ITimelineService>(Substitute.For<ITimelineService>());
            builder.Services.AddSingleton<IRepository<TicketTimelineEvent>>(Substitute.For<IRepository<TicketTimelineEvent>>());
            var tenant = Substitute.For<ITenantContext>();
            tenant.IsHelpdeskAdmin.Returns(true);
            builder.Services.AddSingleton(tenant);
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection));
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
            await using (var scope = app.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.EnsureCreatedAsync();

            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "ordinary-user");
            return new TimelineEndpointsHarness(app, client, connection);
        }

        public async Task SeedAsync(TicketTimelineEvent delivery, MailboxOutboxEffect effect)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.TicketTimelineEvents.Add(delivery);
            db.Set<MailboxOutboxEffect>().Add(effect);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await connection.DisposeAsync();
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
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, "ordinary-user"),
                new Claim(ClaimTypes.Name, "Ordinary User")
            };
            if (Request.Headers.Authorization.ToString().Contains("admin-user", StringComparison.Ordinal))
                claims.Add(new Claim(ClaimTypes.Role, HelpdeskPermissions.HelpdeskAdmin));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
