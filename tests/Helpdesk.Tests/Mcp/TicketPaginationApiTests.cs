using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ListHarness = Helpdesk.Tests.Api.AssigneeSearchEndpointsTests.Harness;
using TaskHarness = Helpdesk.Tests.Api.IncidentReceiverTests.Harness;

namespace Helpdesk.Tests.Mcp;

[Collection("Incident receiver")]
public sealed class TicketPaginationApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task_paging_with_tied_timestamps_is_stable_and_respects_actual_bounded_identity_and_foreign_filters(bool postgres)
    {
        await using var h = await TaskHarness.CreateAsync(postgres);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            (await identity.IntegrationCredentials.SingleAsync(value => value.Id == h.CredentialId)).Permissions = "Request.Read";
            await identity.SaveChangesAsync();
        }
        await h.EditAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "pagination-foreign-org", Name = "Foreign organization" });
            db.Customers.Add(new Customer { Id = "pagination-foreign-customer", Name = "Foreign customer", OrganizationId = "pagination-foreign-org" });
            db.Requests.AddRange(
                new Helpdesk.Shared.Models.Request { Id = "pagination-scope-parent", Title = "matching parent", TrackingId = "SCOPE-PARENT", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId },
                new Helpdesk.Shared.Models.Request { Id = "pagination-foreign-parent", Title = "matching parent", TrackingId = "FOREIGN-PARENT", OrganizationId = "pagination-foreign-org", CustomerId = "pagination-foreign-customer" });
            for (var index = 0; index < 6; index++)
                db.RequestTasks.Add(new RequestTask { Id = $"scoped-task-{index}", Title = "München R&D + 100%_", TrackingId = $"SCOPE-TASK-{index}", OrganizationId = index == 5 ? "pagination-foreign-org" : h.OrganizationId, RequestId = index == 5 ? "pagination-foreign-parent" : "pagination-scope-parent", CustomerId = index == 5 ? "pagination-foreign-customer" : h.CustomerId, CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), AssignedToId = "support-user" });
            return Task.CompletedTask;
        });
        var configuration = new AgentClientConfiguration("https://api.example.test", null, null, null, null, null, null)
        {
            CredentialMode = "integration", IntegrationCredential = h.Token, CredentialTarget = "https://api.example.test"
        };
        var tools = new HelpdeskTools(new HelpdeskAgentClient(configuration, () => new TokenApiBridge(h.Client)),
            new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"scoped-pagination-{Guid.NewGuid():N}.json")));
        foreach (var alias in new[] { "query", "q" })
        {
            var all = new List<string>();
            for (var page = 1; page <= 3; page++)
            {
                var response = await tools.helpdesk_request_tasks("list", JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    [alias] = "München R&D + 100%_", ["page"] = page, ["pageSize"] = 2, ["assignedToId"] = "support-user"
                }));
                Assert.True(response.Success, response.Summary);
                Assert.Equal(5, response.Data!["totalCount"]!.GetValue<int>());
                all.AddRange(response.Data!["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()));
            }
            Assert.Equal(Enumerable.Range(0, 5).Select(index => $"scoped-task-{index}"), all);
            foreach (var requestId in new[] { "pagination-foreign-parent", "nonexistent-parent" })
            {
                var foreign = await tools.helpdesk_request_tasks("list", JsonSerializer.SerializeToElement(new { query = "München R&D + 100%_", requestId, page = 1 }));
                Assert.True(foreign.Success, foreign.Summary);
                Assert.Equal(0, foreign.Data!["totalCount"]!.GetValue<int>());
                Assert.Empty(foreign.Data!["items"]!.AsArray());
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ticket_REST_normalization_and_MCP_omission_and_beyond_last_pages_preserve_filtered_population(bool postgres)
    {
        await using var h = await ListHarness.Create(postgres);
        await h.WithDb(async db =>
        {
            foreach (var family in new[] { "incidents", "requests", "changes" })
            for (var index = 0; index < 6; index++)
            {
                Ticket ticket = family switch { "incidents" => new Incident(), "requests" => new Helpdesk.Shared.Models.Request(), _ => new Change() };
                ticket.Id = $"pagination-{family}-{index}";
                ticket.Title = "pagination matching fixture";
                ticket.TrackingId = $"PAGE-{family}-{index}";
                ticket.OrganizationId = index == 5 ? "other" : "org";
                ticket.CreatedAt = new DateTime(2026, 10, 1, index, 0, 0, DateTimeKind.Utc);
                db.Add(ticket);
            }
            await db.SaveChangesAsync();
        });
        h.Client.DefaultRequestHeaders.Authorization = new("Test", "reader");
        var tools = TicketAdapterHttpTests.Tools(h.Client, "reader");
        foreach (var family in new[] { "incidents", "requests", "changes" })
        {
            // The REST contract still clamps explicitly invalid pagination.
            foreach (var parameters in new[] { "page=0&pageSize=10000", "page=-1&pageSize=101" })
            {
                var direct = await h.Client.GetFromJsonAsync<JsonElement>($"/api/v1/{family}/?q=pagination&summaryOnly=true&{parameters}");
                Assert.Equal(1, direct.GetProperty("page").GetInt32());
                Assert.Equal(100, direct.GetProperty("pageSize").GetInt32());
                Assert.Equal(5, direct.GetProperty("totalCount").GetInt32());
                Assert.Equal(5, direct.GetProperty("items").GetArrayLength());
            }
            var omitted = await List("""{"q":"pagination","pageSize":1,"summaryOnly":true}""");
            Assert.True(omitted.Success, omitted.Summary);
            Assert.Equal(5, omitted.Data!["totalCount"]!.GetValue<int>());
            Assert.Equal(family == "incidents" ? 1 : 5, omitted.Data!["items"]!.AsArray().Count);
            using var directOmitted = await h.Client.GetAsync($"/api/v1/{family}/?q=pagination&pageSize=1&summaryOnly=true");
            directOmitted.EnsureSuccessStatusCode();
            Assert.Equal(family == "incidents" ? 1 : 5, (await directOmitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());

            var integral = await List("""{"query":"pagination","page":1e0,"pageSize":1.0,"summaryOnly":true}""");
            Assert.True(integral.Success, integral.Summary);
            Assert.Single(integral.Data!["items"]!.AsArray());
            foreach (var page in new[] { 6, 1_000_000 })
            {
                var beyond = await List("{\"query\":\"pagination\",\"page\":" + page + ",\"pageSize\":100,\"summaryOnly\":true}");
                Assert.True(beyond.Success, beyond.Summary);
                Assert.Equal(5, beyond.Data!["totalCount"]!.GetValue<int>());
                Assert.Empty(beyond.Data!["items"]!.AsArray());
            }

            Task<Helpdesk.Mcp.Tools.HelpdeskToolResponse> List(string json)
            {
                var request = JsonSerializer.Deserialize<JsonElement>(json);
                return family switch
                {
                    "incidents" => tools.helpdesk_incidents("list", request),
                    "requests" => tools.helpdesk_requests("list", request),
                    _ => tools.helpdesk_changes("list", request)
                };
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_task_REST_large_page_cannot_overflow_into_first_page_and_MCP_bounds_preserve_valid_queries(bool postgres)
    {
        await using var h = await TaskHarness.CreateAsync(postgres);
        await h.EditAsync(db =>
        {
            db.Requests.Add(new Helpdesk.Shared.Models.Request { Id = "pagination-parent", Title = "pagination parent", TrackingId = "PAGE-PARENT", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId });
            for (var index = 0; index < 5; index++)
                db.RequestTasks.Add(new RequestTask { Id = $"pagination-task-{index}", Title = "pagination matching task", TrackingId = $"PAGE-TASK-{index}", OrganizationId = h.OrganizationId, RequestId = "pagination-parent", CustomerId = h.CustomerId, CreatedAt = new DateTime(2026, 10, 1, index, 0, 0, DateTimeKind.Utc) });
            return Task.CompletedTask;
        });
        foreach (var page in new[] { 21_474_837, int.MaxValue })
        {
            using var response = await h.AdminAsync(HttpMethod.Get, $"/api/v1/request-tasks/?q=pagination&page={page}&pageSize=100");
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(page, result.GetProperty("page").GetInt32());
            Assert.Equal(5, result.GetProperty("totalCount").GetInt32());
            Assert.Empty(result.GetProperty("items").EnumerateArray());
        }
        foreach (var parameters in new[] { "page=0&pageSize=10000", "page=-1&pageSize=101" })
        {
            using var response = await h.AdminAsync(HttpMethod.Get, "/api/v1/request-tasks/?q=pagination&" + parameters);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, result.GetProperty("page").GetInt32());
            Assert.Equal(100, result.GetProperty("pageSize").GetInt32());
            Assert.Equal(5, result.GetProperty("items").GetArrayLength());
        }
        var tools = TicketAdapterHttpTests.Tools(h.Client, admin: true);
        foreach (var json in new[] { """{"query":"pagination"}""", """{"query":"pagination","page":1.0,"pageSize":1e0}""", """{"q":"pagination","page":1000000,"pageSize":100}""" })
        {
            var response = await tools.helpdesk_request_tasks("list", JsonSerializer.Deserialize<JsonElement>(json));
            Assert.True(response.Success, response.Summary);
            Assert.Equal(5, response.Data!["totalCount"]!.GetValue<int>());
            Assert.Equal(json.Contains("1000000", StringComparison.Ordinal) ? 0 : json.Contains("pageSize", StringComparison.Ordinal) ? 1 : 5, response.Data!["items"]!.AsArray().Count);
        }
    }

    private sealed class TokenApiBridge(HttpClient client) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery);
            forwarded.Headers.Authorization = request.Headers.Authorization;
            return await client.SendAsync(forwarded, cancellationToken);
        }
    }
}
