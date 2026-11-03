using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class ChatTraceSafetyTests
{
    [Theory]
    [InlineData("Plain text tool result\nSecond line 😀")]
    [InlineData("# Skill instructions\nUse `example` and inspect the result.")]
    [InlineData("{broken JSON remains inspectable")]
    [InlineData("{\"Name\":\"linux-admin\",\"_rationale\":\"Review token usage and authentication\"}")]
    [InlineData("{\"nested\":\"{\\\"name\\\":\\\"example\\\"}\"}")]
    [InlineData("<script>alert('inert')</script>")]
    [InlineData("")]
    public void Received_payloads_survive_extraction_and_durable_serialization_exactly(string payload)
    {
        var frame = JsonSerializer.SerializeToElement(new { argumentsJson = payload, result = payload,
            toolName = "provider/skill_load", toolFailureCode = "Actual reported failure detail", timestampMs = 1793606400000L });
        var details = ChatTraceSafety.Extract(frame);
        var reloaded = JsonSerializer.Deserialize<ChatTraceDetails>(JsonSerializer.Serialize(details))!;
        Assert.Equal(payload, reloaded.Arguments);
        Assert.Equal(payload, reloaded.Result);
        Assert.Equal("provider/skill_load", reloaded.ToolName);
        Assert.Equal("Actual reported failure detail", reloaded.FailureCode);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793606400000L), reloaded.SourceTimestampUtc);
        Assert.False(reloaded.Redacted);
        Assert.False(reloaded.Truncated);
        Assert.Equal(2, reloaded.Version);
    }

    [Fact]
    public void Large_and_deep_results_are_retained_without_format_or_size_based_omission()
    {
        var payload = new string('[', 40) + JsonSerializer.Serialize(new string('s', 200000) + "😀") + new string(']', 40);
        var details = ChatTraceSafety.Extract(JsonSerializer.SerializeToElement(new { result = payload }));
        Assert.Equal(payload, details.Result);
        Assert.False(details.Truncated);
        Assert.False(details.Redacted);
    }

    [Fact]
    public void Absent_payloads_remain_absent_and_malformed_source_time_is_not_invented()
    {
        var details = ChatTraceSafety.Extract(JsonSerializer.SerializeToElement(new { timestampMs = "malformed" }));
        Assert.Null(details.Arguments);
        Assert.Null(details.Result);
        Assert.Null(details.ToolName);
        Assert.Null(details.SourceTimestampUtc);
    }

    [Fact]
    public void Optional_diagnostics_select_only_numeric_flags_and_never_private_thinking()
    {
        var details = ChatTraceSafety.Extract(JsonSerializer.SerializeToElement(new { inputTokens = 12, isProcessing = true, thinking = "PRIVATE", errorDetail = "PRIVATE", filePath = "PRIVATE", totalTokens = "PRIVATE" }));
        Assert.Contains("inputTokens", details.Measures);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(details));
    }
}
