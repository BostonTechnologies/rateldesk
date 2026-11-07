using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class ChatContinuationSafetyTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"openChildRunIds\":null}")]
    public void Legacy_missing_children_remains_unknown_instead_of_proving_completion(string json)
    {
        var completion = ChatContinuationSafety.Parse(JsonDocument.Parse(json).RootElement, Guid.NewGuid(), null);
        Assert.Null(completion.OpenChildRunIds);
        Assert.False(completion.ChildSnapshotKnown);
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData("skipped", true)]
    public void Native_outcome_and_children_are_independent(string outcome, bool quiet)
    {
        var message = Guid.NewGuid();
        var completion = ChatContinuationSafety.Parse(JsonSerializer.SerializeToElement(new
        {
            turnNumber = 2, turnOutcome = outcome, quietEnd = quiet, openChildRunIds = new[] { "child_1", "child_2" }
        }), message, null);
        Assert.Equal(message, completion.OperatorMessageId);
        Assert.Equal(2, completion.TurnNumber);
        Assert.Equal(outcome, completion.TurnOutcome);
        Assert.Equal(quiet, completion.QuietEnd);
        Assert.Equal(new[] { "child_1", "child_2" }, completion.OpenChildRunIds);
    }

    [Theory]
    [InlineData("{\"turnNumber\":\"1\"}")]
    [InlineData("{\"turnNumber\":-1}")]
    [InlineData("{\"turnNumber\":2147483648}")]
    [InlineData("{\"turnOutcome\":\"unknown\"}")]
    [InlineData("{\"quietEnd\":\"true\"}")]
    [InlineData("{\"openChildRunIds\":{}}")]
    [InlineData("{\"openChildRunIds\":[\"child\"]}")]
    [InlineData("{\"turnNumber\":1,\"openChildRunIds\":[\"child\",\"child\"]}")]
    [InlineData("{\"turnNumber\":1,\"openChildRunIds\":[\"bad\\nvalue\"]}")]
    [InlineData("{\"turnNumber\":1,\"openChildRunIds\":[\"child-\u4e00\"]}")]
    [InlineData("{\"turnNumber\":1,\"openChildRunIds\":[42]}")]
    public void Invalid_or_ambiguous_contract_fails_closed(string json)
        => Assert.Throws<InvalidOperationException>(() => ChatContinuationSafety.Parse(JsonDocument.Parse(json).RootElement, Guid.NewGuid(), null));

    [Fact]
    public void Child_collection_and_identity_sizes_are_bounded()
    {
        var tooMany = JsonSerializer.SerializeToElement(new { turnNumber = 1, openChildRunIds = Enumerable.Range(0, 129).Select(x => $"child-{x}") });
        Assert.Throws<InvalidOperationException>(() => ChatContinuationSafety.Parse(tooMany, Guid.NewGuid(), null));
        var longId = JsonSerializer.SerializeToElement(new { turnNumber = 1, openChildRunIds = new[] { new string('a', 129) } });
        Assert.Throws<InvalidOperationException>(() => ChatContinuationSafety.Parse(longId, Guid.NewGuid(), null));
    }
}
