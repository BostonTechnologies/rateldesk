using System.Text.Json;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Helpdesk.Tests.Api;

public sealed class McpPolicyDefinitionTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 18, 0, 0, DateTimeKind.Utc);
    private static IntegrationCredential Credential(bool owner = false) => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = "synthetic-owner", OrganizationId = "organization-a",
        Purpose = owner ? IntegrationCredentialGrantKinds.OwnerAdministrationPurpose : "mcp",
        Permissions = owner ? HelpdeskPermissions.HelpdeskAdmin : HelpdeskPermissions.IncidentRead,
        McpResourceUri = "https://helpdesk.example.test/mcp", ExpiresAtUtc = new DateTimeOffset(Now.AddDays(1))
    };
    private static McpPolicyScopeDraft Scope(bool owner = false) => new("Synthetic policy", owner ? "server" : "organization",
        owner ? null : "organization-a", "Allow", [owner ? HelpdeskPermissions.HelpdeskAdmin : HelpdeskPermissions.IncidentRead],
        ["helpdesk_incidents.get", "helpdesk_raw.get_incident", "helpdesk_incidents.list"], Now.AddHours(4), []);

    [Fact]
    public void Scope_parent_grants_and_expiry_form_non_broadenable_limits()
    {
        var credential = Credential(); var scope = Scope();
        Assert.Null(McpPolicyDefinitionService.ValidateScopes(credential, [scope], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope, scope], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope with { Scope = "server", OrganizationId = null }], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope with { OrganizationId = "organization-b" }], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope with { Permissions = [HelpdeskPermissions.IncidentWrite] }], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope with { ExpiresUtc = Now.AddDays(2) }], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope with { Operations = ["invented.operation"] }], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope with { Exceptions = [new("Broader", "Allow", ["helpdesk_incidents.get"], scope.ExpiresUtc)] }], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope with { Exceptions = [new("Outside parent", "Deny", ["helpdesk_requests.get"], scope.ExpiresUtc)] }], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(credential, [scope with { Exceptions = [new("Too long", "Deny", ["helpdesk_incidents.get"], scope.ExpiresUtc.AddTicks(10))] }], Now));
        Assert.Null(McpPolicyDefinitionService.ValidateScopes(Credential(true), [Scope(true)], Now));
        Assert.NotNull(McpPolicyDefinitionService.ValidateScopes(Credential(true), [Scope()], Now));
    }

    [Fact]
    public void Exact_routes_alias_denies_and_parent_lifecycle_are_enforced()
    {
        var credential = Credential();
        var scope = Scope() with { Exceptions = [new("No detail", "Deny", ["helpdesk_raw.get_incident"], Now.AddHours(2))] };
        var (row, audit) = McpPolicyDefinitionService.Create(credential, [scope], "synthetic-admin", Now);
        Assert.Equal(1, audit.Revision);
        Assert.True(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/incidents/", "/api/v1/incidents/", Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/incidents/example", "/api/v1/incidents/{id}", Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/incidents/summary", "/api/v1/incidents/summary", Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "POST", "/api/v1/incidents/", "/api/v1/incidents/", Now));
        Assert.True(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/incidents/example", "/api/v1/incidents/{id}", Now.AddHours(3)));
        foreach (var state in new[] { "Disabled", "Revoked", "Deleted" })
        {
            row.State = state;
            Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/incidents/", null, Now));
        }
        row.State = "Active";
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/incidents/", null, scope.ExpiresUtc));
        credential.McpResourceUri = "https://other.example.test/mcp";
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/incidents/", null, Now));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1,\"Resource\":\"https://helpdesk.example.test/mcp\",\"Scopes\":[null]}")]
    public void Malformed_policy_definition_fails_closed(string definition)
    {
        var row = new McpAccessPolicy { DefinitionJson = definition, State = "Active", ExpiresUtc = Now.AddDays(1) };
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, Credential(), "GET", "/api/v1/incidents/", null, Now));
    }

    [Fact]
    public void Gateway_status_health_and_self_assignment_use_actual_identity_route_without_deny_bypass()
    {
        var credential = Credential(true);
        foreach (var operation in new[] { "helpdesk_auth.status", "helpdesk_health.get", "helpdesk_incidents.assign_self" })
        {
            var scope = Scope(true) with { Operations = [operation] };
            var (row, _) = McpPolicyDefinitionService.Create(credential, [scope], "synthetic-admin", Now);
            Assert.True(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/auth/me", "/api/v1/auth/me", Now));
        }
        var aliasScope = Scope(true) with
        {
            Operations = ["helpdesk_auth.status", "helpdesk_auth.access", "helpdesk_incidents.assign_self"],
            Exceptions = [new("No status", "Deny", ["helpdesk_auth.status"], Now.AddHours(1))]
        };
        var (aliasRow, _) = McpPolicyDefinitionService.Create(credential, [aliasScope], "synthetic-admin", Now);
        Assert.False(McpPolicyDefinitionService.AllowsRequest(aliasRow, credential, "GET", "/api/v1/auth/me", "/api/v1/auth/me", Now));
        var assignmentScope = aliasScope with { Exceptions = [new("No assignment", "Deny", ["helpdesk_incidents.assign_self"], Now.AddHours(1))] };
        var (assignmentRow, _) = McpPolicyDefinitionService.Create(credential, [assignmentScope], "synthetic-admin", Now);
        Assert.True(McpPolicyDefinitionService.AllowsRequest(assignmentRow, credential, "GET", "/api/v1/auth/me", "/api/v1/auth/me", Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(assignmentRow, credential, "POST", "/api/v1/incidents/bulk/assign", "/api/v1/incidents/bulk/assign", Now));
    }

    [Fact]
    public void Ticket_type_placeholder_cannot_authorize_unrelated_literal_routes()
    {
        var credential = Credential();
        var scope = Scope() with { Operations = ["helpdesk_tickets.timeline_count"] };
        var (row, _) = McpPolicyDefinitionService.Create(credential, [scope], "synthetic-admin", Now);
        Assert.True(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/incidents/example/timeline/count", "/api/v1/incidents/{id}/timeline/count", Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/admin/example/timeline/count", "/api/v1/admin/{id}/timeline/count", Now));
    }

    [Theory]
    [InlineData("incidents")]
    [InlineData("requests")]
    [InlineData("changes")]
    public void Generic_ticket_routes_resolve_the_actual_ticket_type_without_opening_other_areas(string ticketType)
    {
        var credential = Credential(true);
        var scope = Scope(true) with { Operations = ["helpdesk_ai_assistant.chat_capabilities"] };
        var (row, _) = McpPolicyDefinitionService.Create(credential, [scope], "synthetic-admin", Now);
        const string template = "/api/v1/{ticketType}/{ticketId}/ai-assistant/chat-capabilities";
        Assert.True(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", $"/api/v1/{ticketType}/example/ai-assistant/chat-capabilities", template, Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/admin/example/ai-assistant/chat-capabilities", template, Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", $"/api/v1/{ticketType}/example/ai-assistant/chat-capabilities", null, Now));
    }

    [Fact]
    public void Read_only_chat_policy_requires_an_exact_conversation_and_cannot_create_by_omitting_the_query()
    {
        var credential = Credential(true);
        var scope = Scope(true) with { Operations = ["helpdesk_ai_assistant.chat"] };
        var (row, _) = McpPolicyDefinitionService.Create(credential, [scope], "synthetic-admin", Now);
        const string path = "/api/v1/incidents/example/ai-assistant/chat";
        const string template = "/api/v1/{ticketType}/{ticketId}/ai-assistant/chat";
        var query = new QueryCollection(new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        { ["conversationId"] = Guid.NewGuid().ToString() });
        Assert.True(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", path, template, Now, query));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", path, template, Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", path, template, Now,
            new QueryCollection(new Dictionary<string, StringValues> { ["conversationId"] = "" })));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", path, template, Now,
            new QueryCollection(new Dictionary<string, StringValues> { ["conversationId"] = new StringValues([Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]) })));

        var both = scope with
        {
            Operations = ["helpdesk_ai_assistant.chat", "helpdesk_ai_assistant.chat_open"],
            Exceptions = [new("Do not create conversations", "Deny", ["helpdesk_ai_assistant.chat_open"], Now.AddHours(1))]
        };
        var (restricted, _) = McpPolicyDefinitionService.Create(credential, [both], "synthetic-admin", Now);
        Assert.True(McpPolicyDefinitionService.AllowsRequest(restricted, credential, "GET", path, template, Now, query));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(restricted, credential, "GET", path, template, Now));
    }
}
