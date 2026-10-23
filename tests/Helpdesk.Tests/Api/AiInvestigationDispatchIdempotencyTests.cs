using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.AiAssistant;
using Helpdesk.Application.AiAssistant;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class AiInvestigationDispatchIdempotencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_first_dispatches_accept_one_bound_invocation_and_one_outbound_request(bool postgres)
    {
        var gate = new QueueCommitGate();
        var competitor = new ObservedQueueStart();
        await using var fixture = await Fixture.CreateAsync(postgres, gate, competitor, new ObservedSqliteDispatchRead(competitor));
        gate.Enabled = true;
        var first = fixture.DispatchAsync();
        Task<HttpResponseMessage> second;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await fixture.AssertCountsAsync(0);
            Assert.Equal(0, fixture.Webhook.Sent);
            Assert.Empty(fixture.Events.ReceivedCalls());
            competitor.Enabled = true;
            second = Task.Run(fixture.DispatchAsync);
            await competitor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        var winner = (await firstResponse.Content.ReadFromJsonAsync<AiInvestigationInvocationDto>())!;
        var replay = (await secondResponse.Content.ReadFromJsonAsync<AiInvestigationInvocationDto>())!;
        Assert.Equal(winner.Id, replay.Id);
        Assert.Equal(winner.CorrelationId, replay.CorrelationId);
        Assert.Equal(1, fixture.Webhook.Sent);
        await fixture.AssertCountsAsync(1);
        using var repeated = await fixture.DispatchAsync();
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(winner.Id, (await repeated.Content.ReadFromJsonAsync<AiInvestigationInvocationDto>())!.Id);
        Assert.Equal(1, fixture.Webhook.Sent);
        await fixture.AssertCountsAsync(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dispatch_key_cannot_select_another_ticket_configuration_actor_or_instruction(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        using var first = await fixture.DispatchAsync();
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        foreach (var (ticket, request) in new[]
        {
            ("ticket-other", fixture.Request),
            ("ticket-a", fixture.Request with { ConfigurationId = Guid.NewGuid() }),
            ("ticket-a", fixture.Request with { OperatorAssistanceRequest = "A different operation." })
        })
        {
            using var denied = await fixture.Host.AdminAsync(HttpMethod.Post, Route(ticket), request);
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            Assert.Contains("ai_investigation_changed", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await using (var scope = fixture.Host.App.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IAiAssistantAiAssistantService>();
            await Assert.ThrowsAsync<AiInvestigationDispatchConflictException>(() => service.DispatchAsync("ticket-a", "incidents",
                fixture.Request, "different-owner", CancellationToken.None));
        }
        foreach (var key in new[] { "", " ", new string('x', 129), "key\r\nheader" })
        {
            using var invalid = await fixture.Host.AdminAsync(HttpMethod.Post, Route("ticket-a"), fixture.Request with { IdempotencyKey = key });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(1, fixture.Webhook.Sent);
        await fixture.AssertCountsAsync(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_queue_commit_publishes_and_dispatches_nothing_and_retry_creates_one(bool postgres)
    {
        var failure = new QueueCommitFailure();
        await using var fixture = await Fixture.CreateAsync(postgres, failure);
        failure.Enabled = true;
        await Assert.ThrowsAsync<IOException>(async () => { using var response = await fixture.DispatchAsync(); });
        await fixture.AssertCountsAsync(0);
        Assert.Equal(0, fixture.Webhook.Sent);
        Assert.Empty(fixture.Events.ReceivedCalls());
        failure.Enabled = false;
        using var retry = await fixture.DispatchAsync();
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(1, fixture.Webhook.Sent);
        await fixture.AssertCountsAsync(1);
    }

    private static string Route(string ticket) => $"/api/v1/incidents/{ticket}/ai-assistant/investigations";

    private sealed class QueueCommitGate : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        private int observed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && Interlocked.Increment(ref observed) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ObservedQueueStart : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class QueueCommitFailure : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            Enabled ? throw new IOException("Synthetic AI dispatch queue commit failure.") : ValueTask.FromResult(result);
    }

    private sealed class ObservedSqliteDispatchRead(ObservedQueueStart start) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // In SQLite shared-cache mode the real request may wait for the writer
            // during authorization, before it can open its own queue transaction.
            if (start.Enabled && eventData.Context?.Database.IsSqlite() == true) start.Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture(IncidentReceiverTests.Harness host, AcceptedWebhook webhook, IAiInvestigationEventBus events,
        DispatchAiInvestigationDto request) : IAsyncDisposable
    {
        public IncidentReceiverTests.Harness Host { get; } = host;
        public AcceptedWebhook Webhook { get; } = webhook;
        public IAiInvestigationEventBus Events { get; } = events;
        public DispatchAiInvestigationDto Request { get; } = request;

        public Task<HttpResponseMessage> DispatchAsync() => Host.AdminAsync(HttpMethod.Post, Route("ticket-a"), Request);

        public async Task AssertCountsAsync(int count)
        {
            await using var scope = Host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            if (db.Database.IsSqlite())
            {
                // An independent cache observes committed isolation instead of
                // waiting on shared-cache table locks held by this test's writer.
                var connection = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()) { Cache = SqliteCacheMode.Private };
                db.Database.SetConnectionString(connection.ConnectionString);
            }
            Assert.Equal(count, await db.AiInvestigationInvocations.CountAsync());
            Assert.Equal(count, await db.AiInvestigationWorklogEntries.CountAsync());
            var audit = await db.AiAssistantWebhookAuditRecords.Where(item => item.Action == "dispatch-queued").ToListAsync();
            Assert.Equal(count, audit.Count);
            Assert.All(audit, item => Assert.Equal("owner", item.Actor));
            if (count == 1)
            {
                var invocation = await db.AiInvestigationInvocations.SingleAsync();
                Assert.Equal("ticket-a", invocation.TicketId);
                Assert.Equal(Host.OrganizationId, invocation.OrganizationId);
                Assert.Equal("owner", invocation.RequestedByUserId);
                Assert.Equal(Request.ConfigurationId, invocation.ConfigurationId);
                Assert.Equal(Request.OperatorAssistanceRequest, invocation.OperatorAssistanceRequest);
                Assert.Equal(Request.IdempotencyKey, invocation.IdempotencyKey);
                Assert.Equal(invocation.Id, (await db.AiInvestigationWorklogEntries.SingleAsync()).InvocationId);
            }
        }

        public static async Task<Fixture> CreateAsync(bool postgres, params IInterceptor[] interceptors)
        {
            var webhook = new AcceptedWebhook();
            var events = Substitute.For<IAiInvestigationEventBus>();
            var host = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
                configureServices: services =>
                {
                    services.AddSingleton<IHttpClientFactory>(webhook);
                    services.AddSingleton(events);
                    services.AddDbContext<HelpdeskDbContext>(options => options.AddInterceptors(interceptors));
                    services.AddAiAgentOrOwnerMcp();
                    services.AddAuthorization(options =>
                    {
                        options.AddPolicy("HelpdeskAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
                        options.AddPolicy("HelpdeskStaff", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
                    });
                }, configureApp: app => app.MapAiAssistantAiAssistantEndpoints());
            using var created = await host.AdminAsync(HttpMethod.Post, "/api/v1/admin/ai-assistant-webhooks/",
                new UpsertAiAssistantWebhookConfigurationDto(host.OrganizationId, "Synthetic dispatch binding", [AiAssistantTicketArea.Incidents],
                    "https://capture.example.test/webhook", null, null, null, SigningSecret: "synthetic-signing-value"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var configuration = (await created.Content.ReadFromJsonAsync<AiAssistantWebhookConfigurationDto>())!;
            await host.EditAsync(db =>
            {
                db.Incidents.AddRange(new Incident { Id = "ticket-a", OrganizationId = host.OrganizationId, Title = "Synthetic primary ticket" },
                    new Incident { Id = "ticket-other", OrganizationId = host.OrganizationId, Title = "Synthetic other ticket" });
                return Task.CompletedTask;
            });
            return new(host, webhook, events, new DispatchAiInvestigationDto(configuration.Id, "Inspect the synthetic incident.", "isolated-dispatch-key"));
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class AcceptedWebhook : IHttpClientFactory
    {
        private int sent;
        public int Sent => Volatile.Read(ref sent);
        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(AcceptedWebhook owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("https://capture.example.test/webhook", request.RequestUri!.AbsoluteUri);
                Assert.True(request.Headers.Contains("X-Webhook-Signature"));
                Assert.Equal("isolated-dispatch-key", Assert.Single(request.Headers.GetValues("X-Webhook-Delivery")));
                Interlocked.Increment(ref owner.sent);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }
        }
    }
}
