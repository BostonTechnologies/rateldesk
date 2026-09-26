using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Helpdesk.Tests.Cli;

public sealed class HelpdeskCliTests
{
    [Fact]
    public void RootCommand_Exposes_AgentWorkflowSurface()
    {
        var root = HelpdeskCli.BuildRoot(new CliRuntime());
        var commandNames = root.Children.OfType<System.CommandLine.Command>().Select(c => c.Name).ToArray();

        Assert.Contains("auth", commandNames);
        Assert.Contains("config", commandNames);
        Assert.Contains("health", commandNames);
        Assert.Contains("logs", commandNames);
        Assert.Contains("incidents", commandNames);
        Assert.Contains("requests", commandNames);
        Assert.Contains("changes", commandNames);
        Assert.Contains("request-tasks", commandNames);
        Assert.Contains("tickets", commandNames);
        Assert.Contains("organizations", commandNames);
        Assert.Contains("customers", commandNames);
        Assert.Contains("users", commandNames);
        Assert.Contains("self-service", commandNames);
        Assert.Contains("notifications", commandNames);
        Assert.Contains("capabilities", commandNames);
        Assert.Contains("schema", commandNames);
        Assert.Contains("enums", commandNames);
        Assert.Contains("examples", commandNames);
        Assert.Contains("raw", commandNames);
    }

