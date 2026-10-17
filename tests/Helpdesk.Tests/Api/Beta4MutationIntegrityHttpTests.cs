using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Shared.Pairing;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class Beta4MutationIntegrityHttpTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incident_state_writes_reject_undefined_values_atomically_and_defined_states_roundtrip(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres);
        await h.EditAsync(db =>
        {
            foreach (var id in new[] { "incident-one", "incident-two" })
            {
                db.Incidents.Add(new Incident
                {
                    Id = id, Title = "Synthetic incident", TrackingId = id.ToUpperInvariant(),
                    OrganizationId = h.OrganizationId, CustomerId = h.CustomerId,
                    State = id == "incident-two" ? TicketState.Resolved : TicketState.OnHold, Priority = TicketPriority.Medium,
                    ClosedAt = id == "incident-two" ? new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero) : null,
                    UpdatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                    RequesterEmail = "customer@example.test", AssignedToId = "support-user"
                });
                db.TicketSlaStates.Add(new TicketSlaState
                {
                    TicketId = id, StartedAt = DateTimeOffset.UtcNow,
                    ResponseDueAt = DateTimeOffset.UtcNow.AddHours(1), ResolutionDueAt = DateTimeOffset.UtcNow.AddHours(4)
                });
            }
            return Task.CompletedTask;
        });
        var before = await SnapshotAsync(h);
        foreach (var value in new[] { "999", "-1", "\"undefined\"", "\"999\"", "\"Resolved\"", "null", "{}" })
        {
            using var quick = await RawAsync(h, HttpMethod.Post, "/api/v1/incidents/incident-one/state", $"{{\"newState\":{value}}}");
            Assert.Equal(HttpStatusCode.BadRequest, quick.StatusCode);
            Assert.Equal(before, await SnapshotAsync(h));
            if (value == "null") continue; // Nullable PUT state means omitted.
            // Valid metadata alongside an invalid state must not partially edit the incident.
            using var update = await RawAsync(h, HttpMethod.Put, "/api/v1/incidents/incident-two",
                $"{{\"state\":{value},\"priority\":3,\"assignedToId\":\"\",\"ccRecipients\":[\"other@example.test\"]}}");
            Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
            Assert.Equal(before, await SnapshotAsync(h));
        }
        foreach (var value in new[] { 999, -1 })
        {
            using var bulk = await h.AdminAsync(HttpMethod.Post, "/api/v1/incidents/bulk/state", new { ids = new[] { "incident-one", "incident-two" }, newState = value });
            Assert.Equal(HttpStatusCode.BadRequest, bulk.StatusCode);
            Assert.Equal(before, await SnapshotAsync(h));
        }

        foreach (var state in Enum.GetValues<TicketState>())
        {
            using var quick = await h.AdminAsync(HttpMethod.Post, "/api/v1/incidents/incident-one/state", new { newState = state });
            Assert.Equal(HttpStatusCode.OK, quick.StatusCode);
            await AssertIncidentStateAsync(h, "incident-one", state);
            var committed = await SnapshotAsync(h);
            using var repeated = await h.AdminAsync(HttpMethod.Post, "/api/v1/incidents/incident-one/state", new { newState = state });
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(committed, await SnapshotAsync(h));
            using var update = await h.AdminAsync(HttpMethod.Put, "/api/v1/incidents/incident-two", new { state });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            await AssertIncidentStateAsync(h, "incident-two", state);
        }

        // A correctly scoped credential cannot cross the organization boundary, even with a valid state.
        await h.EditAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "foreign-organization", Name = "Other organization" });
            db.Incidents.Add(new Incident { Id = "foreign-incident", Title = "Foreign incident", OrganizationId = "foreign-organization" });
            return Task.CompletedTask;
        });
        before = await SnapshotAsync(h);
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/foreign-incident/state")
            { Content = JsonContent.Create(new { newState = TicketState.Resolved }) };
        h.Authorize(deniedRequest);
        using var denied = await h.Client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(before, await SnapshotAsync(h));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_task_priority_create_patch_and_put_validate_before_any_metadata_changes(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres);
        await SeedTaskAsync(h, "priority", RequestTaskStatus.Pending);
        foreach (var priority in Enum.GetValues<TicketPriority>())
        {
            using var create = await h.AdminAsync(HttpMethod.Post, "/api/v1/request-tasks/", new
            {
                title = $"Priority {priority}", description = "Synthetic task", requestId = "request-priority", priority
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<RequestTask>();
            await AssertTaskPriorityAsync(h, created!.Id, priority);
            using var patch = await h.AdminAsync(HttpMethod.Patch, "/api/v1/request-tasks/task-priority", new { priority });
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
            await AssertTaskPriorityAsync(h, "task-priority", priority);
        }
        var before = await SnapshotAsync(h);
        foreach (var value in new[] { "999", "-1", "\"999\"", "\"High\"", "{}", "true" })
        {
            using var patch = await RawAsync(h, HttpMethod.Patch, "/api/v1/request-tasks/task-priority",
                $"{{\"title\":\"must not persist\",\"clearAssignment\":true,\"clearDueDate\":true,\"priority\":{value}}}");
            Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
            Assert.Equal(before, await SnapshotAsync(h));
            using var create = await RawAsync(h, HttpMethod.Post, "/api/v1/request-tasks/",
                $"{{\"title\":\"must not exist\",\"description\":\"Synthetic\",\"requestId\":\"request-priority\",\"priority\":{value}}}");
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
            Assert.Equal(before, await SnapshotAsync(h));
        }
        using var invalidPut = await h.AdminAsync(HttpMethod.Put, "/api/v1/request-tasks/task-priority", new RequestTask
        {
            Title = "must not persist", RequestId = "request-priority", OrganizationId = h.OrganizationId,
            CustomerId = h.CustomerId, Priority = (TicketPriority)999
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPut.StatusCode);
        Assert.Equal(before, await SnapshotAsync(h));
        // API optional null priority leaves the current priority untouched while applying valid metadata.
        using var metadata = await h.AdminAsync(HttpMethod.Patch, "/api/v1/request-tasks/task-priority", new
        {
            priority = (int?)null, title = "Updated title", clearAssignment = true, clearDueDate = true,
            linkedAssetIds = Array.Empty<string>(), attachments = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        await using var scope = h.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var task = await db.RequestTasks.AsNoTracking().SingleAsync(x => x.Id == "task-priority");
        Assert.Equal(TicketPriority.Critical, task.Priority);
        Assert.Equal("Updated title", task.Title); Assert.Null(task.AssignedToId); Assert.Null(task.DueDate);
        Assert.Empty(task.LinkedAssetIds); Assert.Empty(task.Attachments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_serializes_real_parent_graph_and_repeated_or_lost_responses_do_not_duplicate_effects(bool postgres)
    {
        var fault = new TransitionFault();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, configureServices: services =>
            services.AddDbContext<HelpdeskDbContext>(options => options.AddInterceptors(fault)));
        await SeedTaskAsync(h, "complete", RequestTaskStatus.Pending);
        await SeedTaskAsync(h, "fail", RequestTaskStatus.InProgress);
        await SeedTaskAsync(h, "lost", RequestTaskStatus.Pending);
        await SeedTaskAsync(h, "before", RequestTaskStatus.Pending);

        foreach (var transition in new[]
        {
            (Id: "complete", Action: "start", Status: RequestTaskStatus.InProgress, Parent: TicketState.InProgress),
            (Id: "complete", Action: "complete", Status: RequestTaskStatus.Completed, Parent: TicketState.Resolved),
            (Id: "fail", Action: "fail", Status: RequestTaskStatus.Failed, Parent: TicketState.OnHold)
        })
        {
            var correlation = $"synthetic-{transition.Action}-{postgres}";
            using var request = TransitionRequest(transition.Id, transition.Action, correlation);
            using var response = await h.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            Assert.False(document.RootElement.TryGetProperty("request", out _));
            var result = JsonSerializer.Deserialize<RequestTaskTransitionDto>(json, Json)!;
            Assert.Equal($"task-{transition.Id}", result.Id); Assert.Equal($"request-{transition.Id}", result.RequestId);
            Assert.Equal(transition.Status, result.Status); Assert.Equal(correlation, result.CorrelationId);
            Assert.Equal(correlation, Assert.Single(response.Headers.GetValues(CorrelationConstants.HeaderName)));
            Assert.Equal("Synthetic task", result.Title); Assert.Equal(result.Title, result.Name);
            Assert.Equal("support-user", result.AssignedToId); Assert.Equal(TicketPriority.Medium, result.Priority);
            await AssertTransitionAsync(h, transition.Id, transition.Status, transition.Parent);
            var committed = await SnapshotAsync(h);
            using var repeated = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/task-{transition.Id}/{transition.Action}");
            Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
            Assert.Equal(committed, await SnapshotAsync(h));
        }
        var beforeCommit = await SnapshotAsync(h);
        fault.Enabled = true;
        using (var request = TransitionRequest("before", "start", "synthetic-before-commit"))
            await Assert.ThrowsAsync<IOException>(() => h.Client.SendAsync(request));
        fault.Enabled = false;
        Assert.Equal(beforeCommit, await SnapshotAsync(h));

        using (var request = TransitionRequest("lost", "start", "synthetic-lost-response"))
        {
            request.Headers.Add("X-Synthetic-Lost-Response", "true");
            var lost = await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.SendAsync(request));
            Assert.IsType<IOException>(lost.InnerException);
        }
        await AssertTransitionAsync(h, "lost", RequestTaskStatus.InProgress, TicketState.InProgress);
        var afterLostResponse = await SnapshotAsync(h);
        using var replay = await h.AdminAsync(HttpMethod.Post, "/api/v1/request-tasks/task-lost/start");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(afterLostResponse, await SnapshotAsync(h));
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var notifications = await db.Notifications.AsNoTracking().ToListAsync();
            Assert.Single(notifications, x => x.Title == "DomainEvent.RequestTask.Started" && x.CorrelationId == "synthetic-lost-response");
            Assert.Single(notifications, x => x.Title == "DomainEvent.RequestTask.Completed");
            Assert.Single(notifications, x => x.Title == "DomainEvent.RequestTask.Failed");
            var worklog = Assert.Single(await db.WorkLogs.AsNoTracking().ToListAsync());
            Assert.Equal("request-fail", worklog.TicketId);
            Assert.Equal("system", worklog.TechnicianId);
            Assert.Equal(0, worklog.Hours);
            Assert.Contains("Workflow: Blocked", worklog.NotesText);
        }
        var deniedBefore = await SnapshotAsync(h);
        using (var deniedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/request-tasks/task-before/start"))
        {
            h.Authorize(deniedRequest);
            using var denied = await h.Client.SendAsync(deniedRequest);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.Equal(deniedBefore, await SnapshotAsync(h));
        var manualBefore = await SnapshotAsync(h);
        using var manualRetry = await h.AdminAsync(HttpMethod.Post, "/api/v1/request-tasks/task-fail/retry");
        Assert.Equal(HttpStatusCode.BadRequest, manualRetry.StatusCode);
        Assert.Equal(manualBefore, await SnapshotAsync(h));
        using var noOp = await h.AdminAsync(HttpMethod.Post, "/api/v1/request-tasks/bulk/start", new { ids = new[] { "task-complete", "task-lost" } });
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.Equal(0, (await noOp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetInt32());
        Assert.Equal(manualBefore, await SnapshotAsync(h));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eligible_automation_retry_uses_saved_peer_tenant_and_binding_once(bool postgres)
    {
        using var peer = new PairingTestPeer(Guid.NewGuid()) { AutomationBusinessEnabled = true };
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true, pairingPeer: peer);
        PairingMapping mapping;
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var pairing = peer.Service(scope.ServiceProvider, TimeProvider.System);
            var code = await pairing.GenerateAsync("owner", default);
            var paired = await pairing.ExchangeAsync(peer.Exchange(code.Code), default);
            mapping = new(Guid.NewGuid().ToString("D"), paired.PairId, "Synthetic automation", "17", h.OrganizationId, null, false, true);
            Assert.Equal("Connected", (await pairing.SaveAsync(paired.PairId, mapping, "owner", default)).Status);
        }
        var template = Guid.NewGuid();
        await h.EditAsync(async db =>
        {
            var organization = await db.Organizations.SingleAsync(x => x.Id == h.OrganizationId);
            organization.OrchestrationTenantId = 17;
            db.RequestForms.Add(new RequestForm
            {
                Id = "retry-form", Title = "Synthetic retry form", OrganizationId = h.OrganizationId,
                JsonSchema = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    fields = Array.Empty<object>(),
                    tasks = new[] { new { id = template, name = "Synthetic automation", type = "automation", order = 1, autoStart = true } }
                }))
            });
            db.AutomationBindings.Add(new AutomationBinding
            {
                Id = "retry-binding", OrganizationId = h.OrganizationId, RequestFormId = "retry-form", TaskTemplateId = template,
                SystemConnectionId = Guid.Parse(mapping.Id), OrchestrationRequestDefinitionId = "synthetic-definition",
                OrchestrationJobDefinitionId = "synthetic-job", SyncState = AutomationBindingSyncState.InSync, Enabled = true
            });
            db.Requests.Add(new Request
            {
                Id = "retry-parent", Title = "Synthetic retry request", RequestFormId = "retry-form", PayloadJson = "{}",
                OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, State = TicketState.OnHold
            });
            db.RequestTasks.Add(new RequestTask
            {
                Id = "retry-task", Title = "Synthetic automation", RequestId = "retry-parent", OrganizationId = h.OrganizationId,
                CustomerId = h.CustomerId, TemplateId = template.ToString("D"), Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Failed, State = TicketState.OnHold, FailureReason = "Synthetic previous failure",
                LastAutomationStatus = AutomationTaskStatuses.SubmitFailedManualRetry
            });
        });
        using var retry = await h.AdminAsync(HttpMethod.Post, "/api/v1/request-tasks/retry-task/retry");
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Single(retry.Headers.GetValues(CorrelationConstants.HeaderName));
        Assert.Equal(1, peer.AutomationIngestCalls);
        Assert.Equal(1, peer.AutomationTokenCalls);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var task = await db.RequestTasks.AsNoTracking().SingleAsync(x => x.Id == "retry-task");
            Assert.Equal(RequestTaskStatus.InProgress, task.Status);
            Assert.Equal("synthetic-execution", task.OrchestratorExecutionId);
            Assert.Equal("synthetic-remote-request", task.OrchestrationExternalRequestId);
            Assert.Equal("retry-binding", task.AutomationBindingId);
            Assert.Equal(mapping.Id, task.OrchestrationLinkId);
            Assert.Equal(TicketState.InProgress, (await db.Requests.AsNoTracking().SingleAsync(x => x.Id == "retry-parent")).State);
            var notifications = await db.Notifications.AsNoTracking().ToListAsync();
            Assert.Single(notifications, x => x.Title == RequestTaskDomainEventTypes.RetryRequested);
            Assert.Single(notifications, x => x.Title == RequestTaskDomainEventTypes.AutomationSubmitted);
        }
        var committed = await SnapshotAsync(h);
        using var repeated = await h.AdminAsync(HttpMethod.Post, "/api/v1/request-tasks/retry-task/retry");
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(committed, await SnapshotAsync(h));
        Assert.Equal(1, peer.AutomationIngestCalls);
    }

    private static HttpRequestMessage TransitionRequest(string id, string action, string correlation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/request-tasks/task-{id}/{action}");
        request.Headers.Add("X-Synthetic-Admin", "true");
        request.Headers.Add(CorrelationConstants.HeaderName, correlation);
        return request;
    }

    private static Task SeedTaskAsync(IncidentReceiverTests.Harness h, string suffix, RequestTaskStatus status) => h.EditAsync(db =>
    {
        db.Requests.Add(new Request
        {
            Id = $"request-{suffix}", Title = "Synthetic parent", OrganizationId = h.OrganizationId,
            CustomerId = h.CustomerId, State = status == RequestTaskStatus.InProgress ? TicketState.InProgress : TicketState.New
        });
        db.RequestTasks.Add(new RequestTask
        {
            Id = $"task-{suffix}", Title = "Synthetic task", Description = "Task description", RequestId = $"request-{suffix}",
            OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, Type = RequestTaskType.Manual,
            Status = status, State = status == RequestTaskStatus.InProgress ? TicketState.InProgress : TicketState.New,
            StartedAt = status == RequestTaskStatus.InProgress ? DateTimeOffset.UtcNow : null,
            Priority = TicketPriority.Medium, AssignedToId = "support-user", DueDate = DateTime.UtcNow.AddDays(1),
            TaskSlaMinutes = 30, Attachments = ["synthetic-attachment"], LinkedAssetIds = ["synthetic-asset"]
        });
        return Task.CompletedTask;
    });

    private static async Task<HttpResponseMessage> RawAsync(IncidentReceiverTests.Harness h, HttpMethod method, string path, string json)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Synthetic-Admin", "true");
        return await h.Client.SendAsync(request);
    }

    private static async Task AssertTaskPriorityAsync(IncidentReceiverTests.Harness h, string id, TicketPriority priority)
    {
        using var read = await h.AdminAsync(HttpMethod.Get, $"/api/v1/request-tasks/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(priority, (await read.Content.ReadFromJsonAsync<RequestTask>())!.Priority);
        await using var scope = h.App.Services.CreateAsyncScope();
        Assert.Equal(priority, (await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().RequestTasks.AsNoTracking().SingleAsync(x => x.Id == id)).Priority);
    }

    private static async Task AssertIncidentStateAsync(IncidentReceiverTests.Harness h, string id, TicketState state)
    {
        await using var scope = h.App.Services.CreateAsyncScope();
        var incident = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Incidents.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(state, incident.State);
        Assert.Equal(state == TicketState.Resolved, incident.ClosedAt.HasValue);
    }

    private static async Task AssertTransitionAsync(IncidentReceiverTests.Harness h, string suffix, RequestTaskStatus status, TicketState parentState)
    {
        using var read = await h.AdminAsync(HttpMethod.Get, $"/api/v1/request-tasks/task-{suffix}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(status, (await read.Content.ReadFromJsonAsync<RequestTask>())!.Status);
        await using var scope = h.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var task = await db.RequestTasks.AsNoTracking().SingleAsync(x => x.Id == $"task-{suffix}");
        var request = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == $"request-{suffix}");
        Assert.Equal(status, task.Status); Assert.Equal(parentState, request.State);
        Assert.Equal(parentState == TicketState.Resolved, request.ClosedAt.HasValue);
        Assert.Equal(status == RequestTaskStatus.Completed, task.CompletedAt.HasValue);
        if (suffix != "fail")
        {
            Assert.NotNull(task.StartedAt); Assert.NotNull(task.SlaStartedAt);
            Assert.Equal(TimeSpan.FromMinutes(30), task.DueAt - task.StartedAt);
        }
        Assert.Equal(parentState switch
        {
            TicketState.InProgress => "InProgress",
            TicketState.Resolved => "Completed",
            TicketState.OnHold => "Blocked",
            _ => null
        }, request.WorkflowStatus);
    }

    private static async Task<string> SnapshotAsync(IncidentReceiverTests.Harness h)
    {
        await using var scope = h.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        return JsonSerializer.Serialize(new
        {
            incidents = await db.Incidents.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            tasks = await db.RequestTasks.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            requests = await db.Requests.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            sla = await db.TicketSlaStates.AsNoTracking().OrderBy(x => x.TicketId).ToListAsync(),
            escalations = await db.TicketSlaEscalationEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            timeline = await db.TicketTimelineEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            activity = await db.ActivityLogs.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            notifications = await db.Notifications.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            mailbox = await db.Set<MailboxOutboxEffect>().AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            support = await db.SupportNotificationDeliveries.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            worklogs = await db.WorkLogs.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        }, Json);
    }

    private sealed class TransitionFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<RequestTask>().Any(x => x.State == Microsoft.EntityFrameworkCore.EntityState.Modified))
                throw new IOException("Synthetic failure before task commit.");
            return ValueTask.FromResult(result);
        }
    }
}
