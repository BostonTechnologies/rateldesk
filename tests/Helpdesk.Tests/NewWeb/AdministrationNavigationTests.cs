namespace Helpdesk.Tests.NewWeb;

public class AdministrationNavigationTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        TestEnvironment.RepositoryRoot,
        "src",
        "HelpDesk.NewWeb",
        "Components",
        "Layout",
        "NavMenu.razor"));

    [Fact]
    public void Administration_HasOneEmailSettingsGroupWithTheRequiredDestinations()
    {
        Assert.Equal(1, Count("Title=\"Email Settings\""));
        Assert.Equal(1, Count("Href=\"/admin/email-settings\""));
        Assert.Equal(1, Count("Href=\"/admin/pending-emails\""));
        Assert.Equal(1, Count("Href=\"/admin/email-rules\""));
        Assert.Equal(1, Count("Href=\"/admin/templates\""));
        Assert.Equal(1, Count("Href=\"/admin/layouts\""));
        Assert.DoesNotContain("Title=\"Email Design\"", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Administration_HasOneAutomationGroupAndPreservesRoutesAndAliases()
    {
        Assert.Equal(1, Count("Title=\"Automation\""));
        Assert.Equal(1, Count("Href=\"/admin/automation/integration\""));
        Assert.DoesNotContain("Href=\"/settings/connectivity\"", Source, StringComparison.Ordinal);
        Assert.Equal(1, Count("Href=\"/admin/automation\""));
        Assert.Equal(1, Count("Href=\"/admin/requests/services\""));
        Assert.Equal(1, Count("Href=\"/admin/ai-assistant-webhooks\""));
        Assert.Contains("admin/orchestration/orchestration", Source, StringComparison.Ordinal);
        Assert.Contains("MatchesPath(path, \"admin/requests/services\")", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void RelocatedGroups_UseLocationAwareExpansionWithoutDuplicatingOperationsLinks()
    {
        Assert.Contains("ExpandedChanged=\"OnEmailSettingsExpandedChanged\"", Source, StringComparison.Ordinal);
        Assert.Contains("ExpandedChanged=\"OnAutomationExpandedChanged\"", Source, StringComparison.Ordinal);
        Assert.Contains("UpdateExpandedGroups(e.Location);", Source, StringComparison.Ordinal);
        Assert.Equal(1, Count("Href=\"/admin/pending-emails\""));
        Assert.Equal(1, Count("Href=\"/admin/automation/integration\""));
    }

    private static int Count(string value) => Source.Split(value, StringSplitOptions.None).Length - 1;
}