    [Fact]
    public async Task AuthConfigure_WritesConfig_And_RedactsSecret()
    {
        var temp = Directory.CreateTempSubdirectory("helpdesk-cli-test-");
        var path = Path.Combine(temp.FullName, "cli.json");
        var output = new StringWriter();
        var errors = new StringWriter();

        var code = await HelpdeskCli.RunAsync([
            "--config", path,
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "--agent-user-email", "agent@example.com",
            "auth", "configure"
        ], new CliRuntime { Out = output, Error = errors });

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Contains("secret", File.ReadAllText(path), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        code = await HelpdeskCli.RunAsync(["--config", path, "config", "show"], new CliRuntime { Out = output, Error = errors });

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Contains("\"authentikAppPassword\":\"***\"", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent@example.com", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthToken_MintsClientCredentialsToken()
    {
        async Task<HttpResponseMessage> Responder(HttpRequestMessage request)
        {
            Assert.Equal("https://auth.example/token", request.RequestUri!.ToString());
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=client_credentials", body, StringComparison.Ordinal);
            Assert.Contains("client_id=client", body, StringComparison.Ordinal);
            Assert.Contains("username=agent", body, StringComparison.Ordinal);
            Assert.Contains("password=secret", body, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");
        }

        var output = new StringWriter();
        var code = await HelpdeskCli.RunAsync([
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "auth", "token"
        ], new CliRuntime(() => new RecordingHandler(Responder)) { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Equal("minted-token", output.ToString().Trim());
    }

    [Fact]
    public async Task ConfigShow_UsesRatelDeskAuthentikEnvironment()
    {
        var variables = new Dictionary<string, string?>
        {
            ["RATELDESK_API_BASE_URL"] = "https://helpdesk-api.example",
            ["RATELDESK_AUTHENTIK_TOKEN_URL"] = "https://helpdesk-auth.example/token",
            ["RATELDESK_AUTHENTIK_CLIENT_ID"] = "helpdesk-client",
            ["RATELDESK_AUTHENTIK_USERNAME"] = "helpdesk-agent",
            ["RATELDESK_AUTHENTIK_APP_PASSWORD"] = "helpdesk-secret",
            ["RATELDESK_AGENT_USER_EMAIL"] = "agent@example.com"
        };
        var previous = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var (key, value) in variables)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            var temp = Directory.CreateTempSubdirectory("helpdesk-cli-test-");
            var output = new StringWriter();
            var code = await HelpdeskCli.RunAsync([
                "--config", Path.Combine(temp.FullName, "missing.json"),
                "config", "show"
            ], new CliRuntime { Out = output, Error = new StringWriter() });

            Assert.Equal(CliExitCodes.Success, code);
            Assert.Contains("\"apiBaseUrl\":\"https://helpdesk-api.example\"", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"authentikTokenUrl\":\"https://helpdesk-auth.example/token\"", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"authentikClientId\":\"helpdesk-client\"", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"authentikUsername\":\"helpdesk-agent\"", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    [Fact]
    public async Task IncidentsAssignSelf_ResolvesAgentUser_And_BulkAssigns()
    {
        var calls = new List<HttpRequestMessage>();
        Task<HttpResponseMessage> Responder(HttpRequestMessage request)
        {
            calls.Add(CloneForAssert(request));
            return calls.Count switch
            {
                1 => Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""")),
                2 => Task.FromResult(Json(HttpStatusCode.OK, """{"id":"user-agent","email":"agent@example.com"}""")),
                3 => Task.FromResult(Json(HttpStatusCode.OK, """{"updated":2}""")),
                _ => Task.FromResult(Json(HttpStatusCode.NotFound, "{}"))
            };
        }

        var code = await HelpdeskCli.RunAsync([
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "incidents", "assign-self", "--ids", "inc-1,inc-2"
        ], new CliRuntime(() => new RecordingHandler(Responder)) { Out = new StringWriter(), Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Equal(HttpMethod.Get, calls[1].Method);
        Assert.Equal("https://api.example/api/v1/users/by-email/agent%40example.com", calls[1].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, calls[2].Method);
        Assert.Equal("https://api.example/api/v1/incidents/bulk/assign", calls[2].RequestUri!.ToString());
        var assignBody = await calls[2].Content!.ReadAsStringAsync();
        Assert.Contains("\"assignedToId\":\"user-agent\"", assignBody, StringComparison.Ordinal);
        Assert.Contains("\"inc-1\"", assignBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsAiAudit_UsesExpectedRoute()
    {
        var calls = 0;
        Task<HttpResponseMessage> Responder(HttpRequestMessage request)
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example/api/v1/requests/req-1/ai-audit", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(Json(HttpStatusCode.OK, "[]"));
        }

        var code = await HelpdeskCli.RunAsync([
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "requests", "ai-audit", "req-1"
        ], new CliRuntime(() => new RecordingHandler(Responder)) { Out = new StringWriter(), Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
    }

    [Fact]
    public async Task Raw_Rejects_NonOperatorPaths()
    {
        var errors = new StringWriter();

        var code = await HelpdeskCli.RunAsync([
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "raw", "get", "--path", "/internal/health"
        ], new CliRuntime { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("operator allow-list", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigGet_WithoutKey_ReturnsValidationError()
    {
        var errors = new StringWriter();

        var code = await HelpdeskCli.RunAsync(
            ["config", "get"],
            new CliRuntime { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("Required argument missing", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncidentsList_Defaults_ToSummaryTable()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "incidents", "list", "--page", "1", "--page-size", "2"
        ], request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example/api/v1/incidents/?page=1&pageSize=2", request.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, TicketPageJson("inc-1", "INC-001", "Network down", state: 3, priority: 3));
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
        var text = output.ToString();
        Assert.Contains("INCIDENTS", text, StringComparison.Ordinal);
        Assert.Contains("TRACKING ID", text, StringComparison.Ordinal);
        Assert.Contains("In Progress", text, StringComparison.Ordinal);
        Assert.Contains("P0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadJson", text, StringComparison.Ordinal);
        Assert.DoesNotContain("messageHtml", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncidentsList_Json_Defaults_ToRaw()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "incidents", "list", "--json"
        ], _ => Json(HttpStatusCode.OK, TicketPageJson("inc-1", "INC-001", "Network down", state: 3, priority: 3)), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(3, item.GetProperty("state").GetInt32());
        Assert.False(item.TryGetProperty("stateName", out _));
        Assert.True(item.TryGetProperty("payloadJson", out _));
    }

    [Fact]
    public async Task IncidentsList_JsonSummary_AddsEnumNames_AndFiltersFields()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "incidents", "list", "--json", "--view", "summary", "--fields", "trackingId,state,stateName,stateLabel,priority,priorityName,priorityLabel"
        ], _ => Json(HttpStatusCode.OK, TicketPageJson("inc-1", "INC-001", "Network down", state: 3, priority: 3)), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(3, item.GetProperty("state").GetInt32());
        Assert.Equal("InProgress", item.GetProperty("stateName").GetString());
        Assert.Equal("In Progress", item.GetProperty("stateLabel").GetString());
        Assert.Equal(3, item.GetProperty("priority").GetInt32());
        Assert.Equal("Critical", item.GetProperty("priorityName").GetString());
        Assert.Equal("Critical", item.GetProperty("priorityLabel").GetString());
        Assert.False(item.TryGetProperty("payloadJson", out _));
    }

    [Fact]
    public async Task IncidentsList_TextFields_OnlyShowsRequestedProjection()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "incidents", "list", "--view", "summary", "--fields", "trackingId,stateLabel,subject"
        ], _ => Json(HttpStatusCode.OK, TicketPageJson("inc-1", "INC-001", "Network down", state: 3, priority: 3)), output);

        Assert.Equal(CliExitCodes.Success, code);
        var text = output.ToString();
        Assert.Contains("TRACKINGID", text, StringComparison.Ordinal);
        Assert.Contains("STATELABEL", text, StringComparison.Ordinal);
        Assert.Contains("SUBJECT", text, StringComparison.Ordinal);
        Assert.Contains("INC-001", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CUSTOMER/ORG", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncidentsList_InvalidField_FailsClearly()
    {
        var errors = new StringWriter();
        var code = await HelpdeskCli.RunAsync(BaseArgs([
            "incidents", "list", "--view", "summary", "--fields", "trackingId,nope"
        ]), new CliRuntime(() => new RecordingHandler(request => Task.FromResult(Json(HttpStatusCode.OK, request.RequestUri!.Host == "auth.example"
            ? """{"access_token":"minted-token"}"""
            : TicketPageJson("inc-1", "INC-001", "Network down", state: 3, priority: 3)))))
        { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("Unknown field", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("trackingId", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawView_WithFields_FailsClearly()
    {
        var errors = new StringWriter();
        var code = await HelpdeskCli.RunAsync(BaseArgs([
            "incidents", "list", "--json", "--fields", "trackingId"
        ]), new CliRuntime { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("--fields is only supported", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangesList_JsonSummary_AddsLifecycleLabels()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "changes", "list", "--json", "--view", "summary"
        ], _ => Json(HttpStatusCode.OK, TicketPageJson("chg-1", "CHG-001", "Deploy patch", state: 4, priority: 2, lifecycleState: 3)), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(3, item.GetProperty("lifecycleState").GetInt32());
        Assert.Equal("ApprovedForImplementation", item.GetProperty("lifecycleStateName").GetString());
        Assert.Equal("Approved for Implementation", item.GetProperty("lifecycleStateLabel").GetString());
    }

    [Fact]
    public async Task IncidentsSummary_AggregatesScannedItems()
    {
        var output = new StringWriter();
        var apiCalls = 0;
        var code = await RunWithApiAsync([
            "incidents", "summary", "--active-only", "--page-size", "2", "--max-items", "2", "--json"
        ], request =>
        {
            apiCalls++;
            Assert.Equal("https://api.example/api/v1/incidents/?page=1&pageSize=2&activeOnly=True&includeTotal=True&summaryOnly=true", request.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, TicketPageJson("inc-1", "INC-001", "Network down", state: 3, priority: 3, totalCount: 3, second: true));
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Equal(1, apiCalls);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("incidents", doc.RootElement.GetProperty("resource").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("scannedCount").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("totalCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("capReached").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("partialResults").GetBoolean());
        Assert.Contains(doc.RootElement.GetProperty("byPriority").EnumerateArray(), item => item.GetProperty("priorityName").GetString() == "Critical");
    }

    [Fact]
    public async Task IncidentsSummary_UnknownTotal_DoesNotClaimPartial()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "incidents", "summary", "--page-size", "2", "--max-items", "2", "--json"
        ], _ => Json(HttpStatusCode.OK, TicketArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("totalCount").ValueKind);
        Assert.True(doc.RootElement.GetProperty("capReached").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("partialResults").GetBoolean());
    }

    [Fact]
    public async Task RouteFixes_UseExpectedPaths()
    {
        var calls = new List<HttpRequestMessage>();
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "notifications", "summary", "--json"
        ], request =>
        {
            calls.Add(CloneForAssert(request));
            return Json(HttpStatusCode.OK, """{"unreadErrorCount":2}""");
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Equal("https://api.example/api/v1/notifications/error-summary", calls.Single().RequestUri!.ToString());

        calls.Clear();
        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync(["roles", "list", "--json"], request =>
        {
            calls.Add(CloneForAssert(request));
            return Json(HttpStatusCode.OK, "[]");
        }, output);
        Assert.Equal(CliExitCodes.Success, code);
        Assert.Equal("https://api.example/api/v1/admin/role-definitions/", calls.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task SystemAliases_Work()
    {
        var calls = new List<HttpRequestMessage>();
        var output = new StringWriter();
        var code = await RunWithApiAsync(["system", "info", "--json"], request =>
        {
            calls.Add(CloneForAssert(request));
            return Json(HttpStatusCode.OK, """{"displayVersion":"v1"}""");
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Equal("https://api.example/api/v1/system/version", calls.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task RequestFormsList_ReturnsActionableError()
    {
        var errors = new StringWriter();
        var code = await HelpdeskCli.RunAsync(["request-forms", "list"], new CliRuntime { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("by-service", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncidentsWorklogs_ReturnsSuggestion()
    {
        var errors = new StringWriter();
        var code = await HelpdeskCli.RunAsync(["incidents", "worklogs", "inc-1"], new CliRuntime { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("incidents timeline inc-1", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestTasksList_Defaults_ToSummaryTable_And_ProjectsJson()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "request-tasks", "list", "--assigned-to-me", "--limit", "2"
        ], request =>
        {
            Assert.Equal("https://api.example/api/v1/request-tasks/?pageSize=2&assignedToMe=True", request.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, RequestTaskPageJson());
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
        var text = output.ToString();
        Assert.Contains("REQUEST-TASKS", text, StringComparison.Ordinal);
        Assert.Contains("Pending Approval", text, StringComparison.Ordinal);
        Assert.Contains("Approval", text, StringComparison.Ordinal);
        Assert.DoesNotContain("orchestration-run-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("conditionExpression", text, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "request-tasks", "list", "--json", "--view", "summary"
        ], _ => Json(HttpStatusCode.OK, RequestTaskPageJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(6, item.GetProperty("status").GetInt32());
        Assert.Equal("PendingApproval", item.GetProperty("statusName").GetString());
        Assert.Equal("Pending Approval", item.GetProperty("statusLabel").GetString());
        Assert.Equal("Approval", item.GetProperty("typeLabel").GetString());
        Assert.False(item.TryGetProperty("orchestrationExternalRunId", out _));
    }

    [Fact]
    public async Task RequestTasksList_Json_Defaults_ToRaw()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "request-tasks", "list", "--json"
        ], _ => Json(HttpStatusCode.OK, RequestTaskPageJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("orchestration-run-1", doc.RootElement.GetProperty("items")[0].GetProperty("orchestrationExternalRunId").GetString());
        Assert.False(doc.RootElement.GetProperty("items")[0].TryGetProperty("statusLabel", out _));
    }

    [Fact]
    public async Task NotificationsList_Projects_And_SuppressesMetadata()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "notifications", "list", "--query", "mail", "--limit", "2"
        ], request =>
        {
            Assert.Equal("https://api.example/api/v1/notifications?pageSize=2&search=mail", request.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, NotificationPageJson());
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
        var text = output.ToString();
        Assert.Contains("NOTIFICATIONS", text, StringComparison.Ordinal);
        Assert.Contains("Critical", text, StringComparison.Ordinal);
        Assert.DoesNotContain("metadataJson", text, StringComparison.Ordinal);
        Assert.DoesNotContain("messageHtml", text, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "notifications", "list", "--json", "--view", "summary"
        ], _ => Json(HttpStatusCode.OK, NotificationPageJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(4, item.GetProperty("severity").GetInt32());
        Assert.Equal("Critical", item.GetProperty("severityName").GetString());
        Assert.False(item.TryGetProperty("metadataJson", out _));
        Assert.False(item.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task NotificationsGet_DefaultsSafe_And_ExplicitBodyFlagsIncludeContent()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "notifications", "get", "00000000-0000-0000-0000-000000000001", "--json", "--view", "detail"
        ], _ => Json(HttpStatusCode.OK, NotificationDetailJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using (var doc = JsonDocument.Parse(output.ToString()))
        {
            Assert.True(doc.RootElement.TryGetProperty("messageExcerpt", out _));
            Assert.False(doc.RootElement.TryGetProperty("messageHtml", out _));
            Assert.False(doc.RootElement.TryGetProperty("metadataJson", out _));
        }

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "notifications", "get", "00000000-0000-0000-0000-000000000001", "--json", "--view", "detail", "--include-body", "--include-html"
        ], _ => Json(HttpStatusCode.OK, NotificationDetailJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var full = JsonDocument.Parse(output.ToString());
        Assert.Equal("Full email body", full.RootElement.GetProperty("message").GetString());
        Assert.Equal("<p>Full email body</p>", full.RootElement.GetProperty("messageHtml").GetString());
        Assert.True(full.RootElement.TryGetProperty("metadataJson", out _));
    }

    [Fact]
    public async Task OrganizationsCustomersUsers_Project_ArrayResponses_And_FieldFiltering()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "organizations", "list", "--limit", "1", "--json", "--view", "summary"
        ], _ => Json(HttpStatusCode.OK, OrganizationArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using (var doc = JsonDocument.Parse(output.ToString()))
        {
            Assert.True(doc.RootElement.GetProperty("clientSide").GetBoolean());
            var item = doc.RootElement.GetProperty("items")[0];
            Assert.Equal("Enabled", item.GetProperty("stateLabel").GetString());
            Assert.False(item.TryGetProperty("contactInfo", out _));
        }

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "customers", "list", "--query", "acme", "--json", "--view", "summary", "--fields", "id,name,email,stateLabel"
        ], _ => Json(HttpStatusCode.OK, CustomerArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using (var doc = JsonDocument.Parse(output.ToString()))
        {
            var item = doc.RootElement.GetProperty("items")[0];
            Assert.Equal("cust-1", item.GetProperty("id").GetString());
            Assert.Equal("Enabled", item.GetProperty("stateLabel").GetString());
            Assert.False(item.TryGetProperty("authStatus", out _));
        }

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "users", "by-email", "person@example.com", "--json", "--view", "detail"
        ], _ => Json(HttpStatusCode.OK, UserJson(includePasswordHash: true)), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var user = JsonDocument.Parse(output.ToString());
        Assert.Equal("person@example.com", user.RootElement.GetProperty("email").GetString());
        Assert.False(user.RootElement.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task ProjectedInvalidFields_FailClearly_ForPr2Resources()
    {
        var errors = new StringWriter();
        var code = await HelpdeskCli.RunAsync(BaseArgs([
            "customers", "list", "--json", "--view", "summary", "--fields", "id,nope"
        ]), new CliRuntime(() => new RecordingHandler(request => Task.FromResult(Json(HttpStatusCode.OK, request.RequestUri!.Host == "auth.example"
            ? """{"access_token":"minted-token"}"""
            : CustomerArrayJson()))))
        { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("Unknown field", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketTimeline_And_Worklogs_DefaultSafe_BodyFlagsOptIn()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "incidents", "timeline", "inc-1", "--json", "--view", "detail"
        ], _ => Json(HttpStatusCode.OK, TimelineArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using (var doc = JsonDocument.Parse(output.ToString()))
        {
            var item = doc.RootElement.GetProperty("items")[0];
            Assert.True(item.TryGetProperty("messageTextExcerpt", out _));
            Assert.False(item.TryGetProperty("messageHtml", out _));
            Assert.False(item.TryGetProperty("payloadJson", out _));
        }

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "changes", "worklogs", "chg-1", "--json", "--view", "detail", "--include-body", "--include-html"
        ], _ => Json(HttpStatusCode.OK, WorklogArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var worklogs = JsonDocument.Parse(output.ToString());
        var worklog = worklogs.RootElement.GetProperty("items")[0];
        Assert.Equal("Investigated root cause", worklog.GetProperty("notes").GetString());
        Assert.Equal("<p>hidden</p>", worklog.GetProperty("messageHtml").GetString());
    }

    [Fact]
    public async Task Services_Project_Summary_And_Raw()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "services", "list"
        ], _ => Json(HttpStatusCode.OK, ServiceArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        var text = output.ToString();
        Assert.Contains("SERVICES", text, StringComparison.Ordinal);
        Assert.Contains("Laptop", text, StringComparison.Ordinal);
        Assert.DoesNotContain("org-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("allowedOrganizationIds", text, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "services", "list", "--json"
        ], _ => Json(HttpStatusCode.OK, ServiceArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var raw = JsonDocument.Parse(output.ToString());
        Assert.Equal("org-secret", raw.RootElement[0].GetProperty("allowedOrganizationIds")[0].GetString());
    }

    [Fact]
    public async Task RequestFormsList_RequiresServiceId_And_ProjectsServiceScopedForms()
    {
        var errors = new StringWriter();
        var code = await HelpdeskCli.RunAsync(["request-forms", "list"], new CliRuntime { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("--service-id", errors.ToString(), StringComparison.Ordinal);

        var output = new StringWriter();
        code = await RunWithApiAsync([
            "request-forms", "list", "--service-id", "svc-1", "--json", "--view", "summary"
        ], request =>
        {
            Assert.Equal("https://api.example/api/v1/services/svc-1/request-forms", request.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, RequestFormArrayJson());
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("Production", item.GetProperty("releaseStatusName").GetString());
        Assert.False(item.TryGetProperty("jsonSchema", out _));
        Assert.False(item.TryGetProperty("jsonSchemaExcerpt", out _));
    }

    [Fact]
    public async Task CategoriesRoles_Project_And_FilterFields()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "categories", "list", "--json", "--view", "summary", "--fields", "id,name,type,typeName,typeLabel"
        ], _ => Json(HttpStatusCode.OK, CategoryArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using (var doc = JsonDocument.Parse(output.ToString()))
        {
            var item = doc.RootElement.GetProperty("items")[0];
            Assert.Equal(1, item.GetProperty("type").GetInt32());
            Assert.Equal("Incident", item.GetProperty("typeLabel").GetString());
            Assert.False(item.TryGetProperty("description", out _));
        }

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "roles", "list"
        ], _ => Json(HttpStatusCode.OK, RoleArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Contains("ROLES", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("HelpdeskAdmin", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connectivity_Projection_HidesDuplicateAliases_And_RawPreservesThem()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "connectivity", "orchestration-jobs", "--json", "--view", "summary"
        ], _ => Json(HttpStatusCode.OK, OrchestrationJobsArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using (var doc = JsonDocument.Parse(output.ToString()))
        {
            var item = doc.RootElement.GetProperty("items")[0];
            Assert.Equal(42, item.GetProperty("tenantId").GetInt32());
            Assert.Equal("Acme Tenant", item.GetProperty("tenantName").GetString());
            Assert.False(item.TryGetProperty("tenant_id", out _));
            Assert.False(item.TryGetProperty("orchestrationTenantName", out _));
        }

        output.GetStringBuilder().Clear();
        code = await RunWithApiAsync([
            "connectivity", "orchestration-jobs", "--json"
        ], _ => Json(HttpStatusCode.OK, OrchestrationJobsArrayJson()), output);

        Assert.Equal(CliExitCodes.Success, code);
        using var raw = JsonDocument.Parse(output.ToString());
        Assert.True(raw.RootElement[0].TryGetProperty("tenant_id", out _));
        Assert.True(raw.RootElement[0].TryGetProperty("orchestrationTenantName", out _));
    }

    [Fact]
    public async Task CapabilitiesSchemaAndEnums_AreDeterministic_AndSafe()
    {
        var output = new StringWriter();
        var code = await HelpdeskCli.RunAsync(["capabilities", "--json"], new CliRuntime { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        using (var doc = JsonDocument.Parse(output.ToString()))
        {
            Assert.False(doc.RootElement.GetProperty("profile").GetProperty("authConfigured").GetBoolean());
            Assert.Contains(doc.RootElement.GetProperty("commandGroups").EnumerateArray(), x => x.GetString() == "services");
            Assert.DoesNotContain("secret", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        output.GetStringBuilder().Clear();
        code = await HelpdeskCli.RunAsync(["schema", "connectivity", "orchestration-test", "--json"], new CliRuntime { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        using (var schema = JsonDocument.Parse(output.ToString()))
        {
            Assert.Equal("side-effecting", schema.RootElement.GetProperty("safety").GetString());
            Assert.Equal("POST", schema.RootElement.GetProperty("method").GetString());
        }

        output.GetStringBuilder().Clear();
        code = await HelpdeskCli.RunAsync(["enums", "ticket-state", "--json"], new CliRuntime { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        using var enums = JsonDocument.Parse(output.ToString());
        Assert.Equal("ticket-state", enums.RootElement.GetProperty("name").GetString());
        Assert.Contains(enums.RootElement.GetProperty("values").EnumerateArray(), x => x.GetProperty("name").GetString() == "InProgress" && x.GetProperty("value").GetInt32() == 3);
    }

    [Fact]
    public async Task Capabilities_reports_integration_credential_configuration_without_Authentik_fields()
    {
        var temp = Directory.CreateTempSubdirectory("helpdesk-cli-test-");
        var path = Path.Combine(temp.FullName, "cli.json");
        await File.WriteAllTextAsync(path, """{"apiBaseUrl":"https://api.example","credentialMode":"integration","integrationCredential":"rdk_test"}""");
        var output = new StringWriter();

        var code = await HelpdeskCli.RunAsync(["--config", path, "capabilities", "--json"], new CliRuntime { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        using var document = JsonDocument.Parse(output.ToString());
        var profile = document.RootElement.GetProperty("profile");
        Assert.Equal("integration", profile.GetProperty("credentialMode").GetString());
        Assert.True(profile.GetProperty("authConfigured").GetBoolean());
        Assert.DoesNotContain("rdk_test", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Integration_credential_mode_calls_api_directly_without_an_OIDC_token_exchange()
    {
        var temp = Directory.CreateTempSubdirectory("helpdesk-cli-test-");
        var path = Path.Combine(temp.FullName, "cli.json");
        await File.WriteAllTextAsync(path, """{"apiBaseUrl":"https://api.example","credentialMode":"integration","integrationCredential":"rdk_test"}""");
        var requests = new List<HttpRequestMessage>();
        var output = new StringWriter();
        var code = await HelpdeskCli.RunAsync(["--config", path, "health", "--json"], new CliRuntime(() => new RecordingHandler(request =>
        {
            requests.Add(CloneForAssert(request));
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        })) { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Equal(3, requests.Count);
        Assert.DoesNotContain(requests, request => request.RequestUri!.AbsolutePath.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Bearer rdk_test", requests[2].Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Runtime_does_not_reuse_an_integration_credential_after_configuration_changes()
    {
        var runtime = new CliRuntime();
        var first = new ResolvedCliConfig(new Uri("https://api.example"), null, null, null, null, null, null, "integration", "rdk_first");
        var second = first with { IntegrationCredential = "rdk_second" };

        Assert.Equal("rdk_first", await runtime.GetAccessTokenAsync(first));
        Assert.Equal("rdk_second", await runtime.GetAccessTokenAsync(second));
    }

    [Fact]
    public async Task IncidentsList_RequesterEmail_UsesExpectedQuery()
    {
        var output = new StringWriter();
        var code = await RunWithApiAsync([
            "incidents", "list", "--requester-email", "person@example.com", "--json", "--view", "summary"
        ], request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example/api/v1/incidents/?requesterEmail=person%40example.com", request.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, TicketPageJson("inc-1", "INC-001", "Network down", state: 3, priority: 3));
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
    }

    [Fact]
    public async Task IncidentCreate_ForEmailDryRun_ResolvesCustomer_And_DoesNotPost()
    {
        var output = new StringWriter();
        var calls = new List<HttpRequestMessage>();
        var code = await RunWithApiAsync([
            "incidents", "create",
            "--for-email", "person@example.com",
            "--title", "VPN down",
            "--description", "Cannot connect",
            "--priority", "High",
            "--dry-run",
            "--json"
        ], request =>
        {
            calls.Add(CloneForAssert(request));
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example/api/v1/global-search/customers?q=person%40example.com&pageSize=25", request.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, CustomerSearchJson("cust-1", "person@example.com", "org-1"));
        }, output);

        Assert.Equal(CliExitCodes.Success, code);
        Assert.Single(calls);
        using var doc = JsonDocument.Parse(output.ToString());
        var body = doc.RootElement.GetProperty("body");
        Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal("cust-1", body.GetProperty("customerId").GetString());
        Assert.Equal("org-1", body.GetProperty("organizationId").GetString());
        Assert.Equal("person@example.com", body.GetProperty("requesterEmail").GetString());
        Assert.Equal(2, body.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task IncidentCreate_ForEmail_FailsClearly_WhenCustomerMissing()
    {
        var errors = new StringWriter();
        var code = await HelpdeskCli.RunAsync(BaseArgs([
            "incidents", "create",
            "--for-email", "missing@example.com",
            "--title", "VPN down",
            "--description", "Cannot connect",
            "--dry-run"
        ]), new CliRuntime(() => new RecordingHandler(request => Task.FromResult(Json(
            HttpStatusCode.OK,
            request.RequestUri!.Host == "auth.example"
                ? """{"access_token":"minted-token"}"""
                : """{"items":[]}"""))))
        { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("No unique customer found", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncidentCreate_ForEmail_FailsClearly_WhenCustomerAmbiguous()
    {
        var errors = new StringWriter();
        var code = await HelpdeskCli.RunAsync(BaseArgs([
            "incidents", "create",
            "--for-email", "person@example.com",
            "--title", "VPN down",
            "--description", "Cannot connect",
            "--dry-run"
        ]), new CliRuntime(() => new RecordingHandler(request => Task.FromResult(Json(
            HttpStatusCode.OK,
            request.RequestUri!.Host == "auth.example"
                ? """{"access_token":"minted-token"}"""
                : CustomerSearchJson("cust-1", "person@example.com", "org-1", includeSecond: true)))))
        { Out = new StringWriter(), Error = errors });

        Assert.Equal(CliExitCodes.ValidationError, code);
        Assert.Contains("Multiple customers matched", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncidentsBulkCreate_DryRun_AcceptsItemsArray()
    {
        var temp = Directory.CreateTempSubdirectory("helpdesk-cli-bulk-");
        var file = Path.Combine(temp.FullName, "incidents.json");
        await File.WriteAllTextAsync(file, """
{
  "items": [
    {
      "title": "VPN down",
      "description": "Cannot connect",
      "customerId": "cust-1",
      "organizationId": "org-1",
      "priority": 2
    }
  ]
}
""");

        var output = new StringWriter();
        var code = await HelpdeskCli.RunAsync(BaseArgs([
            "incidents", "bulk-create", "--body-file", file, "--dry-run", "--json"
        ]), new CliRuntime(() => new RecordingHandler(_ => throw new InvalidOperationException("Dry-run should not call HTTP.")))
        { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("cust-1", doc.RootElement.GetProperty("items")[0].GetProperty("body").GetProperty("customerId").GetString());
    }

    [Fact]
    public async Task IncidentSchemaAndExamples_ExposeAgentCreateWorkflow()
    {
        var output = new StringWriter();
        var code = await HelpdeskCli.RunAsync(["schema", "incidents", "create", "--json"], new CliRuntime { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        using (var schema = JsonDocument.Parse(output.ToString()))
        {
            Assert.Equal("incidents create", schema.RootElement.GetProperty("commandPath").GetString());
            Assert.Contains(schema.RootElement.GetProperty("options").EnumerateArray(), x => x.GetString() == "--for-email");
            Assert.Contains(schema.RootElement.GetProperty("enumFields").EnumerateArray(), x => x.GetString() == "priority");
            Assert.Contains("Required fields", schema.RootElement.GetProperty("routeCaveats")[0].GetString(), StringComparison.Ordinal);
        }

        output.GetStringBuilder().Clear();
        code = await HelpdeskCli.RunAsync(["examples", "incident-create", "--json"], new CliRuntime { Out = output, Error = new StringWriter() });

        Assert.Equal(CliExitCodes.Success, code);
        using var examples = JsonDocument.Parse(output.ToString());
        Assert.Contains(examples.RootElement.GetProperty("examples").EnumerateArray(), x => x.GetProperty("command").GetString()!.Contains("incidents create --for-email", StringComparison.Ordinal));
    }

    private static async Task<int> RunWithApiAsync(string[] commandArgs, Func<HttpRequestMessage, HttpResponseMessage> apiResponder, StringWriter output)
    {
        var call = 0;
        return await HelpdeskCli.RunAsync(BaseArgs(commandArgs), new CliRuntime(() => new RecordingHandler(request =>
        {
            call++;
            return Task.FromResult(call == 1
                ? Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""")
                : apiResponder(request));
        }))
        { Out = output, Error = new StringWriter() });
    }

    private static string[] BaseArgs(string[] commandArgs) =>
    [
        "--api-base-url", "https://api.example",
        "--token-url", "https://auth.example/token",
        "--client-id", "client",
        "--username", "agent",
        "--app-password", "secret",
        .. commandArgs
    ];

    private static string TicketPageJson(string id, string trackingId, string subject, int state, int priority, int totalCount = 1, bool second = false, int? lifecycleState = null)
    {
        var lifecycle = lifecycleState.HasValue ? $@",""lifecycleState"":{lifecycleState.Value}" : string.Empty;
        var secondItem = second
            ? $@",
    {{
      ""id"": ""inc-2"",
      ""trackingId"": ""INC-002"",
      ""subject"": ""Email issue"",
      ""updatedAt"": ""2026-07-06T12:18:25Z"",
      ""customerOrgName"": ""OCS Solutions"",
      ""customerName"": """",
      ""requesterEmail"": """",
      ""state"": 1,
      ""priority"": 0,
      ""payloadJson"": ""{{}}"",
      ""messageHtml"": ""<p>hidden</p>""
    }}"
            : string.Empty;
        return $@"{{
  ""page"": 1,
  ""pageSize"": 2,
  ""totalCount"": {totalCount},
  ""items"": [
    {{
      ""id"": ""{id}"",
      ""trackingId"": ""{trackingId}"",
      ""subject"": ""{subject}"",
      ""updatedAt"": ""2026-07-06T13:18:25Z"",
      ""customerOrgName"": ""OCS Solutions"",
      ""customerName"": ""Anas"",
      ""requesterEmail"": ""anas@example.com"",
      ""state"": {state},
      ""priority"": {priority}{lifecycle},
      ""payloadJson"": ""{{}}"",
      ""messageHtml"": ""<p>hidden</p>"",
      ""sla"": {{ ""status"": 1 }}
    }}{secondItem}
  ]
}}";
    }

    private static string TicketArrayJson()
        => """
[
  {
    "id": "inc-1",
    "trackingId": "INC-001",
    "subject": "Network down",
    "updatedAt": "2026-07-06T13:18:25Z",
    "customerOrgName": "OCS Solutions",
    "customerName": "Anas",
    "requesterEmail": "anas@example.com",
    "state": 3,
    "priority": 3
  },
  {
    "id": "inc-2",
    "trackingId": "INC-002",
    "subject": "Email issue",
    "updatedAt": "2026-07-06T12:18:25Z",
    "customerOrgName": "OCS Solutions",
    "customerName": "",
    "requesterEmail": "",
    "state": 1,
    "priority": 0
  }
]
""";

    private static string RequestTaskPageJson()
        => """
{
  "page": 1,
  "pageSize": 2,
  "totalCount": 1,
  "items": [
    {
      "id": "task-1",
      "requestId": "req-1",
      "requestTrackingId": "REQ-001",
      "requestTitle": "New laptop",
      "name": "Manager approval",
      "type": 3,
      "status": 6,
      "assignedToId": "user-1",
      "assignedToDisplayName": "Pat",
      "dueAt": "2026-07-08T10:00:00Z",
      "orchestrationExternalRunId": "orchestration-run-1",
      "conditionExpression": "secret == true",
      "resultJson": "{\"ok\":true}"
    }
  ]
}
""";

    private static string NotificationPageJson()
        => """
{
  "page": 1,
  "pageSize": 2,
  "totalCount": 1,
  "items": [
    {
      "id": "00000000-0000-0000-0000-000000000001",
      "title": "Mail import failed",
      "message": "Full email body",
      "severity": 4,
      "createdUtc": "2026-07-06T13:18:25Z",
      "isRead": false,
      "source": "Mail",
      "category": "Inbound",
      "reference": "INC-001",
      "correlationId": "corr-1",
      "metadataJson": "{\"raw\":true}",
      "messageHtml": "<p>Full email body</p>"
    }
  ]
}
""";

    private static string NotificationDetailJson()
        => """
{
  "id": "00000000-0000-0000-0000-000000000001",
  "title": "Mail import failed",
  "message": "Full email body",
  "severity": 4,
  "createdUtc": "2026-07-06T13:18:25Z",
  "isRead": false,
  "source": "Mail",
  "category": "Inbound",
  "reference": "INC-001",
  "correlationId": "corr-1",
  "metadataJson": "{\"raw\":true}",
  "messageHtml": "<p>Full email body</p>"
}
""";

    private static string OrganizationArrayJson()
        => """
[
  {
    "id": "org-1",
    "name": "Acme",
    "dnsName": "acme.example",
    "isEnabled": true,
    "assignedSlaId": "sla-1",
    "contactInfo": "large support settings"
  },
  {
    "id": "org-2",
    "name": "Blocked Org",
    "isEnabled": false
  }
]
""";

    private static string CustomerArrayJson()
        => """
[
  {
    "id": "cust-1",
    "name": "Acme User",
    "email": "person@example.com",
    "organizationId": "org-1",
    "organizationName": "Acme",
    "isEnabled": true,
    "authStatus": { "authentikUserId": "secret" }
  },
  {
    "id": "cust-2",
    "name": "Other User",
    "email": "other@example.com",
    "organizationId": "org-2",
    "isEnabled": false
  }
]
""";

    private static string CustomerSearchJson(string id, string email, string organizationId, bool includeSecond = false)
    {
        var second = includeSecond
            ? $@",
    {{
      ""id"": ""cust-2"",
      ""name"": ""Second User"",
      ""email"": ""{email}"",
      ""organizationId"": ""org-2"",
      ""organizationName"": ""Other Org""
    }}"
            : string.Empty;
        return $@"{{
  ""items"": [
    {{
      ""id"": ""{id}"",
      ""name"": ""Person"",
      ""email"": ""{email}"",
      ""organizationId"": ""{organizationId}"",
      ""organizationName"": ""Acme""
    }}{second}
  ]
}}";
    }

    private static string UserJson(bool includePasswordHash)
        => $$"""
{
  "id": "user-1",
  "name": "Person Example",
  "email": "person@example.com",
  "role": "HelpdeskAdmin",
  "organizationId": "org-1",
  "organizationName": "Acme",
  "isTestUser": false{{(includePasswordHash ? "," : "")}}
  {{(includePasswordHash ? "\"passwordHash\": \"secret\"" : "")}}
}
""";

    private static string TimelineArrayJson()
        => """
[
  {
    "id": "event-1",
    "ticketId": "inc-1",
    "createdUtc": "2026-07-06T13:18:25Z",
    "eventType": 2,
    "createdByUserName": "System",
    "messageText": "Long diagnostic body",
    "messageHtml": "<p>hidden</p>",
    "payloadJson": "{\"secret\":true}"
  }
]
""";

    private static string WorklogArrayJson()
        => """
[
  {
    "id": "work-1",
    "ticketId": "chg-1",
    "createdAt": "2026-07-06T13:18:25Z",
    "technicianName": "Pat",
    "hours": 1.5,
    "notes": "Investigated root cause",
    "messageHtml": "<p>hidden</p>"
  }
]
""";

    private static string ServiceArrayJson()
        => """
[
  {
    "id": "svc-1",
    "name": "Laptop",
    "description": "Long onboarding service description",
    "parentServiceId": null,
    "allowedOrganizationIds": ["org-secret"],
    "allowedCustomerIds": ["cust-secret"],
    "depth": 0
  }
]
""";

    private static string RequestFormArrayJson()
        => """
[
  {
    "id": "form-1",
    "serviceId": "svc-1",
    "title": "Onboarding",
    "description": "Large form description",
    "jsonSchema": "{\"properties\":{\"secret\":true}}",
    "allowedOrganizationIds": ["org-secret"],
    "releaseStatus": 1
  }
]
""";

    private static string CategoryArrayJson()
        => """
[
  {
    "id": "cat-1",
    "name": "Network",
    "description": "Long category description",
    "type": 1,
    "parentCategoryId": "parent-1",
    "tenantId": "org-1",
    "sortOrder": 5,
    "isActive": true
  }
]
""";

    private static string RoleArrayJson()
        => """
[
  {
    "id": "role-1",
    "name": "HelpdeskAdmin",
    "description": "Can administer helpdesk"
  }
]
""";

    private static string OrchestrationJobsArrayJson()
        => """
[
  {
    "id": "job-1",
    "name": "Provision Laptop",
    "displayName": "Provision Laptop",
    "tenantId": 42,
    "tenantName": "Acme Tenant",
    "tenant_id": 42,
    "tenant_name": "Acme Tenant",
    "orchestrationTenantId": 42,
    "orchestrationTenantName": "Acme Tenant",
    "clientIdentity": "secret-client",
    "inputs": [{ "key": "password", "type": "secret" }]
  }
]
""";

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpRequestMessage CloneForAssert(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;
        if (request.Content is not null)
        {
            clone.Content = new StringContent(request.Content.ReadAsStringAsync().Result, Encoding.UTF8, "application/json");
        }

        return clone;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
