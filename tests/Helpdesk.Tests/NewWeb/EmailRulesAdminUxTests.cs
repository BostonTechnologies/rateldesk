namespace Helpdesk.Tests.NewWeb;

public class EmailRulesAdminUxTests
{
    [Fact]
    public void EmailRulesPage_IsRoutableAndHelpdeskAdminOnly()
    {
        var source = ReadNewWebSource("Components/Pages/Admin/EmailRules/EmailRulesAdmin.razor");

        Assert.Contains("@page \"/admin/email-rules\"", source, StringComparison.Ordinal);
        Assert.Contains("@attribute [Authorize(Roles = \"HelpdeskAdmin\")]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NavMenu_LinksEmailRulesUnderAdminEmailSettings()
    {
        var source = ReadNewWebSource("Components/Layout/NavMenu.razor");

        var emailSettingsIndex = source.IndexOf("Title=\"Email Settings\"", StringComparison.Ordinal);
        var emailRulesIndex = source.IndexOf("Href=\"/admin/email-rules\"", StringComparison.Ordinal);
        var templatesIndex = source.IndexOf("Href=\"/admin/templates\"", StringComparison.Ordinal);

        Assert.True(emailSettingsIndex >= 0);
        Assert.True(emailRulesIndex > emailSettingsIndex);
        Assert.True(templatesIndex > emailRulesIndex);
        Assert.Contains(">Email Rules</MudNavLink>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_UsesTypedClientForOperationalActions()
    {
        var source = ReadNewWebSource("Components/Pages/Admin/EmailRules/EmailRulesAdmin.razor");

        Assert.Contains("@inject IInboundEmailRuleAdminClient RulesClient", source, StringComparison.Ordinal);
        Assert.Contains("RulesClient.EnableAsync(rule.Id)", source, StringComparison.Ordinal);
        Assert.Contains("RulesClient.DisableAsync(rule.Id)", source, StringComparison.Ordinal);
        Assert.Contains("RulesClient.ReorderAsync(payload)", source, StringComparison.Ordinal);
        Assert.Contains("RulesClient.GetAuditAsync(auditMessageId, auditTicketId, auditTenantId, MailboxId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PostAsJsonAsync(\"api/v1/inbound-email-rules", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PutAsJsonAsync(\"api/v1/inbound-email-rules", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableAction_RequiresConfirmationBeforeEndpointCall()
    {
        var source = ReadNewWebSource("Components/Pages/Admin/EmailRules/EmailRulesAdmin.razor");
        var methodStart = source.IndexOf("private async Task EnableRuleAsync", StringComparison.Ordinal);
        var confirmIndex = source.IndexOf("DialogService.ShowMessageBox", methodStart, StringComparison.Ordinal);
        var endpointIndex = source.IndexOf("RulesClient.EnableAsync(rule.Id)", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(confirmIndex > methodStart);
        Assert.True(endpointIndex > confirmIndex);
        Assert.Contains("Enabled rules can affect live inbound email processing.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditStatusLabels_CoverRuleProcessorOutcomes()
    {
        var source = ReadNewWebSource("Components/Pages/Admin/EmailRules/InboundEmailRuleLabels.cs");

        Assert.Contains("InboundEmailProcessingStatus.Succeeded => \"Succeeded\"", source, StringComparison.Ordinal);
        Assert.Contains("InboundEmailProcessingStatus.Failed => \"Failed\"", source, StringComparison.Ordinal);
        Assert.Contains("InboundEmailProcessingStatus.Duplicate => \"Duplicate\"", source, StringComparison.Ordinal);
        Assert.Contains("InboundEmailProcessingStatus.TenantResolutionAmbiguous => \"Ambiguous tenant\"", source, StringComparison.Ordinal);
        Assert.Contains("InboundEmailProcessingStatus.ParserFailed => \"Parser failed\"", source, StringComparison.Ordinal);
        Assert.Contains("InboundEmailProcessingStatus.UnauthorizedSender => \"Unauthorized sender\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsDialog_UsesFriendlyForwardedIncidentActionText()
    {
        var labels = ReadNewWebSource("Components/Pages/Admin/EmailRules/InboundEmailRuleLabels.cs");
        var dialog = ReadNewWebSource("Components/Pages/Admin/EmailRules/InboundEmailRuleDetailsDialog.razor");

        Assert.Contains("Create incident from forwarded support email", labels, StringComparison.Ordinal);
        Assert.Contains("Creates a New / Unassigned incident for the original requester", labels, StringComparison.Ordinal);
        Assert.Contains("InboundEmailRuleLabels.Action(action)", dialog, StringComparison.Ordinal);
        Assert.Contains("InboundEmailRuleLabels.ActionDescription(action)", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Json", dialog, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadNewWebSource(string relativePath)
    {
        return File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "src", "HelpDesk.NewWeb", relativePath));
    }
}
