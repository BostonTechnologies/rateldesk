using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class ChatTraceSafetyTests
{
    [Theory]
    [InlineData("{\"command\":\"curl --password PRIVATE_VALUE https://example.com\"}")]
    [InlineData("{\"command\":\"curl -u user:PRIVATE_VALUE https://example.com\"}")]
    [InlineData("{\"api__key\":\"PRIVATE_VALUE\"}")]
    [InlineData("{\"pass-word\":\"PRIVATE_VALUE\"}")]
    [InlineData("{\"env\":{\"VALUE\":\"PRIVATE_VALUE\"}}")]
    public void Credential_flags_and_normalized_secret_keys_are_removed_before_storage(string payload)
    {
        var details = ChatTraceSafety.Extract(JsonSerializer.SerializeToElement(new { argumentsJson = payload }));
        Assert.True(details.Redacted);
        Assert.DoesNotContain("PRIVATE_VALUE", JsonSerializer.Serialize(details));
    }

    [Fact]
    public void Structured_payload_is_redacted_before_durable_serialization_and_keeps_source_time()
    {
        var frame = JsonSerializer.SerializeToElement(new { timestampMs = 1793606400000L,
            argumentsJson = """{"query":"safe","nested":{"api_key":"PRIVATE_A","authorization":"PRIVATE_B"},"command":"token=PRIVATE_C"}""",
            result = """{"ok":false,"password":"PRIVATE_D","text":"Bearer PRIVATE_E","url":"https://person:PRIVATE_F@example.com/"}""", toolFailureCode = "ToolDenied" });
        var details = ChatTraceSafety.Extract(frame);
        var stored = JsonSerializer.Serialize(details);
        Assert.DoesNotContain("PRIVATE_", stored);
        Assert.Contains("safe", stored);
        Assert.True(details.Redacted);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793606400000L), details.SourceTimestampUtc);
        Assert.Equal("ToolDenied", details.FailureCode);
    }

    [Theory]
    [InlineData("not JSON containing an unlabelled credential")]
    [InlineData("{broken")]
    [InlineData("{\"nested\":\"{\\\"password\\\":\\\"PRIVATE\\\"}\"}")]
    public void Unstructured_or_nested_encoded_payloads_fail_closed(string payload)
    {
        var details = ChatTraceSafety.Extract(JsonSerializer.SerializeToElement(new { result = payload, timestampMs = "malformed" }));
        Assert.True(details.Redacted);
        Assert.DoesNotContain("PRIVATE", details.Result);
        Assert.Null(details.SourceTimestampUtc);
    }

    [Fact]
    public void Oversized_and_deep_payloads_are_bounded_without_raw_fallback()
    {
        var large = ChatTraceSafety.Extract(JsonSerializer.SerializeToElement(new { argumentsJson = new string('s', 40000) }));
        Assert.True(large.Truncated);
        Assert.True(large.Arguments!.Length < 100);
        var deep = ChatTraceSafety.Extract(JsonSerializer.SerializeToElement(new { result = new string('[', 20) + "0" + new string(']', 20) }));
        Assert.True(deep.Redacted);
        Assert.True(deep.Result!.Length < 100);
    }

    [Fact]
    public void Optional_diagnostics_select_only_numeric_flags_and_never_private_thinking()
    {
        var details = ChatTraceSafety.Extract(JsonSerializer.SerializeToElement(new { inputTokens = 12, isProcessing = true, thinking = "PRIVATE", errorDetail = "PRIVATE", filePath = "PRIVATE", totalTokens = "PRIVATE" }));
        Assert.Contains("inputTokens", details.Measures);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(details));
    }
}
