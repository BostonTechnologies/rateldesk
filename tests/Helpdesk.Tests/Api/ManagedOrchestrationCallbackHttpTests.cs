using System.Net;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
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
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Wrong_request_execution_mapping_or_task_cannot_mutate_or_disclose_tasks(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true); var task = await SeedAsync(h, client); var foreignClient = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true); var foreign = await SeedAsync(h, foreignClient);
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        foreach (var payload in new[] { new OrchestrationCallbackDto { RequestTaskId = foreign.Id, RequestId = ExternalRequest, ExecutionId = Execution, Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = task.Id, RequestId = "wrong", ExecutionId = Execution, Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = task.Id, RequestId = ExternalRequest, ExecutionId = "wrong", Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = Guid.NewGuid().ToString("N"), RequestId = ExternalRequest, ExecutionId = Execution, Status = "succeeded" } })
        { using var denied = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", payload); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        await h.EditAsync(async db => { Assert.All(await db.RequestTasks.ToListAsync(), x => Assert.Equal(RequestTaskStatus.InProgress, x.Status)); Assert.Equal(0, await db.WorkLogs.CountAsync()); });
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
