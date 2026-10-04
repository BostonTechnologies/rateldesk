using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Helpdesk.Tests.Api;

/// <summary>Resource authorization evidence through the real issuer/registry and both supported databases.
/// Reciprocal consent/activation conformance is exercised separately; these clients use explicit manual task grants.</summary>
[Collection("Incident receiver")]
public sealed class ManagedOrchestrationCallbackHttpTests
{
    private const string ExternalRequest = "recorded-external-request";
    private const string Execution = "recorded-execution";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dedicated_manual_callback_client_updates_only_approved_recorded_execution_and_preserves_legacy(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var task = await SeedTaskAsync(h);
        var client = await CreateClientAsync(h, task);
        var token = await TokenAsync(h, client);
        using var ping = await SendAsync(h, token, HttpMethod.Get, "/api/v1/orchestration/provider/m2m/ping");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        var pingBody = await ping.Content.ReadAsStringAsync();
        Assert.DoesNotContain(client.GetProperty("clientSecret").GetString()!, pingBody);
        Assert.Equal("no-store", ping.Headers.CacheControl?.ToString());
        using var accepted = await SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        await h.EditAsync(async db =>
        {
            var updated = await db.RequestTasks.SingleAsync(x => x.Id == task.Id);
            Assert.Equal(RequestTaskStatus.Completed, updated.Status);
            Assert.Equal(Execution, updated.OrchestratorExecutionId);
            Assert.Null((await db.WorkLogs.SingleAsync()).TechnicianId);
        });
        using var duplicate = await SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task));
        Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);
        await h.EditAsync(async db => Assert.Equal(1, await db.WorkLogs.CountAsync()));

        await h.RestartAsync();
        using var replayAfterRestart = await SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task));
        Assert.Equal(HttpStatusCode.NoContent, replayAfterRestart.StatusCode);
        using var legacy = await SendAsync(h, h.LegacyCallbackToken(), HttpMethod.Post,
            "/api/v1/orchestration/provider/callback", Callback(task));
        Assert.Equal(HttpStatusCode.NoContent, legacy.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_task_request_execution_or_mapping_cannot_mutate_or_disclose_tasks(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var task = await SeedTaskAsync(h);
        var foreign = await SeedTaskAsync(h);
        var client = await CreateClientAsync(h, task);
        var token = await TokenAsync(h, client);
        foreach (var dto in new[]
        {
            new OrchestrationCallbackDto { RequestTaskId = foreign.Id, RequestId = ExternalRequest, ExecutionId = Execution, Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = task.Id, RequestId = "wrong-request", ExecutionId = Execution, Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = task.Id, RequestId = ExternalRequest, ExecutionId = "wrong-execution", Status = "succeeded" },
            new OrchestrationCallbackDto { RequestTaskId = task.Id, ExecutionId = Execution, Status = "succeeded" },
            new OrchestrationCallbackDto { RequestId = ExternalRequest, ExecutionId = Execution, Status = "succeeded" }
        })
        {
            using var denied = await SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", dto);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        // The same organization alone does not grant arbitrary task authority.
        await h.EditAsync(async db =>
        {
            Assert.All(await db.RequestTasks.ToListAsync(), row => Assert.Equal(RequestTaskStatus.InProgress, row.Status));
            Assert.Equal(0, await db.WorkLogs.CountAsync());
        });
        await h.EditAsync(async db => (await db.RequestTasks.SingleAsync(x => x.Id == task.Id)).OrchestrationPeerInstanceId = "foreign-instance");
        using var wrongPeer = await SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task));
        Assert.Equal(HttpStatusCode.Forbidden, wrongPeer.StatusCode);
        await h.SetEnabledAsync("organization", false);
        using var disabled = await SendAsync(h, token, HttpMethod.Get, "/api/v1/orchestration/provider/m2m/ping");
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_scope_has_no_incident_authority_and_cached_token_observes_revocation_on_replica(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var task = await SeedTaskAsync(h);
        var client = await CreateClientAsync(h, task);
        var token = await TokenAsync(h, client);
        using var incident = await h.CreateIncidentAsync("callback-is-not-create", credential: token);
        Assert.Equal(HttpStatusCode.Forbidden, incident.StatusCode);
        await h.AddReplicaAsync();
        using var revoke = await h.AdminAsync(HttpMethod.Post,
            $"/api/v1/admin/service-clients/{client.GetProperty("client").GetProperty("id").GetGuid():D}/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using var denied = await SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", Callback(task), replica: 1);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await h.EditAsync(async db =>
        {
            Assert.Equal(RequestTaskStatus.InProgress, (await db.RequestTasks.SingleAsync(x => x.Id == task.Id)).Status);
            Assert.Equal(0, await db.WorkLogs.CountAsync());
        });
    }

    private static async Task<RequestTask> SeedTaskAsync(IncidentReceiverTests.Harness h)
    {
        var request = new Request { Id = Guid.NewGuid().ToString("D"), Title = "Approved automation request", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId };
        var task = new RequestTask
        {
            Id = Guid.NewGuid().ToString("N"), RequestId = request.Id, Title = "Recorded automation task",
            OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.InProgress, State = TicketState.InProgress,
            OrchestrationExternalRequestId = ExternalRequest, OrchestratorExecutionId = Execution
        };
        await h.EditAsync(db => { db.Requests.Add(request); db.RequestTasks.Add(task); return Task.CompletedTask; });
        return task;
    }

    private static OrchestrationCallbackDto Callback(RequestTask task) => new()
    { RequestTaskId = task.Id, RequestId = ExternalRequest, ExecutionId = Execution, Status = "succeeded" };

    private static async Task<JsonElement> CreateClientAsync(IncidentReceiverTests.Harness h, RequestTask task)
    {
        var constraints = new ServiceLinkResourceConstraints
        { OrganizationId = h.OrganizationId, CustomerIds = [h.CustomerId], RequestIds = [task.RequestId], TaskIds = [task.Id] };
        using var response = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-clients", new ServiceClientCreateRequest(
            "Scoped callback", h.OrganizationId, "netratel-instance", "tenant-17", [ServiceIdentityScopes.Callback], [h.CustomerId],
            ResourceConstraintsJson: JsonSerializer.Serialize(constraints)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> TokenAsync(IncidentReceiverTests.Harness h, JsonElement client)
    {
        using var response = await h.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = client.GetProperty("client").GetProperty("clientId").GetString()!,
            ["client_secret"] = client.GetProperty("clientSecret").GetString()!, ["scope"] = ServiceIdentityScopes.Callback
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(IncidentReceiverTests.Harness h, string token, HttpMethod method,
        string path, object? body = null, int replica = 0)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await h.ReplicaClient(replica).SendAsync(request);
    }
}
