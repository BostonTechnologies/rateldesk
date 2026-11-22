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
    public void Administration_HasOneEmailLinkWithTheRequiredDestinations()
    {
        Assert.Equal(1, Count("Title=\"Email\""));
        Assert.Equal(1, Count("Href=\"/admin/email-settings\""));
        Assert.Contains("\"admin/pending-emails\"", Source, StringComparison.Ordinal);
        Assert.Contains("\"admin/email-rules\"", Source, StringComparison.Ordinal);
        Assert.Contains("\"admin/templates\"", Source, StringComparison.Ordinal);
        Assert.Contains("\"admin/layouts\"", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Title=\"Email Design\"", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Administration_HasOneAutomationLinkAndPreservesRoutesAndAliases()
    {
        Assert.Equal(1, Count("Title=\"Automation\""));
        Assert.Equal(1, Count("Href=\"/admin/integration\""));
        Assert.DoesNotContain("Href=\"/settings/connectivity\"", Source, StringComparison.Ordinal);
        Assert.Equal(1, Count("Href=\"/admin/automation\""));
        Assert.Contains("\"admin/requests/services\"", Source, StringComparison.Ordinal);
        Assert.Contains("\"admin/ai-assistant-webhooks\"", Source, StringComparison.Ordinal);
        Assert.Contains("admin/orchestration/orchestration", Source, StringComparison.Ordinal);
        Assert.Contains("\"admin/requests/services\"", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void RelocatedGroups_UseLocationAwareExpansionWithoutDuplicatingOperationsLinks()
    {
        Assert.Contains("Aliases=\"@EmailRoutes\"", Source, StringComparison.Ordinal);
        Assert.Contains("Aliases=\"@AutomationRoutes\"", Source, StringComparison.Ordinal);
        var groupSource = File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "src", "HelpDesk.NewWeb", "Components", "Layout", "HelpdeskNavGroup.razor"));
        Assert.Contains("UpdateExpandedGroups(e.Location);", groupSource, StringComparison.Ordinal);
        Assert.Contains("Navigation.LocationChanged -= OnLocationChanged;", groupSource, StringComparison.Ordinal);
        Assert.Contains("\"admin/pending-emails\"", Source, StringComparison.Ordinal);
        Assert.Equal(1, Count("Href=\"/admin/integration\""));
    }

    private static int Count(string value) => Source.Split(value, StringSplitOptions.None).Length - 1;
}
