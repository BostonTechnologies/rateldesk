extern alias NewWeb;
using State = NewWeb::HelpDesk.NewWeb.Services.TicketListState;

namespace Helpdesk.Tests.NewWeb;

public sealed class TicketListStateTests
{
    [Fact]
    public void URL_roundtrip_preserves_every_filter_and_never_emits_obsolete_AI_predicates()
    {
        var state = State.Parse("https://example.test/changes?aiOnly=true&aiInvolved=true&search=A%26B&customerOrgId=a&customerOrgId=b&forId=person&assigneeId=staff&unassigned=true&stateValues=Draft&priority=High&from=2026-10-14T22%3A00%3A00Z&to=2026-10-15T22%3A00%3A00Z&tz=Africa%2FJohannesburg&sort=subject&descending=false&page=3&pageSize=50", true);
        var restored = State.Parse("https://example.test/changes?" + state.Query(), true);
        Assert.Equal(state.Query(), restored.Query()); Assert.Equal(3, restored.Page); Assert.Equal(50, restored.PageSize);
        Assert.Equal("A&B", restored.Search); Assert.Equal(new[] { "a", "b" }, restored.CustomerOrgIds); Assert.Equal("all", restored.View);
        Assert.DoesNotContain("aiOnly", state.Query(true)); Assert.DoesNotContain("aiInvolved", state.Query(true));
        Assert.Contains("q=A%26B", state.Query(true)); Assert.Equal("Africa/Johannesburg", restored.TimeZone);
    }
    [Theory]
    [InlineData("state=Resolved", false, "Resolved")]
    [InlineData("lifecycleState=ImplementedSuccess", true, "ImplementedSuccess")]
    public void Legacy_explicit_state_has_no_hidden_active_restriction(string query, bool changes, string expected)
    {
        var state = State.Parse("https://example.test/list?activeOnly=true&" + query, changes);
        Assert.Equal(expected, Assert.Single(state.States)); Assert.Equal("all", state.View);
    }
    [Theory]
    [InlineData("2026-03-29", "Europe/London", 23)]
    [InlineData("2026-10-25", "Europe/London", 25)]
    [InlineData("2026-10-15", "Africa/Johannesburg", 24)]
    public void Date_only_end_expands_to_next_local_day_across_DST(string date, string zone, int hours)
    {
        var from = State.LocalBoundary(date, zone, false); var to = State.LocalBoundary(date, zone, true);
        Assert.Equal(hours, (to!.Value - from!.Value).TotalHours); Assert.Equal(TimeSpan.Zero, from.Value.Offset);
    }
    [Theory]
    [InlineData("2026-03-29 01:30")]
    [InlineData("2026-10-25 01:30")]
    public void Invalid_or_ambiguous_wall_time_is_not_silently_reinterpreted(string value) => Assert.Throws<FormatException>(() => State.LocalBoundary(value, "Europe/London", false));
}
