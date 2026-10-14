extern alias NewWeb;

using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;
using MudBlazor.Services;
using NavMenu = NewWeb::HelpDesk.NewWeb.Components.Layout.NavMenu;

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
        using var context = NavigationContext("HelpdeskAdmin");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/email-rules");
        var menu = context.Render<NavMenu>();
        var administration = Assert.Single(menu.FindComponents<MudNavGroup>(), group => group.Instance.Title == "Administration");
        var emailSettings = Assert.Single(administration.FindComponents<MudNavGroup>(), group => group.Instance.Title == "Email Settings");

        Assert.Equal(new[]
        {
            "/admin/email-settings", "/admin/pending-emails", "/admin/email-rules", "/admin/templates", "/admin/layouts"
        }, emailSettings.FindAll("a[href]").Select(link => link.GetAttribute("href")));
        var emailRules = Assert.Single(emailSettings.FindAll("a[href='/admin/email-rules']"));
        Assert.Equal("Email Rules", emailRules.TextContent.Trim());
        Assert.Equal("page", emailRules.GetAttribute("aria-current"));
        Assert.Equal("true", administration.Find("button").GetAttribute("aria-expanded"));
        Assert.Equal("true", emailSettings.Find("button").GetAttribute("aria-expanded"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tenant.Roles.Assign")]
    [InlineData("Tenant.Settings.Manage")]
    [InlineData("Incident.Read")]
    public void NavMenu_HidesEmailRulesAndEmailSettingsFromNonAdministrators(string role)
    {
        using var context = NavigationContext(role);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/email-rules");
        var menu = context.Render<NavMenu>();

        Assert.Empty(menu.FindAll("a[href='/admin/email-rules']"));
        Assert.DoesNotContain(menu.FindComponents<MudNavGroup>(), group => group.Instance.Title == "Email Settings");
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

    private static BunitContext NavigationContext(string role)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var authorization = context.AddAuthorization();
        context.Services.RemoveAll<IAuthorizationService>();
        context.Services.RemoveAll<IAuthorizationPolicyProvider>();
        context.Services.AddAuthorizationCore();
        authorization.SetAuthorized("Synthetic email rules administrator");
        authorization.SetRoles(string.IsNullOrEmpty(role) ? [] : [role]);
        return context;
    }

    private static string ReadNewWebSource(string relativePath)
    {
        return File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "src", "HelpDesk.NewWeb", relativePath));
    }
}
