using System.Text.Json;
using Helpdesk.Mcp.Tools;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed partial class McpHttpHostTests
{
    [Fact]
    public async Task Beta9_HTTP_rejects_unknown_envelopes_for_every_catalog_operation_before_upstream_IO()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var http = CreateClient(factory);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        var tools = HelpdeskToolDefinitions.Create();
        var id = 1;
        foreach (var operation in McpOperationCatalog.All)
        {
            var arguments = McpExecutableSchemaTests.ArgumentsFromDiscovery(tools.Single(x => x.ProtocolTool.Name == operation.Tool).ProtocolTool.InputSchema, operation.Operation);
            arguments["unexpectedOuterArgument"] = "must fail before binding";
            using var response = await HttpCallAsync(http, bearer, id++, "tools/call", ToolCallParameters(operation.Tool, arguments));
            AssertRejected(response);
        }
        foreach (var raw in new[]
        {
            "{\"operation\":\"status\",\"operation\":\"status\"}",
            "{\"operation\":\"status\",\"confirm\":false,\"confirm\":true}",
            "{\"operation\":\"status\",\"request\":{}}",
            "{\"operation\":\"status\",\"confirm\":\"true\"}",
            "{}"
        })
        {
            using var args = JsonDocument.Parse(raw);
            using var response = await HttpCallAsync(http, bearer, id++, "tools/call", ToolCallParameters("helpdesk_auth", args.RootElement));
            AssertRejected(response);
        }
        Assert.Equal(0, outbound.ApiRequests);
        Assert.Equal(0, outbound.TokenRequests);
        Beta9Evidence.Record("MCP-B8-UNKNOWN-OUTER-ARGUMENT", "http-all-operations", new
        {
            rejectedCatalogOperations = McpOperationCatalog.All.Count, rejectedAdditionalEnvelopes = 5,
            upstreamApiRequests = outbound.ApiRequests, upstreamTokenRequests = outbound.TokenRequests
        });
    }

    [Fact]
    public async Task Beta9_stdio_rejects_unknown_and_duplicate_outer_fields()
    {
        await using var stdio = StdioMcpClient.Start();
        using var initialize = await stdio.CallAsync(1, "initialize", InitializeParameters());
        await stdio.NotifyAsync("notifications/initialized", new { });
        foreach (var raw in new[] { "{\"operation\":\"get\",\"extra\":true}", "{\"operation\":\"get\",\"operation\":\"get\"}" })
        {
            using var args = JsonDocument.Parse(raw);
            using var response = await stdio.CallAsync(2, "tools/call", ToolCallParameters("helpdesk_capabilities", args.RootElement));
            AssertRejected(response);
        }
    }

    [Fact]
    public async Task Duplicate_params_arguments_return_structured_validation_on_http_and_stdio_without_business_IO()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var http = CreateClient(factory);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        await using var stdio = StdioMcpClient.Start();
        using var initialized = await stdio.CallAsync(1, "initialize", InitializeParameters());
        await stdio.NotifyAsync("notifications/initialized", new { });

        const string raw = "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":{\"name\":\"helpdesk_notifications\",\"arguments\":{\"operation\":\"summary\"},\"arguments\":{\"operation\":\"mark_read\",\"request\":{\"id\":\"synthetic\"},\"confirm\":true}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var httpResponse = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, httpResponse.StatusCode);
        using var httpResult = ParseMcpResponse(await httpResponse.Content.ReadAsStringAsync());
        using var stdioResult = await stdio.CallRawAsync(raw);

        foreach (var result in new[] { httpResult, stdioResult })
        {
            var content = result.RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(42, result.RootElement.GetProperty("id").GetInt32());
            Assert.Equal("validation_failed", content.GetProperty("status").GetString());
            Assert.Equal("validation_error", content.GetProperty("failure").GetProperty("code").GetString());
            Assert.True(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
        Assert.Equal(0, outbound.ApiRequests);
        Assert.Equal(0, outbound.TokenRequests);
    }

    [Fact]
    public async Task Duplicate_params_arguments_rejects_after_gateway_authentication_without_execution_IO()
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound, delegationProbe: delegation);
        using var http = CreateClient(factory);
        const string raw = "{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"tools/call\",\"params\":{\"name\":\"helpdesk_notifications\",\"arguments\":{\"operation\":\"summary\"},\"arguments\":{\"operation\":\"mark_read\",\"confirm\":true}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "rdk_local-a");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        using var response = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var result = ParseMcpResponse(await response.Content.ReadAsStringAsync());
        var content = result.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(43, result.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("validation_failed", content.GetProperty("status").GetString());
        Assert.Equal(1, delegation.Requests);
        Assert.Equal(0, outbound.ApiRequests);
        Assert.Equal(0, outbound.TokenRequests);
    }

    [Fact]
    public async Task Beta9_HTTP_MCP_reads_prometheus_metrics_as_structured_text()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var http = CreateClient(factory);
        using var response = await HttpCallAsync(http, CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]), 1,
            "tools/call", ToolCallParameters("helpdesk_automation", new { operation = "workflow_metrics" }));
        var result = StructuredContent(response.RootElement);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("prometheus", result.GetProperty("data").GetProperty("format").GetString());
        Assert.Contains("helpdesk_tasks_in_progress 2", result.GetProperty("data").GetProperty("text").GetString());
        Assert.Equal(1, outbound.ApiRequests);
        Beta9Evidence.Record("MCP-B8-WORKFLOW-METRICS", "http-mcp", new { structuredContent = result.Clone(), upstreamRequests = outbound.ApiRequests });
    }

    private static void AssertRejected(JsonDocument response)
    {
        Assert.True(response.RootElement.TryGetProperty("error", out _) ||
            response.RootElement.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean(), response.RootElement.GetRawText());
    }
}
