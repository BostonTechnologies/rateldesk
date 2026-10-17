using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Mcp.Http.Authorization;
using Helpdesk.Mcp.Http.Observability;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class SetupVerificationAuthorizationTests
{
    private static readonly ClaimsPrincipal Verifier = new(new ClaimsIdentity([new Claim("setup_verification", "true")], "Test"));
    private static readonly ClaimsPrincipal Ordinary = new(new ClaimsIdentity([], "Test"));

    [Theory]
    [InlineData("helpdesk_incidents", "{\"operation\":\"list\",\"request\":{\"query\":\"private-ticket-marker\"}}")]
    [InlineData("helpdesk_config", "{\"operation\":\"show\"}")]
    [InlineData("helpdesk_capabilities", "{\"operation\":\"get\"}")]
    [InlineData("helpdesk_auth", "null")]
    [InlineData("helpdesk_auth", "{}")]
    [InlineData("helpdesk_auth", "{\"operation\":null}")]
    [InlineData("helpdesk_auth", "{\"operation\":\"other\"}")]
    [InlineData("helpdesk_auth", "{\"operation\":\"status\",\"confirm\":true}")]
    [InlineData("helpdesk_auth", "{\"operation\":\"status\",\"confirm\":\"false\"}")]
    [InlineData("helpdesk_auth", "{\"operation\":\"status\",\"request\":{}}")]
    [InlineData("helpdesk_auth", "{\"operation\":\"status\",\"setup_verification\":false}")]
    public async Task Denied_call_has_a_safe_known_failure_and_never_invokes_the_next_handler(string tool, string argumentsJson)
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
        var invocations = 0;
        var result = await SetupVerificationAuthorization.InvokeToolAsync(Verifier, tool, arguments, _ =>
        {
            invocations++;
            throw new InvalidOperationException("A denied request reached its tool handler.");
        }, default);

        Assert.Equal(0, invocations);
        Assert.True(result.IsError);
        var contract = result.StructuredContent!.Value;
        Assert.False(contract.GetProperty("success").GetBoolean());
        Assert.Equal("forbidden", contract.GetProperty("status").GetString());
        Assert.Equal("setup_verification_operation_denied", contract.GetProperty("failure").GetProperty("code").GetString());
        Assert.False(contract.GetProperty("failure").GetProperty("retryable").GetBoolean());
        Assert.Null(contract.GetProperty("data").Deserialize<object>());
        Assert.Contains("ordinary client", Assert.Single(contract.GetProperty("nextActions").EnumerateArray()).GetString());
        var fallback = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(contract.GetRawText()), JsonNode.Parse(fallback)));
        Assert.DoesNotContain("private-ticket-marker", fallback, StringComparison.Ordinal);
        var outcome = McpToolInvocationAudit.DescribeOutcome(result);
        Assert.True(outcome.ProtocolSucceeded);
        Assert.False(outcome.OperationSucceeded);
    }

    [Theory]
    [InlineData(false, "helpdesk_incidents", "{\"operation\":\"list\"}")]
    [InlineData(false, "helpdesk_auth", "{\"operation\":\"status\",\"setup_verification\":true}")]
    [InlineData(true, "helpdesk_auth", "{\"operation\":\"status\"}")]
    [InlineData(true, "helpdesk_auth", "{\"operation\":\"status\",\"confirm\":false,\"request\":null}")]
    public async Task Ordinary_calls_and_the_exact_verifier_status_contract_retain_the_next_result(bool verifier, string tool, string argumentsJson)
    {
        var expected = new CallToolResult();
        var invocations = 0;
        var result = await SetupVerificationAuthorization.InvokeToolAsync(verifier ? Verifier : Ordinary, tool,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson), _ =>
            {
                invocations++;
                return ValueTask.FromResult(expected);
            }, default);
        Assert.Same(expected, result);
        Assert.Equal(1, invocations);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("json")]
    [InlineData("timeout")]
    [InlineData("sdk")]
    [InlineData("unexpected")]
    public async Task Unrelated_errors_are_never_reclassified_as_a_known_denial(string kind)
    {
        Exception expected = kind switch
        {
            "network" => new HttpRequestException("Synthetic network failure."),
            "json" => new JsonException("Synthetic invalid JSON."),
            "timeout" => new TimeoutException("Synthetic timeout."),
            "sdk" => new McpException("Synthetic SDK error."),
            _ => new InvalidOperationException("Synthetic unexpected result.")
        };
        var arguments = new Dictionary<string, JsonElement> { ["operation"] = JsonSerializer.SerializeToElement("status") };
        var actual = await Assert.ThrowsAnyAsync<Exception>(async () => await SetupVerificationAuthorization.InvokeToolAsync(
            Verifier, "helpdesk_auth", arguments, _ => ValueTask.FromException<CallToolResult>(expected), default));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Canceled_request_is_not_reported_as_a_known_denial()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invocations = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await SetupVerificationAuthorization.InvokeToolAsync(
            Verifier, "helpdesk_incidents", null, _ =>
            {
                invocations++;
                return ValueTask.FromResult(new CallToolResult());
            }, cancellation.Token));
        Assert.Equal(0, invocations);
    }
}
