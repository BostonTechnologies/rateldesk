using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using ListHarness = Helpdesk.Tests.Api.AssigneeSearchEndpointsTests.Harness;
using TaskHarness = Helpdesk.Tests.Api.IncidentReceiverTests.Harness;

namespace Helpdesk.Tests.Mcp;

[Collection("Incident receiver")]
public sealed class TicketAdapterHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ticket_aliases_filter_actual_HTTP_results_counts_and_multiple_pages_under_reader_authority(bool postgres)
    {
        await using var h = await ListHarness.Create(postgres);
        await h.WithDb(async db =>
        {
            foreach (var family in new[] { "incidents", "requests", "changes" })
            for (var index = 0; index < 6; index++)
            {
                Ticket ticket = family switch { "incidents" => new Incident(), "requests" => new Helpdesk.Shared.Models.Request(), _ => new Change() };
                ticket.Id = $"search-{family}-{index}";
                ticket.Title = index == 4 ? "campaign-other-beta" : "campaign-match-alpha";
                ticket.Description = "synthetic adapter fixture";
                ticket.TrackingId = $"SEARCH-{family}-{index}";
                ticket.OrganizationId = index == 5 ? "other" : "org";
                ticket.State = TicketState.New;
                ticket.CreatedAt = new DateTime(2026, 10, 1, index, 0, 0, DateTimeKind.Utc);
                db.Add(ticket);
            }
            await db.SaveChangesAsync();
        });
        // TestAuth resolves the reader's real organization/permission claims in the endpoint.
        var tools = Tools(h.Client, "reader");
        foreach (var family in new[] { "incidents", "requests", "changes" })
        foreach (var alias in new[] { "query", "q" })
        {
            var first = await List(tools, family, new JsonObject { [alias] = "campaign-match-alpha", ["customerOrgId"] = new JsonArray("org", "other"), ["page"] = 1, ["pageSize"] = 2, ["includeTotal"] = true, ["summaryOnly"] = true });
            Assert.True(first.Success, first.Summary);
            Assert.Equal(4, first.Data!["totalCount"]!.GetValue<int>());
            var firstIds = Ids(first);
            Assert.Equal(2, firstIds.Length);
            Assert.All(firstIds, id => Assert.StartsWith("search-" + family + "-", id));
            var second = await List(tools, family, new JsonObject { [alias] = "campaign-match-alpha", ["customerOrgId"] = "org", ["page"] = 2, ["pageSize"] = 2, ["summaryOnly"] = true });
            Assert.True(second.Success, second.Summary);
            Assert.Equal(4, second.Data!["totalCount"]!.GetValue<int>());
            Assert.Equal(2, Ids(second).Length);
            Assert.Empty(firstIds.Intersect(Ids(second)));
            Assert.Equal(Enumerable.Range(0, 4).Select(index => $"search-{family}-{index}").Order(), firstIds.Concat(Ids(second)).Order());
            var repeated = await List(tools, family, new JsonObject { [alias] = "campaign-match-alpha", ["customerOrgId"] = "org", ["page"] = 1, ["pageSize"] = 2, ["summaryOnly"] = true });
            Assert.Equal(firstIds, Ids(repeated));
            Assert.DoesNotContain("search-" + family + "-5", firstIds.Concat(Ids(second)));
            var impossible = await List(tools, family, new JsonObject { [alias] = "campaign-impossible-search", ["page"] = 1, ["summaryOnly"] = true });
            Assert.True(impossible.Success, impossible.Summary);
            Assert.Equal(0, impossible.Data!["totalCount"]!.GetValue<int>());
            Assert.Empty(Ids(impossible));
            var other = await List(tools, family, new JsonObject { [alias] = "campaign-other-beta", ["page"] = 1, ["summaryOnly"] = true });
            Assert.Equal("search-" + family + "-4", Assert.Single(Ids(other)));
            var forbiddenScope = await List(tools, family, new JsonObject { [alias] = "campaign-match-alpha", ["customerOrgId"] = "other", ["page"] = 1, ["summaryOnly"] = true });
            Assert.Equal(0, forbiddenScope.Data!["totalCount"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Priority_only_request_edit_preserves_omitted_state_and_state_only_edit_preserves_priority(bool postgres)
    {
        await using var h = await ListHarness.Create(postgres);
        await h.WithDb(async db =>
        {
            var request = await db.Requests.SingleAsync(request => request.Id == "request");
            request.State = TicketState.OnHold;
            request.Priority = TicketPriority.Medium;
            await db.SaveChangesAsync();
        });
        var tools = Tools(h.Client, "manager");
        var priority = await tools.helpdesk_requests("update", JsonSerializer.SerializeToElement(new { requestId = "request", priority = "High" }), true);
        Assert.True(priority.Success, priority.Summary);
        await h.WithDb(async db => { var fresh = await db.Requests.AsNoTracking().SingleAsync(request => request.Id == "request"); Assert.Equal(TicketState.OnHold, fresh.State); Assert.Equal(TicketPriority.High, fresh.Priority); });
        var state = await tools.helpdesk_requests("update", JsonSerializer.SerializeToElement(new { requestId = "request", state = "InProgress" }), true);
        Assert.True(state.Success, state.Summary);
        await h.WithDb(async db => { var fresh = await db.Requests.AsNoTracking().SingleAsync(request => request.Id == "request"); Assert.Equal(TicketState.InProgress, fresh.State); Assert.Equal(TicketPriority.High, fresh.Priority); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task_aliases_and_identifier_filters_apply_before_actual_HTTP_count_and_paging(bool postgres)
    {
        await using var h = await TaskHarness.CreateAsync(postgres);
        await h.EditAsync(db =>
        {
            db.Requests.Add(new Helpdesk.Shared.Models.Request { Id = "search-parent", Title = "parent", TrackingId = "SEARCH-PARENT", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId });
            for (var index = 0; index < 6; index++)
                db.RequestTasks.Add(new RequestTask { Id = $"search-task-{index}", Title = index == 4 ? "campaign-other-beta" : "campaign-match-alpha", TrackingId = $"TASK-{index}", OrganizationId = h.OrganizationId, RequestId = "search-parent", CustomerId = h.CustomerId, CreatedAt = new DateTime(2026, 10, 1, index, 0, 0, DateTimeKind.Utc), AssignedToId = "support-user", ServiceId = "search-service", Status = index == 5 ? RequestTaskStatus.Completed : RequestTaskStatus.Pending });
            return Task.CompletedTask;
        });
        var tools = Tools(h.Client, admin: true);
        foreach (var alias in new[] { "query", "q" })
        {
            var first = await tools.helpdesk_request_tasks("list", Request(new JsonObject { [alias] = "campaign-match-alpha", ["requestId"] = "search-parent", ["assignedToId"] = "support-user", ["serviceId"] = "search-service", ["status"] = "Pending", ["page"] = 1, ["pageSize"] = 2 }));
            Assert.True(first.Success, first.Summary);
            Assert.Equal(4, first.Data!["totalCount"]!.GetValue<int>());
            Assert.Equal(2, Ids(first).Length);
            var second = await tools.helpdesk_request_tasks("list", Request(new JsonObject { [alias] = "campaign-match-alpha", ["requestId"] = "search-parent", ["page"] = 2, ["pageSize"] = 2 }));
            Assert.Equal(4, second.Data!["totalCount"]!.GetValue<int>());
            Assert.Equal(2, Ids(second).Length);
            Assert.Empty(Ids(first).Intersect(Ids(second)));
            Assert.Equal(Enumerable.Range(0, 4).Select(index => $"search-task-{index}").Order(), Ids(first).Concat(Ids(second)).Order());
            var repeated = await tools.helpdesk_request_tasks("list", Request(new JsonObject { [alias] = "campaign-match-alpha", ["requestId"] = "search-parent", ["assignedToId"] = "support-user", ["serviceId"] = "search-service", ["status"] = "Pending", ["page"] = 1, ["pageSize"] = 2 }));
            Assert.Equal(Ids(first), Ids(repeated));
            foreach (var filter in new[] { "requestId", "assignedToId", "serviceId" })
            {
                var impossible = await tools.helpdesk_request_tasks("list", Request(new JsonObject { [alias] = "campaign-match-alpha", [filter] = "impossible", ["page"] = 1 }));
                Assert.Equal(0, impossible.Data!["totalCount"]!.GetValue<int>());
                Assert.Empty(Ids(impossible));
            }
            var noMatch = await tools.helpdesk_request_tasks("list", Request(new JsonObject { [alias] = "campaign-impossible-search", ["page"] = 1 }));
            Assert.Equal(0, noMatch.Data!["totalCount"]!.GetValue<int>());
            Assert.Empty(Ids(noMatch));
            var other = await tools.helpdesk_request_tasks("list", Request(new JsonObject { [alias] = "campaign-other-beta", ["page"] = 1 }));
            Assert.Equal("search-task-4", Assert.Single(Ids(other)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MCP_task_create_update_priorities_persist_with_independent_API_readback_and_atomic_invalid_edits(bool postgres)
    {
        await using var h = await TaskHarness.CreateAsync(postgres);
        await h.EditAsync(db =>
        {
            db.Requests.Add(new Helpdesk.Shared.Models.Request { Id = "priority-parent", Title = "priority parent", TrackingId = "PRIORITY-PARENT", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId });
            return Task.CompletedTask;
        });
        // This explicit administrator API fixture proves task updates under legitimate
        // authority; it does not represent the ordinary bounded HTTP integration identity.
        var tools = Tools(h.Client, admin: true);
        foreach (var priority in Enum.GetValues<TicketPriority>())
        {
            var create = await tools.helpdesk_request_tasks("create", JsonSerializer.SerializeToElement(new { title = "priority fixture", description = "synthetic details", requestId = "priority-parent", organizationId = h.OrganizationId, priority = priority.ToString() }), true);
            Assert.True(create.Success, create.Summary);
            var taskId = create.Data!["id"]!.GetValue<string>();
            using (var get = await h.AdminAsync(HttpMethod.Get, "/api/v1/request-tasks/" + taskId))
            {
                get.EnsureSuccessStatusCode();
                Assert.Equal((int)priority, (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("priority").GetInt32());
            }
            foreach (var updatedPriority in Enum.GetValues<TicketPriority>())
            {
                var update = await tools.helpdesk_request_tasks("update", JsonSerializer.SerializeToElement(new { taskId, priority = updatedPriority.ToString(), title = "updated fixture", description = "updated details", assignedToId = "support-user", dueDate = "2026-12-01T12:00:00Z" }), true);
                Assert.True(update.Success, update.Summary);
                using var get = await h.AdminAsync(HttpMethod.Get, "/api/v1/request-tasks/" + taskId);
                get.EnsureSuccessStatusCode();
                var fresh = await get.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal((int)updatedPriority, fresh.GetProperty("priority").GetInt32());
                Assert.Equal("updated fixture", fresh.GetProperty("title").GetString());
                Assert.Equal("support-user", fresh.GetProperty("assignedToId").GetString());
            }
            string? before = null;
            await h.EditAsync(async db => before = JsonSerializer.Serialize(await db.RequestTasks.AsNoTracking().SingleAsync(task => task.Id == taskId)));
            var rejected = await tools.helpdesk_request_tasks("update", JsonSerializer.SerializeToElement(new { taskId, title = "must not persist", priority = "999", clearAssignment = true, clearDueDate = true }), true);
            Assert.Equal("validation_failed", rejected.Status);
            var preview = await tools.helpdesk_request_tasks("update", JsonSerializer.SerializeToElement(new { taskId, priority = "Low" }));
            Assert.Equal("confirmation_required", preview.Status);
            await h.EditAsync(async db => Assert.Equal(before, JsonSerializer.Serialize(await db.RequestTasks.AsNoTracking().SingleAsync(task => task.Id == taskId))));
            var cleared = await tools.helpdesk_request_tasks("update", JsonSerializer.SerializeToElement(new { taskId, clearAssignment = true, clearDueDate = true, linkedAssetIds = Array.Empty<string>(), attachments = Array.Empty<string>() }), true);
            Assert.True(cleared.Success, cleared.Summary);
            await h.EditAsync(async db => { var fresh = await db.RequestTasks.AsNoTracking().SingleAsync(task => task.Id == taskId); Assert.Null(fresh.AssignedToId); Assert.Null(fresh.DueDate); Assert.Empty(fresh.LinkedAssetIds); Assert.Empty(fresh.Attachments); });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MCP_task_transitions_serialize_after_commit_with_correlation_and_independent_parent_readbacks(bool postgres)
    {
        await using var h = await TaskHarness.CreateAsync(postgres);
        await h.EditAsync(db =>
        {
            foreach (var suffix in new[] { "complete", "fail" })
            {
                var parent = new Helpdesk.Shared.Models.Request { Id = "mcp-parent-" + suffix, Title = "Synthetic parent", TrackingId = "MCP-PARENT-" + suffix, OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, State = TicketState.InProgress };
                db.Requests.Add(parent);
                db.RequestTasks.Add(new RequestTask { Id = "mcp-task-" + suffix, Request = parent, RequestId = parent.Id, Title = "Synthetic task", TrackingId = "MCP-TASK-" + suffix, OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, Status = suffix == "fail" ? RequestTaskStatus.InProgress : RequestTaskStatus.Pending });
            }
            return Task.CompletedTask;
        });
        var tools = Tools(h.Client, admin: true);
        foreach (var transition in new[] { (Suffix: "complete", Operation: "start", Status: RequestTaskStatus.InProgress, Parent: TicketState.InProgress), (Suffix: "complete", Operation: "complete", Status: RequestTaskStatus.Completed, Parent: TicketState.Resolved), (Suffix: "fail", Operation: "fail", Status: RequestTaskStatus.Failed, Parent: TicketState.OnHold) })
        {
            var response = await tools.helpdesk_request_tasks(transition.Operation, JsonSerializer.SerializeToElement(new { taskId = "mcp-task-" + transition.Suffix }), true);
            Assert.True(response.Success, response.Summary);
            Assert.Equal("completed", response.Status);
            Assert.Equal((int)transition.Status, response.Data!["status"]!.GetValue<int>());
            Assert.Equal("mcp-task-" + transition.Suffix, response.Data!["id"]!.GetValue<string>());
            Assert.False(response.Data!.AsObject().ContainsKey("request"));
            Assert.False(string.IsNullOrWhiteSpace(response.CorrelationId));
            using var read = await h.AdminAsync(HttpMethod.Get, "/api/v1/request-tasks/mcp-task-" + transition.Suffix);
            read.EnsureSuccessStatusCode();
            Assert.Equal((int)transition.Status, (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
            await h.EditAsync(async db =>
            {
                Assert.Equal(transition.Status, (await db.RequestTasks.AsNoTracking().SingleAsync(task => task.Id == "mcp-task-" + transition.Suffix)).Status);
                Assert.Equal(transition.Parent, (await db.Requests.AsNoTracking().SingleAsync(parent => parent.Id == "mcp-parent-" + transition.Suffix)).State);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Four_allowlisted_raw_reads_execute_actual_API_routes_and_override_inputs_reject_before_outbound_IO(bool postgres)
    {
        await using var h = await ListHarness.Create(postgres);
        var outbound = new List<string>();
        var tools = Tools(h.Client, "reader", onRequest: outbound.Add);
        var version = await tools.helpdesk_raw("system_version");
        Assert.True(version.Success, version.Summary);
        Assert.False(string.IsNullOrWhiteSpace(version.Data!["version"]!.GetValue<string>()));
        foreach (var family in new[] { "incident", "request", "change" })
        {
            var request = new JsonObject { [family + "Id"] = family };
            var get = await tools.helpdesk_raw("get_" + family, Request(request));
            Assert.True(get.Success, get.Summary);
            Assert.Equal(family, get.Data!["id"]!.GetValue<string>());
            var countBeforeRejection = outbound.Count;
            foreach (var field in new[] { "method", "path", "host", "url", "body" })
            {
                var attempted = request.DeepClone().AsObject();
                attempted[field] = "https://outside.example.test/override";
                var denied = await tools.helpdesk_raw("get_" + family, Request(attempted));
                Assert.Equal("validation_failed", denied.Status);
                Assert.Contains(field, denied.Summary, StringComparison.Ordinal);
                Assert.Equal(countBeforeRejection, outbound.Count);
            }
        }
        Assert.Equal(new[] { "/api/v1/system/version", "/api/v1/incidents/incident", "/api/v1/requests/request", "/api/v1/changes/change" }, outbound);
        var unknown = await tools.helpdesk_raw("arbitrary", Request(new JsonObject { ["path"] = "/api/v1/system/version" }));
        Assert.Equal("validation_failed", unknown.Status);
        Assert.Equal("unsupported_operation", unknown.Failure!.Code);
        Assert.Equal(4, outbound.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_supported_and_unknown_ticket_updates_reject_atomically_and_preserve_real_provider_state_and_effects(bool postgres)
    {
        await using var h = await ListHarness.Create(postgres);
        var tools = Tools(h.Client, "manager");
        var before = await Snapshot();
        foreach (var family in new[] { "incident", "request", "change" })
        foreach (var confirmed in new[] { false, true })
        {
            var request = Request(new JsonObject { [family + "Id"] = family, ["priority"] = "Critical", ["title"] = "must never persist" });
            var rejected = family switch
            {
                "incident" => await tools.helpdesk_incidents("update", request, confirmed),
                "request" => await tools.helpdesk_requests("update", request, confirmed),
                _ => await tools.helpdesk_changes("update", request, confirmed)
            };
            Assert.Equal("validation_failed", rejected.Status);
            Assert.Contains("request.title", rejected.Summary, StringComparison.Ordinal);
            Assert.Equal(before, await Snapshot());
            using var read = await h.Client.GetAsync($"/api/v1/{family}s/{family}");
            read.EnsureSuccessStatusCode();
            var persisted = await read.Content.ReadFromJsonAsync<JsonElement>();
            Assert.NotEqual("must never persist", persisted.GetProperty(family == "incident" ? "subject" : "title").GetString());
            Assert.Equal((int)TicketPriority.Low, persisted.GetProperty("priority").GetInt32());
        }

        async Task<string> Snapshot()
        {
            string result = string.Empty;
            await h.WithDb(async db => result = JsonSerializer.Serialize(new
            {
                tickets = await db.Tickets.AsNoTracking().OrderBy(ticket => ticket.Id).ToListAsync(),
                timeline = await db.TicketTimelineEvents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                notifications = await db.Notifications.AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                worklogs = await db.WorkLogs.AsNoTracking().OrderBy(row => row.Id).ToListAsync(),
                support = await db.SupportNotificationDeliveries.AsNoTracking().OrderBy(row => row.Id).ToListAsync()
            }));
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Global_search_aliases_filter_actual_administrator_HTTP_results_and_preserve_bounded_counts(bool postgres)
    {
        await using var h = await ListHarness.Create(postgres);
        await h.WithDb(async db =>
        {
            for (var index = 0; index < 28; index++)
            {
                db.Users.Add(new User { Id = "global-users-" + index, Name = "campaign-global-match " + index, Email = $"global-user-{index}@example.test", OrganizationId = "org" });
                db.Customers.Add(new Customer { Id = "global-customers-" + index, Name = "campaign-global-match " + index, Email = $"global-customer-{index}@example.test", OrganizationId = "org" });
                db.Organizations.Add(new Organization { Id = "global-organizations-" + index, Name = "campaign-global-match " + index });
            }
            await db.SaveChangesAsync();
        });
        var tools = Tools(h.Client, "admin");
        foreach (var operation in new[] { "users", "customers", "organizations" })
        {
            using var ignored = await h.Client.GetAsync($"/api/v1/global-search/{operation}?query=campaign-impossible-search&limit=1");
            ignored.EnsureSuccessStatusCode();
            Assert.NotEmpty((await ignored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
            foreach (var alias in new[] { "query", "q" })
            foreach (var bound in new[] { "limit", "pageSize" })
            {
                var result = await tools.helpdesk_search(operation, Request(new JsonObject { [alias] = "campaign-global-match", [bound] = 3 }));
                Assert.True(result.Success, result.Summary);
                Assert.Equal(3, result.Data!["pageSize"]!.GetValue<int>());
                Assert.Equal(3, result.Data!["totalCount"]!.GetValue<int>());
                Assert.Equal(3, Ids(result).Length);
                Assert.All(Ids(result), id => Assert.StartsWith("global-" + operation + "-", id));
                var impossible = await tools.helpdesk_search(operation, Request(new JsonObject { [alias] = "campaign-impossible-search", [bound] = 3 }));
                Assert.Equal(0, impossible.Data!["totalCount"]!.GetValue<int>());
                Assert.Empty(Ids(impossible));
            }
            var capped = await tools.helpdesk_search(operation, Request(new JsonObject { ["query"] = "campaign-global-match", ["limit"] = 50 }));
            Assert.True(capped.Success, capped.Summary);
            Assert.Equal(25, capped.Data!["pageSize"]!.GetValue<int>());
            Assert.Equal(25, Ids(capped).Length);
            var denied = await Tools(h.Client, "reader").helpdesk_search(operation, Request(new JsonObject { ["query"] = "campaign-global-match" }));
            Assert.Equal("forbidden", denied.Status);
        }
    }

    internal static HelpdeskTools Tools(HttpClient http, string? role = null, bool admin = false, Action<string>? onRequest = null)
    {
        var configuration = new AgentClientConfiguration("https://api.example.test", null, null, null, null, null, null) { CredentialMode = "integration", IntegrationCredential = "rdk_synthetic", CredentialTarget = "https://api.example.test" };
        return new HelpdeskTools(new HelpdeskAgentClient(configuration, () => new ApiBridge(http, role, admin, onRequest)), new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"adapter-http-{Guid.NewGuid():N}.json")));
    }
    private sealed class ApiBridge(HttpClient http, string? role, bool admin, Action<string>? onRequest) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onRequest?.Invoke(request.RequestUri!.PathAndQuery);
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery);
            if (request.Content is not null) forwarded.Content = new StringContent(await request.Content.ReadAsStringAsync(ct), System.Text.Encoding.UTF8, "application/json");
            if (admin) forwarded.Headers.Add("X-Synthetic-Admin", "true");
            else forwarded.Headers.Authorization = new AuthenticationHeaderValue("Test", role ?? "HelpdeskAdmin");
            return await http.SendAsync(forwarded, ct);
        }
    }
    private static string[] Ids(HelpdeskToolResponse response) => response.Data!["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray();
    private static JsonElement Request(JsonNode request) => JsonSerializer.SerializeToElement(request);
    private static Task<HelpdeskToolResponse> List(HelpdeskTools tools, string family, JsonObject request) => family switch
    {
        "incidents" => tools.helpdesk_incidents("list", Request(request)),
        "requests" => tools.helpdesk_requests("list", Request(request)),
        _ => tools.helpdesk_changes("list", Request(request))
    };
}
