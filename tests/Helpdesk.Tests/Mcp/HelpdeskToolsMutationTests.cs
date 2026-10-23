using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.Models;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class HelpdeskToolsMutationTests
{
    [Fact]
    public async Task Outbound_timeout_is_returned_as_a_structured_tool_failure()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync("/api/v1/auth/ai-agent/status", true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<JsonNode?>(new AgentClientRemoteException("api_request_timeout", "Helpdesk API request timed out.", 504, null)));
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_auth(cancellationToken: CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("failed", response.Status);
        Assert.Equal("api_request_timeout", response.Failure?.Code);
        Assert.Equal(504, response.Failure?.UpstreamStatus);
        Assert.True(response.Failure?.Retryable);
    }

    [Theory]
    [InlineData("integration")]
    [InlineData(" Integration ")]
    public async Task Integration_credential_auth_status_uses_application_identity_endpoint(string credentialMode)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example", null, null, null, null, null, null)
        {
            CredentialMode = credentialMode,
            IntegrationCredential = "rdk_test"
        });
        client.GetAsync("/api/v1/auth/me", true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["authMode"] = "integration" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_auth(cancellationToken: CancellationToken.None);

        Assert.True(response.Success);
        await client.Received(1).GetAsync("/api/v1/auth/me", true, Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetAsync("/api/v1/auth/ai-agent/status", true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData(" Gateway ")]
    public async Task Gateway_credential_auth_status_uses_application_identity_endpoint(string credentialMode)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example", null, null, null, null, null, null)
        {
            CredentialMode = credentialMode
        });
        client.GetAsync("/api/v1/auth/me", true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["authMode"] = "integration" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_auth(cancellationToken: CancellationToken.None);

        Assert.True(response.Success);
        await client.Received(1).GetAsync("/api/v1/auth/me", true, Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetAsync("/api/v1/auth/ai-agent/status", true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("helpdesk_incidents", "state", "{\"incidentId\":\"INC-123\",\"newState\":\"Resolved\"}")]
    [InlineData("helpdesk_requests", "assign", "{\"ids\":[\"REQ-123\"],\"assignedToId\":\"user-1\"}")]
    [InlineData("helpdesk_changes", "lifecycle", "{\"changeId\":\"CHG-123\",\"lifecycleState\":\"Submitted\"}")]
    [InlineData("helpdesk_request_tasks", "complete", "{\"taskId\":\"TASK-123\"}")]
    public async Task Unconfirmed_ticket_mutations_never_call_the_api(string toolName, string operation, string json)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = toolName switch
        {
            "helpdesk_incidents" => await tools.helpdesk_incidents(operation, Request(json)),
            "helpdesk_requests" => await tools.helpdesk_requests(operation, Request(json)),
            "helpdesk_changes" => await tools.helpdesk_changes(operation, Request(json)),
            _ => await tools.helpdesk_request_tasks(operation, Request(json))
        };

        Assert.False(response.Success);
        Assert.Equal("confirmation_required", response.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Incident_create_requires_the_customer_and_organization_before_confirmation_or_outbound_call()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("create", Request("""{"title":"QA","description":"QA","priority":"Low"}"""), confirm: true);

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains("customerId", response.Summary, StringComparison.Ordinal);
        Assert.Contains("helpdesk_customers list", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Incident_create_serializes_the_documented_priority_label_to_the_api_enum_value()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["id"] = "INC-QA" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("create", Request("""{"title":"QA","description":"QA","priority":"Low","customerId":"customer-1","organizationId":"org-1"}"""), confirm: true);

        Assert.True(response.Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/incidents/", Arg.Is<JsonNode?>(body => HasLowPriority(body)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirmed_incident_state_uses_the_exact_endpoint_and_body()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["correlationId"] = "corr-123" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("state", Request("""{"incidentId":"INC-123","newState":"Resolved"}"""), confirm: true);

        Assert.True(response.Success);
        Assert.Equal("corr-123", response.CorrelationId);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/incidents/INC-123/state", Arg.Is<JsonNode?>(body => HasResolvedState(body)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unconfirmed_safe_config_change_is_not_persisted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-{Guid.NewGuid():N}.json");
        var store = new AgentClientConfigurationStore(path);
        store.Save(AgentClientConfiguration.Empty);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, store);

        var response = await tools.helpdesk_config("set", Request("""{"key":"apiBaseUrl","value":"https://helpdesk.test"}"""));

        Assert.Equal("confirmation_required", response.Status);
        Assert.Null(store.Load().ApiBaseUrl);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Config_cannot_persist_an_endpoint_for_another_instance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-{Guid.NewGuid():N}.json");
        var store = new AgentClientConfigurationStore(path);
        store.Save(AgentClientConfiguration.Empty);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var client = Substitute.For<IHelpdeskAgentClient>();
        var target = new HelpdeskMcpTarget("prod", new Uri("https://prod.example"));
        var tools = new HelpdeskTools(client, store, target);

        var response = await tools.helpdesk_config("set", Request("""{"key":"apiBaseUrl","value":"https://dev.example"}"""), confirm: true);

        Assert.Equal("validation_failed", response.Status);
        Assert.Equal("prod", response.Target?.Instance);
        Assert.Null(store.Load().ApiBaseUrl);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Tool_responses_include_the_immutable_target_context()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var target = new HelpdeskMcpTarget("prod", new Uri("https://prod.example/api"));
        var tools = new HelpdeskTools(client, Store(), target);

        var response = await tools.helpdesk_incidents("state", Request("""{"incidentId":"INC-123","newState":"Resolved"}"""));

        Assert.Equal("confirmation_required", response.Status);
        Assert.Equal("prod", response.Target?.Instance);
        Assert.Equal("https://prod.example/api", response.Target?.ApiBaseUrl);
    }

    [Fact]
    public async Task Assign_self_does_not_resolve_the_agent_before_confirmation()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("assign_self", Request("""{"ids":["INC-123"],"agentUserEmail":"operator@example.test"}"""));

        Assert.Equal("confirmation_required", response.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_bound_assign_self_rejects_an_email_override_without_an_upstream_call()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example", null, null, null, null, null, "operator@example.test")
        {
            CredentialMode = "integration",
            IntegrationCredential = "rdk_test"
        });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("assign_self", Request("""{"ids":["INC-123"],"agentUserEmail":"other@example.test"}"""), confirm: true);

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains("not permitted", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_bound_assign_self_uses_the_authenticated_application_identity()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example", null, null, null, null, null, "operator@example.test")
        {
            CredentialMode = "integration",
            IntegrationCredential = "rdk_test"
        });
        client.GetAsync("/api/v1/auth/me", true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["userId"] = "caller-user" });
        JsonNode? assignedBody = null;
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                assignedBody = call.ArgAt<JsonNode?>(2);
                return new JsonObject { ["id"] = "INC-123" };
            });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("assign_self", Request("""{"ids":["INC-123"]}"""), confirm: true);

        Assert.True(response.Success);
        await client.Received(1).GetAsync("/api/v1/auth/me", true, Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetAsync(Arg.Is<string>(path => path.StartsWith("/api/v1/users/by-email/", StringComparison.Ordinal)), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/incidents/bulk/assign", Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>());
        Assert.Equal("caller-user", (assignedBody as JsonObject)?["assignedToId"]?.ToString());
    }
    [Fact]
    public async Task Close_incident_without_confirmation_does_not_call_the_api()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("close", Request("""{"incidentId":"INC-123","closureNote":"BTClaw closed this test incident."}"""), confirm: false);

        Assert.False(response.Success);
        Assert.Equal("confirmation_required", response.Status);
        Assert.Equal(["INC-123"], response.AffectedIds);
        Assert.Equal("close", response.Confirmation?.Operation);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Close_incident_invalid_request_returns_the_exact_recovery_fields()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("close", Request("""{"incidentId":"INC-123","notes":"wrong"}"""));

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains("request.incidentId, request.closureNote, and optional request.isInternalNote", response.Summary, StringComparison.Ordinal);
        Assert.Contains("Use add_worklog when the note needs hours", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirmed_close_writes_internal_note_then_resolves_incident()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["id"] = "result" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("close", Request("""{"incidentId":"INC-123","closureNote":"BTClaw closed this test incident."}"""), confirm: true);

        Assert.True(response.Success);
        Assert.Equal("completed", response.Status);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/incidents/INC-123/worklogs", Arg.Is<JsonNode?>(body => IsInternalClosureNote(body)), true, Arg.Any<CancellationToken>());
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/incidents/INC-123/state", Arg.Is<JsonNode?>(body => IsResolvedState(body)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirmed_incident_worklog_uses_the_exact_endpoint_and_body()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["id"] = "worklog-1" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("add_worklog", Request("""{"incidentId":"INC-123","hours":1.5,"notes":"Investigated service impact.","isInternalNote":true}"""), confirm: true);

        Assert.True(response.Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/incidents/INC-123/worklogs", Arg.Is<JsonNode?>(body => IsWorklog(body, 1.5m, "Investigated service impact.", true)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Incident_worklog_invalid_request_returns_the_exact_recovery_fields()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("add_worklog", Request("""{"incidentId":"INC-123","note":"wrong"}"""));

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains("request.incidentId, request.hours, request.notes, and optional request.isInternalNote", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("request", "requestId", "REQ-123")]
    [InlineData("change", "changeId", "CHG-123")]
    public async Task Request_and_change_worklogs_reject_malformed_payloads_with_the_exact_recovery_contract(string ticketType, string idName, string ticketId)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());
        var request = Request($$"""{"{{idName}}":"{{ticketId}}","note":"wrong"}""");

        var response = ticketType == "request"
            ? await tools.helpdesk_requests("add_worklog", request)
            : await tools.helpdesk_changes("add_worklog", request);

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains($"request.{idName}, request.hours, request.notes, and optional request.isInternalNote", response.Summary, StringComparison.Ordinal);
        Assert.Contains("Hours must be non-negative", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("request", "requestId", "REQ-123", "/api/v1/requests/REQ-123/worklogs")]
    [InlineData("change", "changeId", "CHG-123", "/api/v1/changes/CHG-123/worklogs")]
    public async Task Request_and_change_worklogs_use_the_canonical_internal_worklog_body(string ticketType, string idName, string ticketId, string expectedPath)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["id"] = "worklog-1" });
        var tools = new HelpdeskTools(client, Store());
        var request = Request($$"""{"{{idName}}":"{{ticketId}}","hours":0,"notes":"Matrix diagnostic note"}""");

        var response = ticketType == "request"
            ? await tools.helpdesk_requests("add_worklog", request, confirm: true)
            : await tools.helpdesk_changes("add_worklog", request, confirm: true);

        Assert.True(response.Success);
        await client.Received(1).SendAsync(HttpMethod.Post, expectedPath, Arg.Is<JsonNode?>(body => IsWorklog(body, 0m, "Matrix diagnostic note", true)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_task_create_rejects_missing_required_fields_without_an_upstream_call()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_request_tasks("create", Request("""{"title":"matrix"}"""), confirm: true);

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains("request.title and request.description", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_task_create_uses_a_normalized_priority_and_exact_route()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["id"] = "task-1" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_request_tasks("create", Request("""{"title":"matrix","description":"matrix","requestId":"request-1","organizationId":"organization-1","priority":"Low"}"""), confirm: true);

        Assert.True(response.Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/request-tasks/", Arg.Is<JsonNode?>(body => HasLowPriority(body)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_task_update_uses_the_safe_patch_contract_without_the_route_identifier_in_the_body()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["id"] = "task-1" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_request_tasks("update", Request("""{"taskId":"task-1","title":"updated matrix task","clearDueDate":true}"""), confirm: true);

        Assert.True(response.Success);
        await client.Received(1).SendAsync(HttpMethod.Patch, "/api/v1/request-tasks/task-1", Arg.Is<JsonNode?>(body => IsTaskPatch(body)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_task_update_rejects_an_empty_or_open_ended_patch_without_an_upstream_call()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var empty = await tools.helpdesk_request_tasks("update", Request("""{"taskId":"task-1"}"""), confirm: true);
        var openEnded = await tools.helpdesk_request_tasks("update", Request("""{"taskId":"task-1","status":"Completed"}"""), confirm: true);

        Assert.Equal("validation_failed", empty.Status);
        Assert.Contains("at least one", empty.Summary, StringComparison.Ordinal);
        Assert.Equal("validation_failed", openEnded.Status);
        Assert.Contains("not supported", openEnded.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bulk_ticket_create_rejects_an_invalid_item_without_an_upstream_call()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("bulk_create", Request("""{"items":[{"title":"matrix","description":"matrix"}]}"""), confirm: true);

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains("request.items entry.customerId", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bulk_ticket_create_normalizes_each_priority_before_dispatch()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["id"] = "incident-1" });
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("bulk_create", Request("""{"items":[{"title":"matrix","description":"matrix","customerId":"customer-1","organizationId":"organization-1","priority":"Low"}]}"""), confirm: true);

        Assert.True(response.Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/incidents/", Arg.Is<JsonNode?>(body => HasLowPriority(body)), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("request")]
    [InlineData("change")]
    public async Task Request_and_change_creation_reject_missing_api_prerequisites_before_confirmation(string ticketType)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());
        var request = ticketType == "request"
            ? Request("""{"title":"matrix","description":"matrix"}""")
            : Request("""{"title":"matrix","description":"matrix","changeType":"Normal","changeTemplate":{}}""");

        var response = ticketType == "request"
            ? await tools.helpdesk_requests("create", request, confirm: true)
            : await tools.helpdesk_changes("create", request, confirm: true);

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains(ticketType == "request" ? "customerId" : "organizationId", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Change_creation_reports_the_missing_normal_template_fields_without_an_upstream_call()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_changes("create", Request("""
            {"title":"matrix","description":"matrix","organizationId":"organization-1","requestedForUserId":"user-1","implementorUserId":"user-2","implementationStartAt":"2026-08-23T10:00:00Z","implementationEndAt":"2026-08-23T11:00:00Z","changeType":"Normal","changeTemplate":{"scopeOfChange":"bounded change"}}
            """), confirm: true);

        Assert.Equal("validation_failed", response.Status);
        Assert.Contains("affectedSystems", response.Summary, StringComparison.Ordinal);
        Assert.Contains("businessJustification", response.Summary, StringComparison.Ordinal);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_forms_by_service_uses_the_api_forms_route()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonArray());
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_request_forms("by_service", Request("""{"serviceId":"service/one"}"""));

        Assert.True(response.Success);
        await client.Received(1).GetAsync("/api/v1/services/service%2Fone/forms/", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_form_contract_rejects_unknown_properties_without_calling_the_api()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_request_forms("by_service", Request("""{"serviceId":"service-1","unexpected":true}"""));

        Assert.Equal("validation_failed", response.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ticket_count_uses_the_ticket_type_and_identifier()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(JsonValue.Create(4));
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_tickets("timeline_count", Request("""{"ticketType":"incidents","ticketId":"INC-123"}"""));

        Assert.True(response.Success);
        await client.Received(1).GetAsync("/api/v1/incidents/INC-123/timeline/count", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Domain_ticket_count_uses_the_known_incident_type()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(JsonValue.Create(2));
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_incidents("attachments_count", Request("""{"incidentId":"INC-123"}"""));

        Assert.True(response.Success);
        await client.Received(1).GetAsync("/api/v1/incidents/INC-123/attachments/count", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ticket_count_requires_a_supported_ticket_type_without_calling_the_api()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_tickets("timeline_count", Request("""{"ticketId":"INC-123"}"""));

        Assert.Equal("validation_failed", response.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Organization_tenants_uses_the_global_admin_route()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonArray());
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_organizations("tenants");

        Assert.True(response.Success);
        await client.Received(1).GetAsync("/api/v1/admin/tenants", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Service_items_uses_the_service_items_route()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonArray());
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_services("items", Request("""{"serviceId":"service-1"}"""));

        Assert.True(response.Success);
        await client.Received(1).GetAsync("/api/v1/service-items/service-1", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Category_get_is_rejected_locally_without_an_upstream_call()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());

        var response = await tools.helpdesk_categories("get", Request("""{"categoryId":"category-1"}"""));

        Assert.Equal("validation_failed", response.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Service_request_form_requires_confirmation_and_uses_the_nested_forms_route()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["id"] = "form-1" });
        var tools = new HelpdeskTools(client, Store());
        var request = Request("""{"serviceId":"service-1","title":"New access form"}""");

        var unconfirmed = await tools.helpdesk_admin_mutations("create_service_request_form", request);
        var confirmed = await tools.helpdesk_admin_mutations("create_service_request_form", request, confirm: true);

        Assert.Equal("confirmation_required", unconfirmed.Status);
        Assert.True(confirmed.Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/services/service-1/forms/", Arg.Is<JsonNode?>(body => IsServiceForm(body)), true, Arg.Any<CancellationToken>());
    }

    private static JsonElement Request(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static AgentClientConfigurationStore Store() => new(Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-{Guid.NewGuid():N}.json"));

    private static bool IsInternalClosureNote(JsonNode? body)
        => body is JsonObject value && value["notes"]?.ToString() == "BTClaw closed this test incident." && value["isInternalNote"]?.GetValue<bool>() == true;

    private static bool HasResolvedState(JsonNode? body)
        => body is JsonObject value && value["newState"]?.GetValue<int>() == (int)TicketState.Resolved;

    private static bool IsResolvedState(JsonNode? body)
        => body is JsonObject value && value["newState"]?.GetValue<int>() == (int)TicketState.Resolved;

    private static bool IsWorklog(JsonNode? body, decimal hours, string notes, bool isInternalNote)
        => body is JsonObject value
           && value["hours"]?.GetValue<decimal>() == hours
           && value["notes"]?.ToString() == notes
           && value["isInternalNote"]?.GetValue<bool>() == isInternalNote;

    private static bool IsServiceForm(JsonNode? body)
        => body is JsonObject value && value["title"]?.ToString() == "New access form" && value["serviceId"] is null;

    private static bool HasLowPriority(JsonNode? body)
        => body is JsonObject value && value["priority"]?.GetValue<int>() == (int)TicketPriority.Low;

    private static bool IsTaskPatch(JsonNode? body)
        => body is JsonObject value && value["taskId"] is null && value["title"]?.ToString() == "updated matrix task" && value["clearDueDate"]?.GetValue<bool>() == true;
}
