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
