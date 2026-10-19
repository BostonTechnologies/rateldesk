using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using Json.Schema;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class OperatorAdapterTests
{
    [Fact]
    public async Task Form_schema_and_binding_edits_preserve_explicit_false_and_omitted_audience()
    {
        var (client, tools) = Create();
        using var form = JsonDocument.Parse("""{"requestFormId":"form/a","body":{"title":"Reviewed form","schema":{"fields":[],"tasks":[{"id":"11111111-1111-1111-1111-111111111111","name":"Manual launch","type":"automation","autoStart":false,"dependsOn":[]}]}}}""");
        Assert.True((await tools.helpdesk_automation("update_form", form.RootElement, true)).Success);
        var forwarded = client.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "SendAsync").GetArguments();
        Assert.Equal("/api/v1/request-forms/form%2Fa", forwarded[1]);
        var body = Assert.IsType<JsonObject>(forwarded[2]);
        Assert.Null(body["schema"]);
        Assert.False(body.ContainsKey("allowedOrganizationIds"));
        using var schema = JsonDocument.Parse(body["jsonSchema"]!.GetValue<string>());
        Assert.False(schema.RootElement.GetProperty("tasks")[0].GetProperty("autoStart").GetBoolean());

        using var binding = JsonDocument.Parse("""{"bindingId":"binding/a","body":{"enabled":false,"clearOrchestrationJobDefinitionId":true}}""");
        Assert.True((await tools.helpdesk_automation("update_binding", binding.RootElement, true)).Success);
        await client.Received(1).SendAsync(HttpMethod.Put, "/api/v1/admin/orchestration/bindings/binding%2Fa",
            Arg.Is<JsonNode?>(value => value != null && !value["enabled"]!.GetValue<bool>() && value["clearOrchestrationJobDefinitionId"]!.GetValue<bool>()), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("100e-2")]
    public async Task Mathematically_integral_numbers_match_discovery_and_are_normalized_for_the_typed_api(string number)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("{\"bindingId\":\"binding-1\",\"body\":{\"syncState\":" + number + ",\"enabled\":false}}");
        var definition = HelpdeskToolDefinitions.Create().Single(tool => tool.ProtocolTool.Name == "helpdesk_automation");
        var arguments = new JsonObject { ["operation"] = "update_binding", ["request"] = JsonNode.Parse(request.RootElement.GetRawText()), ["confirm"] = true };
        Assert.True(JsonSchema.FromText(definition.ProtocolTool.InputSchema.GetRawText()).Evaluate(arguments).IsValid);

        var result = await tools.helpdesk_automation("update_binding", request.RootElement, true);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).SendAsync(HttpMethod.Put, "/api/v1/admin/orchestration/bindings/binding-1",
            Arg.Is<JsonNode?>(body => body != null && body["syncState"]!.ToJsonString() == "1"), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1e-100")]
    [InlineData("2147483648")]
    public async Task Nonintegral_underflow_and_out_of_range_integer_inputs_do_not_reach_the_api(string number)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("{\"body\":{\"enabled\":false,\"expectedRevision\":" + number + "}}");
        var result = await tools.helpdesk_connections("update_netclaw_settings", request.RootElement, true);
        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Calendar_dates_durations_and_string_tenant_scope_match_the_canonical_wire_types()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"body":{"name":"Business calendar","timeZoneId":"UTC","weeklyRules":[{"dayOfWeek":1,"isWorkingDay":true,"workingHours":[{"start":"09:00:00","end":"17:00:00"}]}],"exceptions":[{"date":"2027-01-01","isWorkingDayOverride":false}]}}""");
        var result = await tools.helpdesk_sla("create_calendar", request.RootElement, true);
        Assert.True(result.Success, result.Summary);
        using var tenant = JsonDocument.Parse("""{"tenantId":"organization/a"}""");
        Assert.True((await tools.helpdesk_sla("tenant_settings", tenant.RootElement)).Success);
        await client.Received(1).GetAsync("/api/v1/tenant-sla-settings/organization%2Fa", true, Arg.Any<CancellationToken>());

        var malformed = JsonNode.Parse(request.RootElement.GetRawText())!;
        malformed["body"]!["weeklyRules"]![0]!["workingHours"]![0]!["start"] = "tomorrow morning";
        client.ClearReceivedCalls();
        using var invalid = JsonDocument.Parse(malformed.ToJsonString());
        Assert.Equal("validation_failed", (await tools.helpdesk_sla("create_calendar", invalid.RootElement, true)).Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dataset_rows_preserve_bounded_user_defined_columns_and_reject_excessive_nesting()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"datasetId":"dataset-1","body":{"deleteMissing":false,"rows":[{"externalKey":"server-1","data":{"name":"Example server","capacity":12.5,"magnitude":1e100,"active":true,"tags":["web","test"],"optional":null}}]}}""");
        Assert.True((await tools.helpdesk_datasets("upsert_rows", request.RootElement, true)).Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/resources/datasets/dataset-1/rows",
            Arg.Is<JsonNode?>(body => body != null && body["rows"]![0]!["data"]!["capacity"]!.ToJsonString() == "12.5" &&
                body["rows"]![0]!["data"]!["magnitude"]!.ToJsonString() == "1e100" && body["rows"]![0]!["data"]!["tags"]!.AsArray().Count == 2), true, Arg.Any<CancellationToken>());
        JsonNode nested = JsonValue.Create("value")!;
        for (var index = 0; index < 8; index++) nested = new JsonObject { ["child"] = nested };
        var malformed = JsonNode.Parse(request.RootElement.GetRawText())!;
        malformed["body"]!["rows"]![0]!["data"]!["deep"] = nested;
        using var invalid = JsonDocument.Parse(malformed.ToJsonString());
        Assert.Equal("validation_failed", (await tools.helpdesk_datasets("upsert_rows", invalid.RootElement, true)).Status);
    }

    [Fact]
    public async Task Configuration_results_redact_submitted_secret_echoes_and_stored_secret_fields()
    {
        var (client, tools) = Create();
        const string secret = "synthetic-device-secret";
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["message"] = "Server echoed " + secret, ["deviceToken"] = "stored-device-secret", ["clientSecret"] = "stored-peer-secret", ["hasDeviceToken"] = true });
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { body = new { enabled = false, deviceToken = secret } }));
        var result = await tools.helpdesk_connections("update_netclaw_settings", request.RootElement, true);
        Assert.True(result.Success);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(secret, serialized);
        Assert.DoesNotContain("stored-device-secret", serialized);
        Assert.DoesNotContain("stored-peer-secret", serialized);
        Assert.True(result.Data!["hasDeviceToken"]!.GetValue<bool>());
        Assert.Contains("[redacted]", serialized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Configuration_remote_and_local_validation_errors_do_not_echo_submitted_secrets(bool remote)
    {
        var (client, tools) = Create();
        const string secret = "synthetic-device-secret";
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<JsonNode?>(remote
                ? new AgentClientRemoteException("failure-" + secret, "Rejected " + secret, 503, null) { CorrelationId = secret }
                : new AgentClientValidationException("Invalid " + secret)));
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { body = new { enabled = false, deviceToken = secret } }));
        var result = await tools.helpdesk_connections("update_netclaw_settings", request.RootElement, true);
        Assert.False(result.Success);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task One_time_account_result_allows_only_its_new_root_activation_token()
    {
        var (client, tools) = Create();
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject
            {
                ["activationToken"] = "new-private-activation", ["plainTextKey"] = "unrelated-existing-key",
                ["password"] = "unrelated-password", ["nested"] = new JsonObject { ["activationToken"] = "existing-nested-token" },
                ["message"] = "New token new-private-activation; old token existing-nested-token",
                ["correlationId"] = "new-private-activation"
            });
        using var request = JsonDocument.Parse("""{"body":{"displayName":"Synthetic user","email":"operator@example.test"}}""");
        Assert.Equal("confirmation_required", (await tools.helpdesk_tenant_admin("create_local_account", request.RootElement)).Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        var result = await tools.helpdesk_tenant_admin("create_local_account", request.RootElement, true);
        Assert.Equal("new-private-activation", result.Data!["activationToken"]!.GetValue<string>());
        Assert.True(result.Data["sensitiveResult"]!.GetValue<bool>());
        Assert.Equal("[redacted]", result.CorrelationId);
        Assert.Equal("New token [redacted]; old token [redacted]", result.Data["message"]!.GetValue<string>());
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("unrelated-existing-key", serialized);
        Assert.DoesNotContain("unrelated-password", serialized);
        Assert.DoesNotContain("existing-nested-token", serialized);
    }

    private static (IHelpdeskAgentClient Client, HelpdeskTools Tools) Create()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(AgentClientConfiguration.Empty with { ApiBaseUrl = "https://api.example.test", CredentialMode = "gateway" });
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return (client, new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
    }
}
