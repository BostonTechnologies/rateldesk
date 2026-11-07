extern alias NewWeb;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using System.Text.Json;
using NewWeb::HelpDesk.NewWeb.Services;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class ChatPresentationTests
{
    [Fact]
    public void Child_followups_keep_native_boundaries_and_equal_replies_in_the_original_operator_turn()
    {
        var input = E(1, "operator");
        input.ClientMessageId = Guid.NewGuid();
        var first = Completion(3, input, 1, ["child-a", "child-b"]);
        var middle = Completion(5, input, 2, ["child-b"]);
        var final = Completion(7, input, 3, []);
        var events = new[] { input, E(2, "assistant"), first, E(4, "assistant"), middle, E(6, "assistant"), final };
        var pending = Assert.Single(AiAssistantChatPresentation.Build(events.Take(5)));
        Assert.False(pending.Completed);
        Assert.Equal(2, pending.Responses.Count);
        var reloaded = Assert.Single(AiAssistantChatPresentation.Build(events.Reverse().Concat(events)));
        Assert.True(reloaded.Completed);
        Assert.Equal(3, reloaded.Responses.Count);
        Assert.Equal(new[] { 1, 2, 3 }, reloaded.NativeTurns.Select(x => x.TurnNumber!.Value));
    }

    [Fact]
    public void Parent_completion_does_not_close_an_actionable_child_approval()
    {
        var input = E(1, "operator");
        input.ClientMessageId = Guid.NewGuid();
        var turn = Assert.Single(AiAssistantChatPresentation.Build([
            input, E(2, "approval_request", "child-consent"), Completion(3, input, 1, ["child-a"]) ]));
        Assert.False(turn.Completed);
        Assert.True(turn.HasPendingApproval);
        Assert.True(turn.InitiallyExpanded);
    }

    [Fact]
    public void Native_failure_is_preserved_even_without_an_error_frame()
    {
        var input = E(1, "operator");
        input.ClientMessageId = Guid.NewGuid();
        var failed = Completion(2, input, 1, ["child-a"], "failed");
        var turn = Assert.Single(AiAssistantChatPresentation.Build([input, failed]));
        Assert.True(turn.HasError);
        Assert.False(turn.Completed);
        Assert.True(turn.InitiallyExpanded);
    }

    private static AiAssistantChatEvent Completion(long sequence, AiAssistantChatEvent input, int ordinal, string[] children, string outcome = "completed")
    {
        var completed = E(sequence, "turn_completed");
        completed.MetadataJson = JsonSerializer.Serialize(new ChatTurnCompletion(input.ClientMessageId!.Value, ordinal, outcome, false, children));
        return completed;
    }

    [Fact]
    public void Sequence_boundaries_pair_calls_and_ignore_duplicate_replay()
    {
        var events = new[] { E(1, "created"), E(2, "operator"), E(3, "tool_call", "a"), E(4, "tool_result", "a"),
            E(5, "assistant"), E(6, "turn_completed"), E(7, "recovery_inconclusive"), E(8, "operator") };
        var turns = AiAssistantChatPresentation.Build(events.Reverse().Concat(events));
        Assert.Equal(2, turns.Count);
        var operation = Assert.Single(turns[0].Operations);
        Assert.Equal(3, operation.Call!.Sequence);
        Assert.Equal(4, operation.Result!.Sequence);
        Assert.Single(turns[0].Responses);
        Assert.True(turns[0].Completed);
        Assert.False(turns[0].InitiallyExpanded);
        Assert.True(turns[1].InitiallyExpanded);
        Assert.Empty(turns[0].Activity);
    }

    [Fact]
    public void Metadata_retains_only_known_identifier_and_not_other_payload_fields()
    {
        const string raw = "{\"name\":\"linux-admin\",\"task\":\"secret shell payload\",\"arguments\":{\"token\":\"sensitive\"}}";
        var metadata = ChatActivitySafety.Extract("skill_load", raw, false, false, null);
        Assert.Equal("linux-admin", metadata.SkillName);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(metadata));
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(metadata));
        var mcp = ChatActivitySafety.Extract("provider/terminal_execute", raw, true, false, 1851);
        Assert.Equal("provider", mcp.ProviderName);
        Assert.Equal("terminal_execute", mcp.DisplayName);
        Assert.Equal("mcp", mcp.ToolKind);
        Assert.Equal(1851, mcp.DurationMs);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("Name")]
    public void Skill_summary_recognizes_native_name_casing(string field)
    {
        var metadata = ChatActivitySafety.Extract("skill_load", JsonSerializer.Serialize(new Dictionary<string, string> { [field] = "example-skill" }), false, false, null);
        Assert.Equal("example-skill", metadata.SkillName);
    }

    [Theory]
    [InlineData("{\"name\":\"/etc/secrets\"}")]
    [InlineData("{\"name\":\"token=secret\"}")]
    [InlineData("{\"name\":\"valid\",\"name\":\"other\"}")]
    [InlineData("{\"name\":\"valid\\n\"}")]
    [InlineData("not json")]
    public void Unsafe_or_ambiguous_skill_names_are_not_retained(string raw)
        => Assert.Null(ChatActivitySafety.Extract("skill_load", raw, false, false, null).SkillName);

    [Fact]
    public void Completed_result_uses_call_identity_and_counts_one_operation()
    {
        var call = E(2, "tool_call", "one");
        call.MetadataJson = JsonSerializer.Serialize(ChatActivitySafety.Extract("skill_load", "{\"name\":\"helpdesk\"}", false, false, null));
        var result = E(3, "tool_result", "one");
        result.MetadataJson = JsonSerializer.Serialize(ChatActivitySafety.Extract("skill_load", null, true, true, 1000));
        var turn = Assert.Single(AiAssistantChatPresentation.Build([E(1, "operator"), call, result, E(4, "turn_completed")]));
        Assert.Equal("helpdesk", Assert.Single(turn.Operations).Metadata!.DisplayName);
        Assert.Contains("1 operations", turn.Summary);
        Assert.Contains("1 skills", turn.Summary);
        Assert.True(turn.InitiallyExpanded);
    }

    private static AiAssistantChatEvent E(long sequence, string type, string? callId = null)
        => new() { Sequence = sequence, Type = type, CallId = callId, CreatedUtc = DateTimeOffset.UnixEpoch };
}
