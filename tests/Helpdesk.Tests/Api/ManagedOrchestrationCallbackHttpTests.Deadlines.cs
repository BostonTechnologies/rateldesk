using System.Net;
using Microsoft.AspNetCore.Http;
using Helpdesk.Shared.Services;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.Shared.DTOs.RequestForm;
using System.Text.Json;
using Helpdesk.API.Background;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed partial class ManagedOrchestrationCallbackHttpTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Deadline_schema_upgrade_preserves_existing_attempt_and_parent_history_on_both_providers(bool postgres)
    {
        await using var fixture = await Helpdesk.Tests.Infrastructure.Persistence.ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        var started = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        DbContextOptions<HelpdeskDbContext> options;
        await using (var setup = fixture.Open())
        {
            await setup.GetService<IMigrator>().MigrateAsync(postgres
                ? "20261108173602_AddPendingEmailOperations" : "20261108173712_AddPendingEmailOperations");
            options = HistoricalSeedOptions(setup, postgres);
        }
        await using (var seed = new PreDeadlineSeedContext(options))
        {
            seed.Requests.Add(new Request { Id = "upgrade-parent", Title = "Legacy request", TrackingId = "REQ-UPGRADE", State = TicketState.InProgress, WorkflowStatus = "InProgress" });
            seed.RequestTasks.Add(new RequestTask { Id = "upgrade-task", RequestId = "upgrade-parent", Title = "Legacy running task", TrackingId = "TSK-UPGRADE",
                Type = RequestTaskType.Automation, Status = RequestTaskStatus.InProgress, StartedAt = started,
                HardTimeoutSeconds = 120, AutomationAttemptCorrelationId = "retained-attempt", AutomationAttemptHistoryJson = "[]",
                OrchestrationExternalRequestId = "retained-request", OrchestrationExternalRunId = "retained-run" });
            await seed.SaveChangesAsync();
        }
        await using var upgraded = fixture.Open();
        await upgraded.Database.MigrateAsync();
        await upgraded.Database.MigrateAsync();
        Assert.False(upgraded.Database.HasPendingModelChanges());
        var task = await upgraded.RequestTasks.SingleAsync();
        Assert.Equal(started, task.StartedAt);
        Assert.Equal("retained-attempt", task.AutomationAttemptCorrelationId);
        Assert.Equal("retained-request", task.OrchestrationExternalRequestId);
        Assert.Equal("retained-run", task.OrchestrationExternalRunId);
        Assert.Equal("[]", task.AutomationAttemptHistoryJson);
        Assert.Null(task.AutomationDeadlineUtcTicks);
        Assert.False(task.AutomationContinuationPending);
        Assert.Equal(0, task.AutomationStateVersion);
        Assert.Equal(0, (await upgraded.Requests.SingleAsync()).WorkflowStateVersion);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Operator_hold_migration_preserves_ambiguous_historic_holds_without_adopting_timeout_or_failure_holds(bool postgres)
    {
        await using var fixture = await Helpdesk.Tests.Infrastructure.Persistence.ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        DbContextOptions<HelpdeskDbContext> options;
        await using (var setup = fixture.Open())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261111161000_AddAutomationExecutionDeadlines");
            options = HistoricalSeedOptions(setup, postgres);
        }
        await using (var seed = new PreOperatorHoldSeedContext(options))
        {
            seed.Requests.AddRange(
                new Request { Id = "old-operator", State = TicketState.OnHold, TrackingId = "REQ-OPERATOR", PayloadJson = "retained-input" },
                new Request { Id = "old-timeout", State = TicketState.OnHold, WorkflowStatus = "NeedsReconciliation", WorkflowBlockReason = AutomationTaskStatuses.ReconciliationHoldReason },
                new Request { Id = "old-failure", State = TicketState.OnHold, WorkflowStatus = "Blocked", WorkflowBlockReason = "Preserved independent reason" });
            await seed.SaveChangesAsync();
        }
        await using var upgraded = fixture.Open();
        await upgraded.Database.MigrateAsync();
        await upgraded.Database.MigrateAsync();
        Assert.False(upgraded.Database.HasPendingModelChanges());
        var oldOperator = await upgraded.Requests.SingleAsync(row => row.Id == "old-operator");
        Assert.True(oldOperator.OperatorHold);
        Assert.Equal("retained-input", oldOperator.PayloadJson);
        Assert.Equal("REQ-OPERATOR", oldOperator.TrackingId);
        Assert.False((await upgraded.Requests.SingleAsync(row => row.Id == "old-timeout")).OperatorHold);
        var failure = await upgraded.Requests.SingleAsync(row => row.Id == "old-failure");
        Assert.False(failure.OperatorHold);
        Assert.Equal("Preserved independent reason", failure.WorkflowBlockReason);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Legacy_running_repair_uses_bounded_indexed_batches_beyond_one_hundred_rows(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        await h.EditAsync(async db =>
        {
            (await db.RequestTasks.SingleAsync()).StartedAt = clock.GetUtcNow().AddDays(-30);
            for (var index = 0; index < 100; index++)
                db.RequestTasks.Add(new RequestTask { Id = Guid.NewGuid().ToString("N"), Title = $"Legacy attempt {index}",
                    RequestId = task.RequestId, OrganizationId = h.OrganizationId, CustomerId = h.CustomerId,
                    Type = RequestTaskType.Automation, Status = RequestTaskStatus.InProgress,
                    StartedAt = clock.GetUtcNow().AddDays(-30), HardTimeoutSeconds = 2400 });
        });
        Assert.Equal(50, await SweepAsync(h));
        Assert.Equal(50, await SweepAsync(h));
        Assert.Equal(1, await SweepAsync(h));
        Assert.Equal(0, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            Assert.Equal(101, await db.RequestTasks.CountAsync(row => row.LastAutomationStatus == AutomationTaskStatuses.ExecutionUnknownManualReconcile));
            Assert.Equal(101, await db.WorkLogs.CountAsync());
            var sql = postgres
                ? "SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'Tickets'"
                : "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Tickets'";
            var indexes = await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
            Assert.Contains("IX_Tickets_Type_AutomationNextCheckUtcTicks", indexes);
            Assert.Contains("IX_Tickets_Type_AutomationContinuationPending", indexes);
        });
        Assert.Equal(0, probe.Submissions);
    }

    [Theory]
    [InlineData(false, "succeeded")][InlineData(true, "succeeded")]
    [InlineData(false, "failed")][InlineData(true, "failed")]
    [InlineData(false, "cancelled")][InlineData(true, "cancelled")]
    public async Task Missing_callback_reconciles_confirmed_results_before_deadline_and_applies_failure_policy_only_to_known_failure(bool postgres, string outcome)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        await SeedDeadlineAsync(h, client, clock, connectivity);
        probe.Response = query => Task.FromResult<OrchestrationCallbackDto?>(ReadResult(query, outcome));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await SweepAsync(h));
        Assert.Equal(0, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(outcome == "succeeded" ? RequestTaskStatus.Completed : RequestTaskStatus.Failed, saved.Status);
            Assert.Equal(outcome, saved.LastAutomationStatus);
            Assert.Null(saved.AutomationTimedOutAt);
            Assert.Equal(outcome == "succeeded" ? 0 : 1, saved.RetryCount);
            Assert.Equal(outcome != "succeeded", saved.NextRetryAt.HasValue);
            Assert.False(saved.AutomationContinuationPending);
        });
        Assert.Equal(1, probe.Reads);
        Assert.Equal(0, probe.Submissions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Changed_saved_mapping_revision_cannot_be_used_to_reconcile_an_earlier_admitted_attempt(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        await SeedDeadlineAsync(h, client, clock, connectivity);
        connectivity.GetResolvedOrchestrationSettingsAsync(h.OrganizationId, client.MappingId, Arg.Any<CancellationToken>())
            .Returns(new OrchestrationResolvedSettings { Enabled = true, Pairing = new(h.OrganizationId, "synthetic-peer-tenant", client.PeerId, client.MappingId.ToString("D"), 2) });
        probe.Response = query => Task.FromResult<OrchestrationCallbackDto?>(ReadResult(query, "succeeded"));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db => Assert.Equal(AutomationTaskStatuses.ExecutionUnknownManualReconcile,
            (await db.RequestTasks.SingleAsync()).LastAutomationStatus));
        Assert.Equal(0, probe.Reads);
        Assert.Equal(0, probe.Submissions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Durable_deadline_holds_unknown_without_retry_or_failure_policy_and_late_success_recovers_only_its_hold(bool postgres)
    {
        var clock = new DeadlineClock();
        var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        probe.Response = query => Task.FromResult<OrchestrationCallbackDto?>(ReadResult(query, "running"));
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db => Assert.Equal(RequestTaskStatus.InProgress, (await db.RequestTasks.SingleAsync()).Status));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(RequestTaskStatus.Failed, saved.Status);
            Assert.Equal(AutomationTaskStatuses.ExecutionUnknownManualReconcile, saved.LastAutomationStatus);
            Assert.Equal(clock.GetUtcNow(), saved.AutomationTimedOutAt);
            Assert.Equal("deadline-attempt", saved.AutomationTimedOutAttemptId);
            Assert.Null(saved.NextRetryAt);
            Assert.Equal(0, saved.RetryCount);
            Assert.Equal("NeedsReconciliation", (await db.Requests.SingleAsync()).WorkflowStatus);
            Assert.Equal(TicketState.OnHold, (await db.Requests.SingleAsync()).State);
            Assert.Equal(1, await db.WorkLogs.CountAsync());
            Assert.Equal(0, await db.Incidents.CountAsync());
        });
        Assert.Equal(0, await SweepAsync(h));
        await h.RestartAsync();
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        var callback = Callback(task);
        callback.CorrelationId = "deadline-attempt";
        using var completed = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", callback);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        using var duplicate = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", callback);
        Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(RequestTaskStatus.Completed, saved.Status);
            Assert.NotNull(saved.AutomationTimedOutAt);
            Assert.Equal("deadline-attempt", saved.AutomationAttemptCorrelationId);
            Assert.Equal("Completed", (await db.Requests.SingleAsync()).WorkflowStatus);
            Assert.Equal(TicketState.Resolved, (await db.Requests.SingleAsync()).State);
            Assert.Equal(2, await db.WorkLogs.CountAsync());
        });
        Assert.Equal(0, probe.Submissions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Restart_and_competing_deadline_workers_transition_once_and_preserve_an_unrelated_parent_hold(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        await h.EditAsync(async db =>
        {
            var parent = await db.Requests.SingleAsync();
            parent.WorkflowStatus = "Blocked"; parent.WorkflowBlockReason = "Independent operator hold"; parent.State = TicketState.OnHold;
            var saved = await db.RequestTasks.SingleAsync();
            saved.AutomationReconciliationLeaseId = "abandoned-worker";
            saved.AutomationReconciliationLeaseUntilUtcTicks = clock.GetUtcNow().AddSeconds(121).UtcTicks;
        });
        clock.Advance(TimeSpan.FromSeconds(120));
        await h.RestartAsync();
        Assert.Equal(0, await SweepAsync(h));
        clock.Advance(TimeSpan.FromSeconds(2));
        var results = await Task.WhenAll(SweepAsync(h), SweepAsync(h));
        Assert.Equal(1, results.Sum());
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        using var completed = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        await h.EditAsync(async db =>
        {
            Assert.Equal(RequestTaskStatus.Completed, (await db.RequestTasks.SingleAsync()).Status);
            var parent = await db.Requests.SingleAsync();
            Assert.Equal("Blocked", parent.WorkflowStatus);
            Assert.Equal("Independent operator hold", parent.WorkflowBlockReason);
            Assert.Equal(TicketState.OnHold, parent.State);
            Assert.Equal(2, await db.WorkLogs.CountAsync());
        });
        Assert.Equal(1, probe.Reads);
        Assert.Equal(0, probe.Submissions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Callback_winning_a_watchdog_race_cannot_be_overwritten_or_duplicate_its_committed_effects(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Response = async query =>
        {
            readStarted.SetResult();
            await releaseRead.Task;
            return ReadResult(query, "running");
        };
        clock.Advance(TimeSpan.FromMinutes(3));
        var sweep = SweepAsync(h);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        using var completed = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        releaseRead.SetResult();
        await sweep;
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(RequestTaskStatus.Completed, saved.Status);
            Assert.Equal("succeeded", saved.LastAutomationStatus);
            Assert.Null(saved.AutomationTimedOutAt);
            Assert.Equal(1, await db.WorkLogs.CountAsync());
            Assert.Equal(TicketState.Resolved, (await db.Requests.SingleAsync()).State);
        });
        Assert.Equal(0, probe.Submissions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Missing_legacy_timing_is_reconciled_but_manual_and_approval_clocks_are_not_execution_deadlines(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            saved.StartedAt = null; saved.ExpectedRuntimeSeconds = null; saved.GraceSeconds = null; saved.HardTimeoutSeconds = null;
            saved.AutomationAttemptCorrelationId = null;
            foreach (var type in new[] { RequestTaskType.Manual, RequestTaskType.Approval })
                db.RequestTasks.Add(new RequestTask { Id = Guid.NewGuid().ToString("N"), RequestId = task.RequestId,
                    OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, Type = type,
                    Status = RequestTaskStatus.InProgress, StartedAt = clock.GetUtcNow().AddDays(-90),
                    DueAt = clock.GetUtcNow().AddDays(-1), Title = $"Long human {type}" });
        });
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            Assert.Equal(2, await db.RequestTasks.CountAsync(row => row.Type != RequestTaskType.Automation && row.Status == RequestTaskStatus.InProgress));
            Assert.Equal(AutomationTaskStatuses.ExecutionUnknownManualReconcile, (await db.RequestTasks.SingleAsync(row => row.Id == task.Id)).LastAutomationStatus);
        });
        Assert.Equal(0, probe.Reads);
        Assert.Equal(0, probe.Submissions);
    }

    [Theory]
    [InlineData(false, "quick")][InlineData(true, "quick")]
    [InlineData(false, "put")][InlineData(true, "put")]
    [InlineData(false, "bulk")][InlineData(true, "bulk")]
    [InlineData(false, "take-over")][InlineData(true, "take-over")]
    public async Task Supported_operator_state_routes_preserve_their_hold_after_a_late_same_attempt_success(bool postgres, string route)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                ConfigureDeadlines(services, clock, probe, connectivity);
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("RequestAccess", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("RequestManager", policy => policy.RequireAuthenticatedUser());
                });
            }, configureApp: app => app.MapRequestEndpoints());
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var dependentId = Guid.NewGuid().ToString("N");
        await h.EditAsync(async db =>
        {
            var schema = new RequestFormSchemaModel { Tasks =
                [new() { Id = first, Type = "automation" }, new() { Id = second, Type = "automation", Order = 1, DependsOn = [first], AutoStart = false }] };
            var form = new RequestForm { Title = "Hold test", OrganizationId = h.OrganizationId,
                JsonSchema = JsonSerializer.SerializeToDocument(schema, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            db.RequestForms.Add(form);
            (await db.Requests.SingleAsync()).RequestFormId = form.Id;
            (await db.RequestTasks.SingleAsync()).TemplateId = first.ToString();
            db.RequestTasks.Add(new RequestTask { Id = dependentId, Title = "Dependent task", RequestId = task.RequestId,
                OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, TemplateId = second.ToString(),
                Type = RequestTaskType.Automation, Status = RequestTaskStatus.Pending, IsBlocked = true, AutoStart = false, Order = 1 });
        });
        if (route == "take-over")
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            Assert.Equal(1, await SweepAsync(h));
        }
        using var held = await SetOperatorStateAsync(h, task.RequestId, route, TicketState.OnHold);
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        if (route != "take-over")
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            Assert.Equal(1, await SweepAsync(h));
        }
        await h.RestartAsync();
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        using var completed = await SendCorrelatedAsync(h, token, Callback(task), "deadline-attempt");
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        await h.EditAsync(async db =>
        {
            var parent = await db.Requests.SingleAsync();
            Assert.True(parent.OperatorHold);
            Assert.Equal(TicketState.OnHold, parent.State);
            Assert.Equal(RequestHoldPolicy.OperatorStatus, parent.WorkflowStatus);
            Assert.Equal(RequestHoldPolicy.OperatorReason, parent.WorkflowBlockReason);
            var dependent = await db.RequestTasks.SingleAsync(row => row.Id == dependentId);
            Assert.True(dependent.IsBlocked);
            Assert.Equal(RequestTaskStatus.Pending, dependent.Status);
            Assert.Equal(RequestTaskStatus.Completed, (await db.RequestTasks.SingleAsync(row => row.Id == task.Id)).Status);
        });
        using var released = await SetOperatorStateAsync(h, task.RequestId, "quick", TicketState.InProgress);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            var parent = await db.Requests.SingleAsync();
            Assert.False(parent.OperatorHold);
            Assert.Null(parent.WorkflowBlockReason);
            Assert.NotEqual(TicketState.OnHold, parent.State);
            Assert.False((await db.RequestTasks.SingleAsync(row => row.Id == dependentId)).IsBlocked);
        });
        Assert.Equal(0, probe.Submissions);
    }

    private static Task<HttpResponseMessage> SetOperatorStateAsync(IncidentReceiverTests.Harness h, string requestId, string route, TicketState state)
        => route switch
        {
            "put" => h.AdminAsync(HttpMethod.Put, $"/api/v1/requests/{requestId}", new { state }),
            "bulk" => h.AdminAsync(HttpMethod.Post, "/api/v1/requests/bulk/state", new { ids = new[] { requestId }, newState = state }),
            _ => h.AdminAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/state", new { newState = state })
        };

    [Theory][InlineData(false)][InlineData(true)]
    public async Task An_operator_release_committed_after_a_held_continuation_evaluation_cannot_be_cleared_by_that_old_worker(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe(); var barrier = new HeldContinuationClear();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                ConfigureDeadlines(services, clock, probe, connectivity);
                services.AddDbContext<HelpdeskDbContext>(options => options.AddInterceptors(barrier));
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("RequestAccess", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("RequestManager", policy => policy.RequireAuthenticatedUser());
                });
            }, configureApp: app => app.MapRequestEndpoints());
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await h.EditAsync(async db =>
        {
            var schema = new RequestFormSchemaModel { Tasks =
                [new() { Id = first, Type = "automation" }, new() { Id = second, Type = "manual", Order = 1, DependsOn = [first] }] };
            var form = new RequestForm { Title = "Release race", OrganizationId = h.OrganizationId,
                JsonSchema = JsonSerializer.SerializeToDocument(schema, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            db.RequestForms.Add(form);
            var parent = await db.Requests.SingleAsync(); parent.RequestFormId = form.Id;
            parent.OperatorHold = true; parent.State = TicketState.OnHold;
            parent.WorkflowStatus = RequestHoldPolicy.OperatorStatus; parent.WorkflowBlockReason = RequestHoldPolicy.OperatorReason;
            var completed = await db.RequestTasks.SingleAsync(); completed.Status = RequestTaskStatus.Completed;
            completed.LastAutomationStatus = "succeeded"; completed.AutomationContinuationPending = true; completed.TemplateId = first.ToString();
            db.RequestTasks.Add(new RequestTask { Id = "release-dependent", Title = "Dependent", RequestId = task.RequestId,
                OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, TemplateId = second.ToString(),
                Type = RequestTaskType.Manual, Status = RequestTaskStatus.Pending, IsBlocked = true, Order = 1 });
        });
        var firstSweep = SweepAsync(h);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            using var release = await SetOperatorStateAsync(h, task.RequestId, "quick", TicketState.InProgress);
            Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, await firstSweep);
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync(row => row.Id == task.Id);
            Assert.True(saved.AutomationContinuationPending);
            Assert.Null(saved.AutomationReconciliationLeaseId);
            Assert.True((await db.RequestTasks.SingleAsync(row => row.Id == "release-dependent")).IsBlocked);
        });
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            Assert.False((await db.RequestTasks.SingleAsync(row => row.Id == task.Id)).AutomationContinuationPending);
            Assert.False((await db.RequestTasks.SingleAsync(row => row.Id == "release-dependent")).IsBlocked);
            Assert.False((await db.Requests.SingleAsync()).OperatorHold);
        });
        Assert.Equal(0, probe.Submissions); Assert.Equal(0, probe.Reads);
    }

    private sealed class HeldContinuationClear : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"AutomationContinuationPending\" =", StringComparison.Ordinal)
                && command.CommandText.Contains("\"AutomationReconciliationLeaseId\" = NULL", StringComparison.Ordinal)
                && Entered.TrySetResult())
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Durable_continuation_recomputes_an_approval_only_workflow_without_applying_an_execution_deadline(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        await SeedDeadlineAsync(h, client, clock, connectivity);
        await h.EditAsync(async db =>
        {
            var task = await db.RequestTasks.SingleAsync();
            task.Type = RequestTaskType.Approval; task.Status = RequestTaskStatus.Completed;
            task.AutomationContinuationPending = true; task.AutomationAttemptCorrelationId = null;
            task.AutomationDeadlineUtcTicks = null; task.LastAutomationStatus = null;
        });
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            Assert.Equal(TicketState.Resolved, (await db.Requests.SingleAsync()).State);
            var task = await db.RequestTasks.SingleAsync();
            Assert.False(task.AutomationContinuationPending); Assert.Null(task.AutomationDeadlineUtcTicks);
            Assert.Null(task.AutomationTimedOutAt); Assert.Equal(RequestTaskStatus.Completed, task.Status);
        });
        Assert.Equal(0, probe.Reads); Assert.Equal(0, probe.Submissions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task A_successful_start_commits_its_parent_fence_attempt_and_deadline_before_remote_dispatch_can_finish(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        var payloads = Substitute.For<IRequestTaskPayloadBuilder>();
        payloads.BuildAsync(Arg.Any<RequestTask>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RequestTaskPayloadBuildResult.Succeeded("Synthetic job", "{}"));
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgement = new TaskCompletionSource<OrchestrationIngestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.SubmissionResponse = _ => { dispatched.TrySetResult(); return acknowledgement.Task; };
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                ConfigureDeadlines(services, clock, probe, connectivity);
                services.RemoveAll<IRequestTaskPayloadBuilder>(); services.AddSingleton(payloads);
            });
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        await h.EditAsync(async db =>
        {
            var pending = await db.RequestTasks.SingleAsync();
            pending.Status = RequestTaskStatus.Pending; pending.State = TicketState.New;
            pending.StartedAt = null; pending.AutomationAttemptCorrelationId = null;
            pending.OrchestrationExternalRequestId = null; pending.OrchestrationExternalRunId = null; pending.OrchestratorExecutionId = null;
        });
        await using var scope = h.App.Services.CreateAsyncScope();
        var start = scope.ServiceProvider.GetRequiredService<IRequestTaskLifecycleService>().StartAsync(task.Id, default);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(RequestTaskStatus.InProgress, saved.Status);
            Assert.NotNull(saved.StartedAt); Assert.False(string.IsNullOrWhiteSpace(saved.AutomationAttemptCorrelationId));
            Assert.InRange(saved.AutomationDeadlineUtcTicks!.Value - saved.StartedAt.Value.UtcTicks,
                120 * TimeSpan.TicksPerSecond, 120 * TimeSpan.TicksPerSecond + TimeSpan.TicksPerMicrosecond - 1);
            Assert.True(saved.AutomationStateVersion > 0);
            Assert.True((await db.Requests.SingleAsync()).WorkflowStateVersion > 0);
        });
        acknowledgement.SetResult(new OrchestrationIngestResult { RequestId = ExternalRequest, RunId = Execution, ExecutionId = Execution,
            Status = "running", Disposition = OrchestrationSubmissionDisposition.Admitted, Outcome = OrchestrationExecutionOutcome.Processing });
        Assert.Equal(RequestTaskStatus.InProgress, (await start).Status);
        Assert.Equal(1, probe.Submissions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task A_hold_committed_while_binding_readiness_is_awaited_fences_the_start_before_any_provider_submission(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        var bindings = Substitute.For<IAutomationBindingService>();
        var readyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyRelease = new TaskCompletionSource<AutomationBindingDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bindings.GetByTaskTemplateAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { readyStarted.TrySetResult(); return readyRelease.Task; });
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                ConfigureDeadlines(services, clock, probe, connectivity);
                services.RemoveAll<IAutomationBindingService>(); services.AddSingleton(bindings);
            });
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var running = await SeedDeadlineAsync(h, client, clock, connectivity);
        var pendingId = Guid.NewGuid().ToString("N");
        await h.EditAsync(async db =>
        {
            var form = new RequestForm { Title = "Start fence", OrganizationId = h.OrganizationId };
            db.RequestForms.Add(form); (await db.Requests.SingleAsync()).RequestFormId = form.Id;
            db.RequestTasks.Add(new RequestTask { Id = pendingId, Title = "Second eligible task", RequestId = running.RequestId,
                OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Pending, TemplateId = Guid.NewGuid().ToString(), AutoStart = false });
        });
        await using var scope = h.App.Services.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRequestTaskLifecycleService>();
        var start = lifecycle.StartAsync(pendingId, default);
        await readyStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await SweepAsync(h));
        readyRelease.SetResult(new AutomationBindingDto { Enabled = true, SystemConnectionId = client.MappingId });
        await Assert.ThrowsAsync<RequestProgressionHeldException>(() => start);
        // A caller that catches the rejected start cannot persist its stale tracked intent later.
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().SaveChangesAsync();
        await h.EditAsync(async db =>
        {
            var pending = await db.RequestTasks.SingleAsync(row => row.Id == pendingId);
            Assert.Equal(RequestTaskStatus.Pending, pending.Status);
            Assert.Null(pending.StartedAt);
            Assert.Null(pending.AutomationAttemptCorrelationId);
            Assert.Equal("NeedsReconciliation", (await db.Requests.SingleAsync()).WorkflowStatus);
        });
        Assert.Equal(0, probe.Submissions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Provider_configuration_failures_and_already_uncertain_submissions_still_record_a_durable_timeout_hold(bool postgres)
    {
        var clock = new DeadlineClock(); var probe = new DeadlineProbe();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => ConfigureDeadlines(services, clock, probe, connectivity));
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedDeadlineAsync(h, client, clock, connectivity);
        probe.Response = _ => throw new ArgumentException("Synthetic invalid saved provider configuration.");
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.NotNull(saved.AutomationTimedOutAt);
            Assert.Equal("NeedsReconciliation", (await db.Requests.SingleAsync()).WorkflowStatus);
            // Cover a submit-uncertain record left before the parent hold committed.
            saved.LastAutomationStatus = AutomationTaskStatuses.SubmitUncertainManualReconcile;
            saved.AutomationTimedOutAt = null; saved.AutomationNextCheckUtcTicks = null;
            var parent = await db.Requests.SingleAsync(); parent.WorkflowStatus = "InProgress";
            parent.WorkflowBlockReason = null; parent.State = TicketState.InProgress;
        });
        Assert.Equal(1, await SweepAsync(h));
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.NotNull(saved.AutomationTimedOutAt);
            Assert.Equal(task.Id, saved.Id);
            Assert.Equal("NeedsReconciliation", (await db.Requests.SingleAsync()).WorkflowStatus);
            Assert.Equal(0, saved.RetryCount); Assert.Null(saved.NextRetryAt);
        });
        Assert.Equal(0, probe.Submissions);
    }

    private static DbContextOptions<HelpdeskDbContext> HistoricalSeedOptions(HelpdeskDbContext current, bool postgres)
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>();
        if (postgres) options.UseNpgsql(current.Database.GetConnectionString());
        else options.UseSqlite(current.Database.GetConnectionString(), sqlite => sqlite.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"));
        return options.Options;
    }

    private class PreOperatorHoldSeedContext(DbContextOptions<HelpdeskDbContext> options)
        : HelpdeskDbContext(options, new UpgradeTenant(), new HttpContextAccessor())
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Request>().Ignore(request => request.OperatorHold);
            builder.Entity<Customer>().Ignore("IdentityUserId");
            if (Database.IsSqlite())
            {
                // SQLite's retirement migration follows the fixed deadline schema.
                builder.Entity<User>().Ignore("IsDeleted");
                builder.Entity<User>().Ignore("DeletedAtUtc");
            }
        }
    }

    private sealed class PreDeadlineSeedContext(DbContextOptions<HelpdeskDbContext> options) : PreOperatorHoldSeedContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var name in new[] { "SelfServiceSubmissionId", "SelfServiceSubmitterId", "SelfServiceSubmissionHash", nameof(Request.WorkflowStateVersion) })
                builder.Entity<Request>().Ignore(name);
            foreach (var name in new[] { nameof(RequestTask.AutomationDeadlineUtcTicks), nameof(RequestTask.AutomationNextCheckUtcTicks),
                nameof(RequestTask.AutomationTimedOutAt), nameof(RequestTask.AutomationTimedOutAttemptId),
                nameof(RequestTask.AutomationReconciliationLeaseId), nameof(RequestTask.AutomationReconciliationLeaseUntilUtcTicks),
                nameof(RequestTask.AutomationContinuationPending), nameof(RequestTask.AutomationStateVersion) })
                builder.Entity<RequestTask>().Ignore(name);
            builder.Entity<User>().Ignore("IsDeleted");
            builder.Entity<User>().Ignore("DeletedAtUtc");
        }
    }

    private sealed class UpgradeTenant : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }

    private static void ConfigureDeadlines(IServiceCollection services, DeadlineClock clock, DeadlineProbe probe, IOrchestrationConnectivityService connectivity)
    {
        services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        services.RemoveAll<IOrchestrationInternalClient>(); services.AddSingleton<IOrchestrationInternalClient>(probe);
        services.RemoveAll<IOrchestrationConnectivityService>(); services.AddSingleton(connectivity);
    }

    private static async Task<RequestTask> SeedDeadlineAsync(IncidentReceiverTests.Harness h, PairingBusinessFixture.Client client,
        DeadlineClock clock, IOrchestrationConnectivityService connectivity)
    {
        var task = await SeedAsync(h, client);
        connectivity.GetResolvedOrchestrationSettingsAsync(h.OrganizationId, client.MappingId, Arg.Any<CancellationToken>())
            .Returns(new OrchestrationResolvedSettings { Enabled = true, Pairing = new(h.OrganizationId, "synthetic-peer-tenant", client.PeerId, client.MappingId.ToString("D"), 1) });
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            saved.StartedAt = clock.GetUtcNow(); saved.ExpectedRuntimeSeconds = 90; saved.GraceSeconds = 30; saved.HardTimeoutSeconds = 120;
            saved.AutomationAttemptCorrelationId = "deadline-attempt"; saved.OrchestrationExternalRunId = Execution;
            saved.FailurePolicy = "FailRequest"; saved.IsCritical = true; saved.MaxRetries = 3;
            var parent = await db.Requests.SingleAsync(); parent.State = TicketState.InProgress; parent.WorkflowStatus = "InProgress";
        });
        return task;
    }

    private static async Task<int> SweepAsync(IncidentReceiverTests.Harness h)
    {
        await using var scope = h.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AutomationExecutionDeadlineProcessor>().ProcessAsync(default);
    }

    private static OrchestrationCallbackDto ReadResult(OrchestrationExecutionQuery query, string status) => new()
    {
        RequestTaskId = query.RequestTaskId, RequestId = query.OrchestrationRequestId, CorrelationId = query.CorrelationId,
        OrchestrationRequestId = query.OrchestrationRequestId, OrchestrationRunId = query.OrchestrationRunId,
        ExecutionId = query.OrchestrationRunId, Status = status
    };

    private sealed class DeadlineClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class DeadlineProbe : IOrchestrationInternalClient
    {
        public int Reads;
        public int Submissions;
        public Func<OrchestrationIngestRequest, Task<OrchestrationIngestResult>>? SubmissionResponse { get; set; }
        public Func<OrchestrationExecutionQuery, Task<OrchestrationCallbackDto?>> Response { get; set; } = _ => Task.FromResult<OrchestrationCallbackDto?>(null);
        public Task<OrchestrationHealthResult> HealthAsync(OrchestrationResolvedSettings settings, CancellationToken cancellationToken = default, bool useTokenCache = true)
            => Task.FromResult(new OrchestrationHealthResult { Success = true });
        public Task<OrchestrationIngestResult> IngestAsync(OrchestrationResolvedSettings settings, OrchestrationIngestRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Submissions);
            return SubmissionResponse is not null ? SubmissionResponse(request)
                : throw new InvalidOperationException("The deadline worker must never submit executions.");
        }
        public Task<OrchestrationCallbackDto?> ReadExecutionAsync(OrchestrationResolvedSettings settings, OrchestrationExecutionQuery execution, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return Response(execution);
        }
    }
}
