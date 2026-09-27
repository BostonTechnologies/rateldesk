using System.Net;
using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class OrchestrationIngestLifecycleIntegrationTests
{
    private static readonly string SyntheticClientSecret = string.Concat("synthetic", "-", "secret", "-", "123");

    [Theory]
    [InlineData("{\"requestId\":\"41\",\"runId\":\"99\",\"executionId\":\"99\",\"status\":null}", "41", "99", "99")]
    [InlineData("{\"requestId\":\"41\",\"runId\":\"99\",\"executionId\":\"99\"}", "41", "99", "99")]
    [InlineData("{\"requestId\":\"41\",\"runId\":\"99\",\"executionId\":\"99\",\"status\":\"\"}", "41", "99", "99")]
    [InlineData("{\"requestId\":\"41\",\"runId\":\"99\",\"executionId\":\"99\",\"status\":99}", "41", "99", "99")]
    [InlineData("{\"requestId\":\"41\",\"status\":\"Accepted\"}", "41", null, null)]
    [InlineData("{\"runId\":\"99\",\"status\":\"Accepted\"}", null, "99", null)]
    public async Task Incomplete_acknowledgement_is_durably_uncertain_without_an_invented_execution(
        string body,
        string? requestId,
        string? runId,
        string? executionId)
    {
        var result = await RunAsync(body);

        Assert.Equal(RequestTaskStatus.Failed, result.Saved.Status);
        Assert.Equal(TicketState.OnHold, result.Saved.State);
        Assert.Equal(AutomationTaskStatuses.SubmitUncertainManualReconcile, result.Saved.LastAutomationStatus);
        Assert.Equal(requestId, result.Saved.OrchestrationExternalRequestId);
        Assert.Equal(runId, result.Saved.OrchestrationExternalRunId);
        Assert.Equal(executionId, result.Saved.OrchestratorExecutionId);
        Assert.Null(result.Saved.NextRetryAt);
        Assert.Equal(1, result.DispatchCount);
        await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationSubmittedEvent>(), Arg.Any<CancellationToken>());
        await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationRunningEvent>(), Arg.Any<CancellationToken>());
        await result.Events.Received(1).PublishAsync(Arg.Any<RequestTaskAutomationSubmitUncertainEvent>(), Arg.Any<CancellationToken>());
        await result.FailurePolicy.DidNotReceive().OnTaskFailedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Http_error_with_remote_request_id_keeps_identity_without_admission()
    {
        var result = await RunAsync("""{"requestId":"41","status":"Accepted","message":"shape rejected"}""", HttpStatusCode.BadRequest);

        Assert.Equal(RequestTaskStatus.Failed, result.Saved.Status);
        Assert.Equal(TicketState.OnHold, result.Saved.State);
        Assert.Equal("41", result.Saved.OrchestrationExternalRequestId);
        Assert.Null(result.Saved.OrchestratorExecutionId);
        Assert.Equal(AutomationTaskStatuses.SubmitUncertainManualReconcile, result.Saved.LastAutomationStatus);
        Assert.Null(result.Saved.NextRetryAt);
        Assert.Equal(1, result.DispatchCount);
        await result.Events.Received(1).PublishAsync(Arg.Any<RequestTaskAutomationSubmitUncertainEvent>(), Arg.Any<CancellationToken>());
        await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationSubmittedEvent>(), Arg.Any<CancellationToken>());
        await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationRunningEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Accepted", RequestTaskStatus.InProgress)]
    [InlineData("Completed", RequestTaskStatus.Completed)]
    [InlineData("Failed", RequestTaskStatus.Failed)]
    [InlineData("Cancelled", RequestTaskStatus.Failed)]
    [InlineData("provider-specific-state", RequestTaskStatus.Failed)]
    [InlineData("Rejected", RequestTaskStatus.Failed)]
    public async Task Provider_message_is_redacted_in_every_lifecycle_outcome(string status, RequestTaskStatus expectedStatus)
    {
        var hasIdentity = status != "Rejected";
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            requestId = hasIdentity ? "41" : null,
            runId = hasIdentity ? "netratel-run-99" : null,
            executionId = hasIdentity ? "netratel-run-99" : null,
            status,
            message = $"useful context {SyntheticClientSecret} synthetic-bearer-456"
        });
        var result = await RunAsync(body);

        Assert.Equal(expectedStatus, result.Saved.Status);
        var expectedState = expectedStatus switch
        {
            RequestTaskStatus.InProgress => TicketState.InProgress,
            RequestTaskStatus.Completed => TicketState.Resolved,
            _ => TicketState.OnHold
        };
        Assert.Equal(expectedState, result.Saved.State);
        Assert.Null(result.Saved.NextRetryAt);
        Assert.Equal(1, result.DispatchCount);
        Assert.DoesNotContain(SyntheticClientSecret, result.Saved.ResultJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-bearer-456", result.Saved.ResultJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticClientSecret, result.Saved.FailureReason ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-bearer-456", result.Saved.FailureReason ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("useful context", result.Saved.ResultJson ?? result.Saved.FailureReason, StringComparison.Ordinal);
        if (status is "Completed" or "Failed" or "Cancelled" or "provider-specific-state" or "Rejected")
        {
            await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationSubmittedEvent>(), Arg.Any<CancellationToken>());
            await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationRunningEvent>(), Arg.Any<CancellationToken>());
        }

        switch (status)
        {
            case "Accepted":
                Assert.Equal("netratel-run-99", result.Saved.OrchestratorExecutionId);
                await result.Events.Received(1).PublishAsync(
                    Arg.Is<RequestTaskAutomationSubmittedEvent>(e => e.ExecutionId == "netratel-run-99"),
                    Arg.Any<CancellationToken>());
                await result.Events.Received(1).PublishAsync(
                    Arg.Is<RequestTaskAutomationRunningEvent>(e => e.ExecutionId == "netratel-run-99"),
                    Arg.Any<CancellationToken>());
                break;
            case "Completed":
                Assert.Equal(TicketState.Resolved, result.Saved.State);
                Assert.Equal("41", result.Saved.OrchestrationExternalRequestId);
                Assert.Equal("netratel-run-99", result.Saved.OrchestrationExternalRunId);
                Assert.Equal("netratel-run-99", result.Saved.OrchestratorExecutionId);
                await result.Events.Received(1).PublishAsync(Arg.Any<RequestTaskCompletedEvent>(), Arg.Any<CancellationToken>());
                await result.Events.Received(1).PublishAsync(Arg.Any<RequestTaskAutomationCompletedEvent>(), Arg.Any<CancellationToken>());
                break;
            case "Failed":
            case "Cancelled":
                Assert.Equal(TicketState.OnHold, result.Saved.State);
                Assert.Equal("41", result.Saved.OrchestrationExternalRequestId);
                Assert.Equal("netratel-run-99", result.Saved.OrchestrationExternalRunId);
                Assert.Equal("netratel-run-99", result.Saved.OrchestratorExecutionId);
                await result.Events.Received(1).PublishAsync(Arg.Any<RequestTaskFailedEvent>(), Arg.Any<CancellationToken>());
                await result.Events.Received(1).PublishAsync(
                    Arg.Is<RequestTaskAutomationFailedEvent>(e =>
                        e.ExecutionId == "netratel-run-99"
                        && !e.Reason.Contains(SyntheticClientSecret, StringComparison.Ordinal)
                        && !e.Reason.Contains("synthetic-bearer-456", StringComparison.Ordinal)),
                    Arg.Any<CancellationToken>());
                break;
            case "provider-specific-state":
                Assert.Equal(TicketState.OnHold, result.Saved.State);
                Assert.Equal(AutomationTaskStatuses.SubmitUncertainManualReconcile, result.Saved.LastAutomationStatus);
                Assert.Null(result.Saved.NextRetryAt);
                await result.Events.Received(1).PublishAsync(Arg.Any<RequestTaskAutomationSubmitUncertainEvent>(), Arg.Any<CancellationToken>());
                break;
            case "Rejected":
                Assert.Equal(TicketState.OnHold, result.Saved.State);
                Assert.Equal(AutomationTaskStatuses.SubmitRejectedManualRetry, result.Saved.LastAutomationStatus);
                await result.Events.Received(1).PublishAsync(Arg.Any<RequestTaskAutomationSubmitRejectedEvent>(), Arg.Any<CancellationToken>());
                break;
        }
    }

    private static async Task<CaseResult> RunAsync(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return await RunAsync(new ProviderResponder(body, statusCode));
    }

    private static async Task<CaseResult> RunAsync(ProviderResponder handler)
    {
        await using var fixture = await LifecycleFixture.CreateAsync(handler);
        await fixture.StartAsync();
        return await fixture.ReadResultAsync();
    }

    [Fact]
    public async Task Caller_cancellation_after_send_is_durable_uncertain_without_redispatch()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ProviderResponder((_, cancellationToken) =>
        {
            sent.TrySetResult();
            return pendingResponse.Task.WaitAsync(cancellationToken);
        });
        await using var fixture = await LifecycleFixture.CreateAsync(handler);
        using var cancellation = new CancellationTokenSource();

        var submission = fixture.StartAsync(cancellation.Token);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await submission;

        var result = await fixture.ReadResultAsync();
        AssertUncertainSubmission(result, handler.DispatchCount);
        await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationSubmittedEvent>(), Arg.Any<CancellationToken>());
        await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationRunningEvent>(), Arg.Any<CancellationToken>());
        await AssertTaskCannotBeStartedAgainAsync(fixture);
    }

    [Fact]
    public async Task Dropped_provider_reply_is_durable_uncertain_without_redispatch()
    {
        var handler = new ProviderResponder((_, _) => throw new HttpRequestException("synthetic connection dropped after dispatch"));

        var (fixture, result) = await RunWithFixtureAsync(handler);
        await using (fixture)
        {
            AssertUncertainSubmission(result, handler.DispatchCount);
            await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationSubmittedEvent>(), Arg.Any<CancellationToken>());
            await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationRunningEvent>(), Arg.Any<CancellationToken>());
            await AssertTaskCannotBeStartedAgainAsync(fixture);
        }
    }

    [Fact]
    public async Task Oversized_provider_reply_is_durable_uncertain_without_redispatch()
    {
        var handler = new ProviderResponder((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 1024 * 1024 + 1))
        }));

        var (fixture, result) = await RunWithFixtureAsync(handler);
        await using (fixture)
        {
            AssertUncertainSubmission(result, handler.DispatchCount);
            await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationSubmittedEvent>(), Arg.Any<CancellationToken>());
            await result.Events.DidNotReceive().PublishAsync(Arg.Any<RequestTaskAutomationRunningEvent>(), Arg.Any<CancellationToken>());
            await AssertTaskCannotBeStartedAgainAsync(fixture);
        }
    }

    private static async Task<(LifecycleFixture Fixture, CaseResult Result)> RunWithFixtureAsync(ProviderResponder handler)
    {
        var fixture = await LifecycleFixture.CreateAsync(handler);
        await fixture.StartAsync();
        return (fixture, await fixture.ReadResultAsync());
    }

    private static async Task AssertTaskCannotBeStartedAgainAsync(LifecycleFixture fixture)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.StartAsync());
        Assert.Equal(1, fixture.Handler.DispatchCount);
    }

    private static void AssertUncertainSubmission(CaseResult result, int dispatchCount)
    {
        Assert.Equal(RequestTaskStatus.Failed, result.Saved.Status);
        Assert.Equal(TicketState.OnHold, result.Saved.State);
        Assert.Equal(AutomationTaskStatuses.SubmitUncertainManualReconcile, result.Saved.LastAutomationStatus);
        Assert.Null(result.Saved.NextRetryAt);
        Assert.Equal(1, dispatchCount);
    }

    private sealed record CaseResult(RequestTask Saved, IDomainEventPublisher Events, IFailurePolicyEngine FailurePolicy, int DispatchCount);

    private sealed class ProviderResponder : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;
        private int dispatchCount;

        public ProviderResponder(string body, HttpStatusCode statusCode)
            : this((_, _) => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) }))
        {
        }

        public ProviderResponder(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        public int DispatchCount => Volatile.Read(ref dispatchCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref dispatchCount);
            Assert.Equal("synthetic-bearer-456", request.Headers.Authorization?.Parameter);
            return responder(request, cancellationToken);
        }
    }

    private sealed class LifecycleFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<HelpdeskDbContext> options;
        private readonly ITenantContext tenant;
        private readonly HttpContextAccessor accessor;
        private readonly HelpdeskDbContext db;
        private readonly RequestTaskLifecycleService lifecycle;

        private LifecycleFixture(
            SqliteConnection connection,
            DbContextOptions<HelpdeskDbContext> options,
            ITenantContext tenant,
            HttpContextAccessor accessor,
            HelpdeskDbContext db,
            RequestTaskLifecycleService lifecycle,
            string taskId,
            IDomainEventPublisher events,
            IFailurePolicyEngine failurePolicy,
            ProviderResponder handler)
        {
            this.connection = connection;
            this.options = options;
            this.tenant = tenant;
            this.accessor = accessor;
            this.db = db;
            this.lifecycle = lifecycle;
            TaskId = taskId;
            Events = events;
            FailurePolicy = failurePolicy;
            Handler = handler;
        }

        public string TaskId { get; }
        public IDomainEventPublisher Events { get; }
        public IFailurePolicyEngine FailurePolicy { get; }
        public ProviderResponder Handler { get; }

        public static async Task<LifecycleFixture> CreateAsync(ProviderResponder handler)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("synthetic-tenant");
            var accessor = new HttpContextAccessor();
            var db = new HelpdeskDbContext(options, tenant, accessor);
            await db.Database.EnsureCreatedAsync();
            var requestId = Guid.NewGuid().ToString("N");
            var taskId = Guid.NewGuid().ToString("N");
            db.Requests.Add(new Request { Id = requestId, OrganizationId = "synthetic-tenant", Title = "Synthetic request", Description = "Synthetic request" });
            db.RequestTasks.Add(new RequestTask
            {
                Id = taskId,
                RequestId = requestId,
                OrganizationId = "synthetic-tenant",
                Title = "Synthetic automation",
                Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Pending
            });
            await db.SaveChangesAsync();

            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
            var tokens = Substitute.For<IOrchestrationTokenService>();
            tokens.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>())
                .Returns("synthetic-bearer-456");
            var actualClient = new OrchestrationInternalClient(factory, tokens, NullLogger<OrchestrationInternalClient>.Instance);
            var connectivity = Substitute.For<IOrchestrationConnectivityService>();
            connectivity.GetResolvedOrchestrationSettingsAsync(Arg.Any<CancellationToken>()).Returns(new OrchestrationResolvedSettings
            {
                Enabled = true,
                BaseUrl = "https://provider.example.test",
                IngestPath = "/internal/ingest",
                ClientSecret = SyntheticClientSecret
            });
            var payloads = Substitute.For<IRequestTaskPayloadBuilder>();
            payloads.BuildAsync(Arg.Any<RequestTask>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(RequestTaskPayloadBuildResult.Succeeded("Synthetic job", "{}", "binding-1", "request-definition-1", "job-definition-1"));
            var events = Substitute.For<IDomainEventPublisher>();
            var failurePolicy = Substitute.For<IFailurePolicyEngine>();
            var correlation = Substitute.For<ICorrelationContext>();
            correlation.GetCorrelationId().Returns("synthetic-correlation");
            var lifecycle = new RequestTaskLifecycleService(
                new EfRepository<RequestTask>(db),
                new EfRepository<Request>(db),
                connectivity,
                Substitute.For<IAutomationBindingService>(),
                payloads,
                actualClient,
                failurePolicy,
                events,
                correlation,
                NullLogger<RequestTaskLifecycleService>.Instance);

            return new LifecycleFixture(connection, options, tenant, accessor, db, lifecycle, taskId, events, failurePolicy, handler);
        }

        public Task<RequestTask> StartAsync(CancellationToken cancellationToken = default)
            => lifecycle.StartAsync(TaskId, cancellationToken);

        public async Task<CaseResult> ReadResultAsync()
        {
            await using var verification = new HelpdeskDbContext(options, tenant, accessor);
            var saved = await verification.RequestTasks.AsNoTracking().SingleAsync(task => task.Id == TaskId);
            return new CaseResult(saved, Events, FailurePolicy, Handler.DispatchCount);
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
