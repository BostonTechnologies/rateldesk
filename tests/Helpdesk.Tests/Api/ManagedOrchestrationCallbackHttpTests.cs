using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class ManagedOrchestrationCallbackHttpTests
{
    private const string ExternalRequest = "recorded-external-request";
    private const string Execution = "recorded-execution";
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Paired_callback_updates_only_its_correlated_execution_and_preserves_idempotent_restart(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true); var task = await SeedAsync(h, client);
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        using var ping = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Get, "/api/v1/orchestration/provider/m2m/ping"); Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        Assert.DoesNotContain(client.Created.ClientSecret, await ping.Content.ReadAsStringAsync());
        using var accepted = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task)); Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        await h.EditAsync(async db => { Assert.Equal(RequestTaskStatus.Completed, (await db.RequestTasks.SingleAsync()).Status); Assert.Null((await db.WorkLogs.SingleAsync()).TechnicianId); });
        await h.RestartAsync(); using var repeated = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task)); Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        await h.EditAsync(async db => Assert.Equal(1, await db.WorkLogs.CountAsync()));
        using var legacy = await PairingBusinessFixture.SendAsync(h, h.LegacyCallbackToken(), HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task)); Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
    }
    [Theory]
    [InlineData(false, "succeeded")][InlineData(true, "succeeded")]
    [InlineData(false, "unknown")][InlineData(true, "unknown")]
    public async Task Wrong_request_execution_mapping_or_task_cannot_mutate_or_disclose_tasks(bool postgres, string status)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true); var task = await SeedAsync(h, client); var foreignClient = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true); var foreign = await SeedAsync(h, foreignClient);
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        foreach (var payload in new[] { new OrchestrationCallbackDto { RequestTaskId = foreign.Id, RequestId = ExternalRequest, ExecutionId = Execution, Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = task.Id, RequestId = "wrong", ExecutionId = Execution, Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = task.Id, RequestId = ExternalRequest, ExecutionId = "wrong", Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = Guid.NewGuid().ToString("N"), RequestId = ExternalRequest, ExecutionId = Execution, Status = "succeeded" } })
        { payload.Status = status; using var denied = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", payload); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        await h.EditAsync(async db => { Assert.All(await db.RequestTasks.ToListAsync(), x => Assert.Equal(RequestTaskStatus.InProgress, x.Status)); Assert.Equal(0, await db.WorkLogs.CountAsync()); });
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Unknown_outcome_survives_restart_without_regression_or_any_retry(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedAsync(h, client);
        const string history = "[{\"OrchestrationExternalRunId\":\"prior-execution\",\"ResultJson\":\"retained-prior-output\"}]";
        const string partial = "{\"output\":\"retained-current-output\"}";
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            saved.OrchestrationExternalRunId = Execution;
            saved.AutomationAttemptCorrelationId = "recorded-attempt";
            saved.AutomationAttemptHistoryJson = history;
            saved.ResultJson = partial;
            saved.MaxRetries = 3;
            saved.RetryCount = 1;
            saved.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        });
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        var unknown = Callback(task);
        unknown.Status = "Unknown";
        unknown.ErrorJson = "{\"reason\":\"Dispatch acknowledgement deadline exceeded\"}";
        using var response = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", unknown);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        string? savedResult = null;
        DateTimeOffset? updated = null;
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(RequestTaskStatus.Failed, saved.Status);
            Assert.Equal(TicketState.OnHold, saved.State);
            Assert.True(AutomationTaskStatuses.RequiresManualReconciliation(saved.LastAutomationStatus));
            Assert.Contains("reconcile", saved.FailureReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(saved.NextRetryAt);
            Assert.Equal(1, saved.RetryCount);
            Assert.Equal(history, saved.AutomationAttemptHistoryJson);
            Assert.Equal("recorded-attempt", saved.AutomationAttemptCorrelationId);
            Assert.Equal(ExternalRequest, saved.OrchestrationExternalRequestId);
            Assert.Equal(Execution, saved.OrchestrationExternalRunId);
            Assert.Equal(Execution, saved.OrchestratorExecutionId);
            Assert.Equal(TicketState.OnHold, (await db.Requests.SingleAsync()).State);
            using var result = JsonDocument.Parse(saved.ResultJson!);
            Assert.Equal(partial, result.RootElement.GetProperty("previousResultJson").GetString());
            Assert.Equal(unknown.ErrorJson, result.RootElement.GetProperty("errorJson").GetString());
            savedResult = saved.ResultJson;
            updated = saved.LastAutomationUpdatedAt;
            Assert.Contains("unknown", (await db.WorkLogs.SingleAsync()).NotesText!, StringComparison.OrdinalIgnoreCase);
        });
        await h.RestartAsync();
        foreach (var status in new[] { "unknown", "running" })
        {
            var repeated = Callback(task); repeated.Status = status;
            using var ignored = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", repeated);
            Assert.Equal(HttpStatusCode.NoContent, ignored.StatusCode);
        }
        using var singleRetry = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id}/retry");
        Assert.Equal(HttpStatusCode.BadRequest, singleRetry.StatusCode);
        using var bulkRetry = await h.AdminAsync(HttpMethod.Post, "/api/v1/request-tasks/bulk/retry", new { ids = new[] { task.Id } });
        Assert.Equal(HttpStatusCode.OK, bulkRetry.StatusCode);
        Assert.Equal(0, (await bulkRetry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetInt32());
        await using (var scope = h.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IRequestTaskRetryProcessor>().ProcessAsync(default);
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(RequestTaskStatus.Failed, saved.Status);
            Assert.True(AutomationTaskStatuses.RequiresManualReconciliation(saved.LastAutomationStatus));
            Assert.Equal(savedResult, saved.ResultJson);
            Assert.Equal(updated, saved.LastAutomationUpdatedAt);
            Assert.Equal(history, saved.AutomationAttemptHistoryJson);
            Assert.Equal("recorded-attempt", saved.AutomationAttemptCorrelationId);
            Assert.Equal(Execution, saved.OrchestrationExternalRunId);
            Assert.Null(saved.NextRetryAt);
            Assert.Equal(1, await db.WorkLogs.CountAsync());
        });
    }

    [Theory]
    [InlineData(false, "succeeded")][InlineData(true, "succeeded")]
    [InlineData(false, "failed")][InlineData(true, "failed")]
    public async Task Known_outcome_reconciles_same_unknown_execution_and_late_callbacks_cannot_regress_it(bool postgres, string outcome)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        var task = await SeedAsync(h, client);
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        var callback = Callback(task); callback.Status = "unknown";
        using var unknown = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", callback);
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
        callback.Status = outcome;
        callback.ResultJson = "{\"output\":\"provider-confirmed\"}";
        using var confirmed = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", callback);
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        foreach (var status in new[] { "unknown", "running", outcome })
        {
            callback.Status = status;
            using var late = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", callback);
            Assert.Equal(HttpStatusCode.NoContent, late.StatusCode);
        }
        await h.EditAsync(async db =>
        {
            var saved = await db.RequestTasks.SingleAsync();
            Assert.Equal(outcome == "succeeded" ? RequestTaskStatus.Completed : RequestTaskStatus.Failed, saved.Status);
            Assert.Equal(outcome, saved.LastAutomationStatus);
            Assert.False(AutomationTaskStatuses.RequiresManualReconciliation(saved.LastAutomationStatus));
            Assert.Equal(Execution, saved.OrchestratorExecutionId);
            Assert.Equal(ExternalRequest, saved.OrchestrationExternalRequestId);
            Assert.Equal(2, await db.WorkLogs.CountAsync());
            using var result = JsonDocument.Parse(saved.ResultJson!);
            Assert.Equal("{\"output\":\"provider-confirmed\"}", result.RootElement.GetProperty("resultJson").GetString());
            Assert.Contains("unknown", result.RootElement.GetProperty("previousResultJson").GetString()!, StringComparison.OrdinalIgnoreCase);
        });
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Delete_revokes_cached_callback_on_other_replica_without_peer_or_task_loss(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true); var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true); var task = await SeedAsync(h, client);
        var token = await PairingBusinessFixture.TokenAsync(h, client); await h.AddReplicaAsync();
        using var delete = await h.AdminAsync(HttpMethod.Delete, $"/api/v1/admin/system-connections/{client.PairId}/mappings/{client.MappingId:D}"); Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var denied = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task), replica: 1); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await h.EditAsync(async db => { Assert.Equal(RequestTaskStatus.InProgress, (await db.RequestTasks.SingleAsync()).Status); Assert.Equal(0, await db.WorkLogs.CountAsync()); });
    }
    private static async Task<RequestTask> SeedAsync(IncidentReceiverTests.Harness h, PairingBusinessFixture.Client client)
    {
        var request = new Request { Id = Guid.NewGuid().ToString("D"), Title = "Scoped automation", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId };
        var task = new RequestTask { Id = Guid.NewGuid().ToString("N"), RequestId = request.Id, OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, Title = "Recorded task", Type = RequestTaskType.Automation,
            State = TicketState.InProgress, Status = RequestTaskStatus.InProgress, OrchestrationExternalRequestId = ExternalRequest, OrchestratorExecutionId = Execution,
            OrchestrationLinkId = client.MappingId.ToString("D"), OrchestrationLinkRevision = 1, OrchestrationPeerInstanceId = client.PeerId };
        await h.EditAsync(db => { db.Requests.Add(request); db.RequestTasks.Add(task); return Task.CompletedTask; }); return task;
    }
    private static OrchestrationCallbackDto Callback(RequestTask task) => new() { RequestTaskId = task.Id, RequestId = ExternalRequest, ExecutionId = Execution, Status = "succeeded" };
}
