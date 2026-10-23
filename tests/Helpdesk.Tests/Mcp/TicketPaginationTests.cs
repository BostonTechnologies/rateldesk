using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using Json.Schema;
using Microsoft.AspNetCore.WebUtilities;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class TicketPaginationTests
{
    private static readonly string[] Tools = ["helpdesk_incidents", "helpdesk_requests", "helpdesk_changes", "helpdesk_request_tasks"];

    [Theory]
    [InlineData("helpdesk_incidents")]
    [InlineData("helpdesk_requests")]
    [InlineData("helpdesk_changes")]
    [InlineData("helpdesk_request_tasks")]
    public async Task Campaign_invalid_pagination_is_rejected_before_business_IO(string tool)
    {
        var tools = CreateTools(out var client);
        var result = await List(tools, tool, JsonSerializer.Deserialize<JsonElement>("""{"page":0,"pageSize":10000}"""));
        Assert.False(result.Success);
        Assert.Equal("validation_failed", result.Status);
        Assert.Contains("request.page", result.Summary, StringComparison.Ordinal);
        Assert.NotNull(result.Failure);
        Assert.Empty(client.ReceivedCalls());
    }

    public static IEnumerable<object[]> PaginationCases()
    {
        foreach (var tool in Tools)
        {
            yield return [tool, "{}", true, ""];
            yield return [tool, """{"Page":0,"PAGESIZE":10000}""", false, "Page"];
            yield return [tool, """{"page":1,"PAGE":0}""", false, "PAGE"];
            yield return [tool, """{"pageSize":1,"PageSize":10000}""", false, "PageSize"];
            foreach (var field in new[] { "page", "pageSize" })
            {
                foreach (var value in new[] { "1", "1.0", "1e0", "1e+0", "10e-1", "100e-2", "0.1e1", "1.5e1", "100", "100.0", "1e2" })
                    yield return [tool, "{\"" + field + "\":" + value + "}", true, field];
                if (field == "page")
                    foreach (var value in new[] { "101", "10000", "1000000", "1e6" })
                        yield return [tool, "{\"page\":" + value + "}", true, field];
                foreach (var value in new[] { "-1", "0", "null", "\"1\"", "true", "false", "[]", "{}", "1.5", "1e-1", "2147483648", "9223372036854775808" })
                    yield return [tool, "{\"" + field + "\":" + value + "}", false, field];
                foreach (var value in field == "page" ? new[] { "1000001", "1000000.1" } : new[] { "101", "10000", "100.1" })
                    yield return [tool, "{\"" + field + "\":" + value + "}", false, field];
            }
        }
    }

    [Theory]
    [MemberData(nameof(PaginationCases))]
    public async Task Pagination_matches_executable_schema_and_adapter_without_mutating_input(string tool, string json, bool valid, string field)
    {
        var schema = JsonSchema.FromText(HelpdeskToolDefinitions.Create().Single(item => item.ProtocolTool.Name == tool).ProtocolTool.InputSchema.GetRawText());
        var arguments = JsonNode.Parse("{\"operation\":\"list\",\"request\":" + json + "}");
        Assert.Equal(valid, schema.Evaluate(arguments).IsValid);
        var tools = CreateTools(out var client);
        var request = JsonSerializer.Deserialize<JsonElement>(json);
        var original = request.GetRawText();
        var result = await List(tools, tool, request);
        Assert.Equal(original, request.GetRawText());
        Assert.Equal(valid, result.Success);
        if (!valid)
        {
            Assert.Equal("validation_failed", result.Status);
            Assert.Contains("request." + field, result.Summary, StringComparison.Ordinal);
            Assert.NotNull(result.Failure);
            Assert.Equal("validation_error", result.Failure.Code);
            Assert.False(result.Failure.Retryable);
            Assert.Empty(client.ReceivedCalls());
            return;
        }
        var path = Assert.IsType<string>(Assert.Single(client.ReceivedCalls()).GetArguments()[0]);
        var query = QueryHelpers.ParseQuery(new Uri("https://api.example.test" + path).Query);
        foreach (var name in new[] { "page", "pageSize" })
        {
            if (request.TryGetProperty(name, out var value))
                Assert.Equal(value.GetDouble().ToString("0", System.Globalization.CultureInfo.InvariantCulture), query[name].ToString());
            else Assert.False(query.ContainsKey(name));
        }
    }

    [Theory]
    [InlineData("page")]
    [InlineData("pageSize")]
    public async Task Duplicate_properties_and_fractional_remainders_reject_before_parsing_or_IO(string field)
    {
        // Duplicate members are outside JSON Schema's object data model. Enforce
        // their rejection on the raw request before JsonObject materialization.
        foreach (var tool in Tools)
        foreach (var json in new[] { "{\"" + field + "\":1,\"" + field + "\":1}", "{\"" + field + "\":1,\"" + field + "\":null}", "{\"" + field + "\":1.000000000000000000000000000001}", "{\"" + field + "\":1e9999999999999999999999}", "{\"" + field + "\":1e-9999999999999999999999}" })
        {
            var tools = CreateTools(out var client);
            var request = JsonSerializer.Deserialize<JsonElement>(json);
            var result = await List(tools, tool, request);
            Assert.Equal("validation_failed", result.Status);
            Assert.Contains("request." + field, result.Summary, StringComparison.Ordinal);
            Assert.Equal(json, request.GetRawText());
            Assert.Empty(client.ReceivedCalls());
        }
    }

    private static HelpdeskTools CreateTools(out IHelpdeskAgentClient client)
    {
        client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"pagination-{Guid.NewGuid():N}.json")));
    }

    private static Task<HelpdeskToolResponse> List(HelpdeskTools tools, string tool, JsonElement request) => tool switch
    {
        "helpdesk_incidents" => tools.helpdesk_incidents("list", request),
        "helpdesk_requests" => tools.helpdesk_requests("list", request),
        "helpdesk_changes" => tools.helpdesk_changes("list", request),
        _ => tools.helpdesk_request_tasks("list", request)
    };
}
