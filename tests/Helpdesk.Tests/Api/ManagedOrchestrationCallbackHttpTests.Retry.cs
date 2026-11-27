using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed partial class ManagedOrchestrationCallbackHttpTests
{
    [Theory]
    [InlineData(false, "BlockRequest", "owned")]
    [InlineData(true, "FailRequest", "owned")]
    [InlineData(false, "FailRequest", "legacy-name")]
    [InlineData(true, "BlockRequest", "legacy-safe")]
    [InlineData(false, "BlockRequest", "legacy-pending")]
    [InlineData(true, "FailRequest", "legacy-pending")]
    public async Task Known_rejection_retry_recovers_the_request_and_admits_one_replacement(bool postgres, string policy, string hold)
    {
        using var provider = new RetryAdmissionProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureRetryProvider(services, provider, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedRetryTaskAsync(h, client, connectivity, policy);
        using var initialStart = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id}/start");
        Assert.Equal(HttpStatusCode.OK, initialStart.StatusCode);

        string? oldAttempt = null;
        const string alreadyCreatedAttempt = "automation-attempt-65de49c291bd464bb99ca05f66decba0";
        await h.EditAsync(async db =>
        {
            var request = await db.Requests.SingleAsync();
            var saved = await db.RequestTasks.SingleAsync();
            oldAttempt = saved.AutomationAttemptCorrelationId;
            Assert.Equal(RequestTaskStatus.Failed, saved.Status);
            Assert.Equal(AutomationTaskStatuses.SubmitRejectedManualRetry, saved.LastAutomationStatus);
            Assert.Equal("approval_required", saved.ProviderDiagnostic?.Reason);
            Assert.Equal(422, saved.ProviderDiagnostic?.HttpStatus);
            Assert.Equal(oldAttempt, saved.ProviderDiagnostic?.CorrelationId);
            Assert.DoesNotContain("provider-canary", JsonSerializer.Serialize(saved.ProviderDiagnostic));
            Assert.Null(saved.OrchestrationExternalRunId);
            Assert.Equal(task.Id, request.WorkflowFailureTaskId);
            Assert.Equal(oldAttempt, request.WorkflowFailureAttemptId);
            Assert.Equal(RequestFailureCategory.SubmissionRejected, request.WorkflowFailureCategory);
            Assert.Equal(policy == "FailRequest" ? "Failed" : "Blocked", request.WorkflowStatus);
            Assert.Equal(TicketState.OnHold, request.State);
            if (hold == "owned") return;

            request.WorkflowFailureTaskId = null;
            request.WorkflowFailureAttemptId = null;
            request.WorkflowFailureCategory = null;
            if (hold == "legacy-name") request.WorkflowBlockReason = $"Critical task '{saved.Name}' failed.";
            if (hold != "legacy-pending") return;

            // The released Retry archived the attempt before resetting the task and left the parent untouched.
            saved.AutomationAttemptHistoryJson = JsonSerializer.Serialize(new[]
            {
                new { saved.AutomationAttemptCorrelationId, saved.LastAutomationStatus, saved.LastAutomationUpdatedAt,
                    saved.ProviderDiagnostic, saved.StartedAt, saved.FailureReason, RetriedAtUtc = DateTimeOffset.UtcNow }
            });
            saved.AutomationAttemptCorrelationId = alreadyCreatedAttempt;
            saved.Status = RequestTaskStatus.Pending;
            saved.State = TicketState.New;
            saved.StartedAt = null;
            saved.AutomationDeadlineUtcTicks = null;
            saved.AutomationNextCheckUtcTicks = null;
            saved.AutomationContinuationPending = false;
            saved.LastAutomationStatus = null;
            saved.LastAutomationUpdatedAt = null;
            saved.ProviderDiagnostic = null;
            saved.FailureReason = null;
            saved.ResultJson = null;
        });
        await h.RestartAsync();
        provider.Repair();

        if (postgres && hold == "owned")
        {
            // Simulate a restart between the committed retry intent and the API's StartAsync call.
            await using (var scope = h.App.Services.CreateAsyncScope())
            {
                var lifecycle = scope.ServiceProvider.GetRequiredService<IRequestTaskLifecycleService>();
                var intent = await lifecycle.RetryAsync(task.Id, default);
                Assert.True(RequestTaskRetryRecovery.HasPendingRetryIntent(intent));
                Assert.Equal(intent.AutomationAttemptCorrelationId, (await lifecycle.RetryAsync(task.Id, default)).AutomationAttemptCorrelationId);
            }
            Assert.Equal(1, provider.Submissions);
            await h.RestartAsync();
            Assert.Equal(1, await SweepAsync(h));
        }
        else if (hold == "legacy-safe")
        {
            using var bulk = await h.AdminAsync(HttpMethod.Post, "/api/v1/request-tasks/bulk/retry", new { ids = new[] { task.Id, task.Id } });
            Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
            Assert.Contains("\"updated\":1", await bulk.Content.ReadAsStringAsync());
        }
        else if (hold == "owned")
        {
            var path = $"/api/v1/request-tasks/{task.Id}/retry";
            var responses = await Task.WhenAll(h.AdminAsync(HttpMethod.Post, path), h.AdminAsync(HttpMethod.Post, path));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
                Assert.All(responses, response => Assert.Contains(response.StatusCode,
                    new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict, HttpStatusCode.BadRequest }));
            }
            finally
            {
                foreach (var response in responses) response.Dispose();
            }
        }
        else
        {
            using var retry = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id}/retry");
            Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        }

        string? replacement = null;
        await h.EditAsync(async db =>
        {
            var request = await db.Requests.SingleAsync();
            var saved = await db.RequestTasks.SingleAsync();
            replacement = saved.AutomationAttemptCorrelationId;
            Assert.NotEqual(oldAttempt, replacement);
            if (hold == "legacy-pending") Assert.Equal(alreadyCreatedAttempt, replacement);
            Assert.Equal(RequestTaskStatus.InProgress, saved.Status);
            Assert.Null(request.WorkflowBlockReason);
            Assert.Null(request.WorkflowFailureTaskId);
            var previous = Assert.Single(ProviderExecutionDiagnostics.RecentRejections(saved));
            Assert.Equal(oldAttempt, previous.CorrelationId);
            Assert.Equal("run_as_denied", previous.Code);
            Assert.Equal("approval_required", previous.Reason);
            Assert.Equal("configuration_required", previous.RetryDisposition);
        });
        using var duplicate = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id}/retry");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(2, provider.Submissions);

        var token = await PairingBusinessFixture.TokenAsync(h, client);
        using var stale = await SendCorrelatedAsync(h, token, Callback(task), oldAttempt!);
        Assert.Equal(HttpStatusCode.NoContent, stale.StatusCode);
        await h.EditAsync(async db => Assert.Equal(RequestTaskStatus.InProgress, (await db.RequestTasks.SingleAsync()).Status));
        using var completed = await SendCorrelatedAsync(h, token, Callback(task), replacement!);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        await h.EditAsync(async db =>
        {
            var request = await db.Requests.SingleAsync();
            Assert.Equal(TicketState.Resolved, request.State);
            Assert.Equal("Completed", request.WorkflowStatus);
            Assert.Equal(RequestTaskStatus.Completed, (await db.RequestTasks.SingleAsync()).Status);
        });
        Assert.Equal(2, provider.Submissions);
    }

    [Theory]
    [InlineData("operator", "OnHold")]
    [InlineData("dependency", "InProgress")]
    [InlineData("approval", "PendingApproval")]
    [InlineData("sibling", "Blocked")]
    [InlineData("reconciliation", "NeedsReconciliation")]
    [InlineData("closure", "Blocked")]
    public async Task Retrying_a_rejected_attempt_keeps_independent_progression_holds(string blocker, string workflowStatus)
    {
        using var provider = new RetryAdmissionProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(false, serviceIdentity: true,
            configureServices: services => ConfigureRetryProvider(services, provider, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedRetryTaskAsync(h, client, connectivity, "BlockRequest");
        using var start = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id}/start");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        await h.EditAsync(async db =>
        {
            var parent = await db.Requests.SingleAsync();
            var child = await db.RequestTasks.SingleAsync();
            switch (blocker)
            {
                case "operator":
                    parent.OperatorHold = true;
                    break;
                case "dependency":
                    child.IsBlocked = true;
                    break;
                case "closure":
                    parent.AdministrativeClosureReason = "Administrative closure";
                    break;
                default:
                    child.Order = 1;
                    db.RequestTasks.Add(new()
                    {
                        RequestId = parent.Id, CustomerId = h.CustomerId, OrganizationId = h.OrganizationId,
                        Title = "Independent task", Order = 0,
                        Type = blocker == "approval" ? RequestTaskType.Approval : RequestTaskType.Automation,
                        Status = blocker == "approval" ? RequestTaskStatus.PendingApproval : RequestTaskStatus.Failed,
                        LastAutomationStatus = blocker == "reconciliation" ? AutomationTaskStatuses.SubmitUncertainManualReconcile : null
                    });
                    break;
            }
        });
        provider.Repair();
        using var retry = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id}/retry");
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        Assert.Equal(1, provider.Submissions);
        await h.EditAsync(async db =>
        {
            var parent = await db.Requests.SingleAsync();
            var saved = await db.RequestTasks.SingleAsync(row => row.Id == task.Id);
            Assert.Equal(workflowStatus, parent.WorkflowStatus);
            Assert.Equal(blocker == "closure" ? RequestTaskStatus.Failed : RequestTaskStatus.Pending, saved.Status);
            if (blocker != "closure") Assert.True(RequestTaskRetryRecovery.HasPendingRetryIntent(saved));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_committed_operator_change_wins_over_an_uncommitted_retry(bool postgres)
    {
        using var provider = new RetryAdmissionProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        var gate = new RetrySaveGate();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                ConfigureRetryProvider(services, provider, connectivity);
                services.AddDbContext<HelpdeskDbContext>(options => options.AddInterceptors(gate));
            });
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedRetryTaskAsync(h, client, connectivity, "BlockRequest");
        using var start = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id}/start");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var retry = h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id}/retry");
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await h.EditAsync(async db =>
            {
                var parent = await db.Requests.SingleAsync();
                if (postgres) parent.AdministrativeClosureReason = "Retired by operator";
                else parent.OperatorHold = true;
            });
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await retry;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await h.EditAsync(async db =>
        {
            var parent = await db.Requests.SingleAsync();
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(RequestTaskStatus.Failed, saved.Status);
            Assert.Equal(parent.WorkflowFailureAttemptId, saved.AutomationAttemptCorrelationId);
            Assert.Null(saved.AutomationAttemptHistoryJson);
            if (postgres) Assert.Equal("Retired by operator", parent.AdministrativeClosureReason);
            else Assert.True(parent.OperatorHold);
        });
        Assert.Equal(1, provider.Submissions);
    }

    private sealed class RetrySaveGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RequestTask>().Any(entry =>
                    entry.State == Microsoft.EntityFrameworkCore.EntityState.Modified && RequestTaskRetryRecovery.HasPendingRetryIntent(entry.Entity)))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_failure_holds_and_attempts_survive_the_additive_upgrade(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        DbContextOptions<HelpdeskDbContext> options = null!;
        await h.EditAsync(async db =>
        {
            var predecessor = db.Database.GetMigrations().Single(name => name.EndsWith("_AddProviderExecutionDiagnostic", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(predecessor);
            options = HistoricalSeedOptions(db, postgres);
        });
        const string hold = "NetRatel rejected submission. Ask support to check the recorded attempt before retrying.";
        var request = new Request { Id = Guid.NewGuid().ToString("D"), Title = "Historical request", OrganizationId = h.OrganizationId,
            CustomerId = h.CustomerId, State = TicketState.OnHold, WorkflowStatus = "Blocked", WorkflowBlockReason = hold };
        await using (var seed = new PreFailureOwnershipSeedContext(options))
        {
            seed.Requests.Add(request);
            seed.RequestTasks.Add(new() { Title = "Historical automation", RequestId = request.Id, OrganizationId = h.OrganizationId,
                CustomerId = h.CustomerId, Type = RequestTaskType.Automation, Status = RequestTaskStatus.Failed,
                LastAutomationStatus = AutomationTaskStatuses.SubmitRejectedManualRetry, AutomationAttemptCorrelationId = "historical-attempt" });
            await seed.SaveChangesAsync();
        }
        await h.EditAsync(async db =>
        {
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            var upgraded = await db.Requests.SingleAsync();
            Assert.Null(upgraded.WorkflowFailureTaskId);
            Assert.Null(upgraded.WorkflowFailureAttemptId);
            Assert.Null(upgraded.WorkflowFailureCategory);
            Assert.Equal(hold, upgraded.WorkflowBlockReason);
            Assert.Equal("historical-attempt", (await db.RequestTasks.SingleAsync()).AutomationAttemptCorrelationId);
        });
    }

    private sealed class PreFailureOwnershipSeedContext(DbContextOptions<HelpdeskDbContext> options)
        : HelpdeskDbContext(options, new UpgradeTenant(), new Microsoft.AspNetCore.Http.HttpContextAccessor())
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Request>().Ignore(request => request.WorkflowFailureTaskId);
            builder.Entity<Request>().Ignore(request => request.WorkflowFailureAttemptId);
            builder.Entity<Request>().Ignore(request => request.WorkflowFailureCategory);
        }
    }

    private static void ConfigureRetryProvider(IServiceCollection services, RetryAdmissionProbe provider, IOrchestrationConnectivityService connectivity)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(PairingTransport.ClientName).Returns(new HttpClient(provider, disposeHandler: false));
        var tokens = Substitute.For<IOrchestrationTokenService>();
        tokens.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("synthetic-pairing-token");
        services.RemoveAll<IOrchestrationInternalClient>();
        services.AddSingleton<IOrchestrationInternalClient>(new OrchestrationInternalClient(factory, tokens, NullLogger<OrchestrationInternalClient>.Instance));
        services.RemoveAll<IOrchestrationConnectivityService>();
        services.AddSingleton(connectivity);
        var payloads = Substitute.For<IRequestTaskPayloadBuilder>();
        payloads.BuildAsync(Arg.Any<RequestTask>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RequestTaskPayloadBuildResult.Succeeded("Synthetic job", "{\"input\":{\"Action\":\"Add\"}}", "binding-1", "42", "42"));
        services.RemoveAll<IRequestTaskPayloadBuilder>();
        services.AddSingleton(payloads);
    }

    private static async Task<RequestTask> SeedRetryTaskAsync(IncidentReceiverTests.Harness h, PairingBusinessFixture.Client client,
        IOrchestrationConnectivityService connectivity, string policy)
    {
        var task = await SeedAsync(h, client);
        connectivity.GetResolvedOrchestrationSettingsAsync(h.OrganizationId, client.MappingId, Arg.Any<CancellationToken>())
            .Returns(new OrchestrationResolvedSettings
            {
                Enabled = true, BaseUrl = "https://provider.example.test", IngestPath = "/internal/ingest",
                Pairing = new(h.OrganizationId, "17", client.PeerId, client.MappingId.ToString("D"), 1)
            });
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            saved.Status = RequestTaskStatus.Pending;
            saved.State = TicketState.New;
            saved.AutoStart = false;
            saved.FailurePolicy = policy;
            saved.OrchestrationExternalRequestId = null;
            saved.OrchestratorExecutionId = null;
        });
        return task;
    }

    private sealed class RetryAdmissionProbe : HttpMessageHandler
    {
        private int submissions;
        private volatile bool repaired;
        public int Submissions => Volatile.Read(ref submissions);
        public void Repair() => repaired = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/internal/ingest", request.RequestUri?.AbsolutePath);
            Interlocked.Increment(ref submissions);
            var payload = (await request.Content!.ReadFromJsonAsync<OrchestrationIngestRequest>(ct))!;
            if (repaired)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    requestId = ExternalRequest, runId = Execution, executionId = Execution, status = "running"
                }) };
            return new(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new
            {
                code = "run_as_denied",
                diagnostic = new ProviderExecutionDiagnostic("NetRatel", "admission", "run_as_denied", "provider-canary",
                    "configuration_required", payload.CorrelationId, false, 422) { Reason = "approval_required" }
            }) };
        }
    }
}
