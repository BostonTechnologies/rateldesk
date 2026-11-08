extern alias NewWeb;

using Bunit;
using System.Net;
using System.Net.Http.Json;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Enums;
using NSubstitute;
using EmailRulesPage = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.EmailRules.EmailRulesAdmin;
using IRuleClient = NewWeb::HelpDesk.NewWeb.Services.IInboundEmailRuleAdminClient;
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
    public async Task NavMenu_LinksEmailRulesUnderAdminEmailSettings()
    {
        await using var context = NavigationContext("HelpdeskAdmin");
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
    public async Task NavMenu_HidesEmailRulesAndEmailSettingsFromNonAdministrators(string role)
    {
        await using var context = NavigationContext(role);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/email-rules");
        var menu = context.Render<NavMenu>();

        Assert.Empty(menu.FindAll("a[href='/admin/email-rules']"));
        Assert.DoesNotContain(menu.FindComponents<MudNavGroup>(), group => group.Instance.Title == "Email Settings");
    }

    [Fact]
    public async Task Audit_renders_aligned_headers_friendly_deleted_labels_details_and_server_status_filter()
    {
        await using var context = NavigationContext("HelpdeskAdmin");
        var client = Substitute.For<IRuleClient>();
        var queries = new List<InboundEmailAuditQuery>();
        var audit = new InboundEmailRuleAuditDto("audit", "exact-message", null, "deleted-org", "deleted-rule",
            "CreateIncidentForOriginalForwardedSender", true, InboundEmailProcessingStatus.Failed, null,
            "Complete synthetic failure", DateTimeOffset.UtcNow);
        client.GetRulesAsync().Returns([]);
        client.SearchAuditAsync(Arg.Any<InboundEmailAuditQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var query = call.Arg<InboundEmailAuditQuery>(); queries.Add(query);
            return new InboundEmailAuditPage([new(audit, "Unknown rule", "Unknown organization", "Default mailbox", null, null)], 305, query.Page, query.PageSize);
        });
        context.Services.AddSingleton(client);
        using var api = new AuditLookupApi();
        context.Services.AddSingleton<IHttpClientFactory>(api);
        var popovers = context.Render<MudPopoverProvider>();
        var page = context.Render<EmailRulesPage>();
        await page.FindAll("[role=tab]")[1].ClickAsync(new());
        page.WaitForAssertion(() => Assert.Contains("Unknown rule", page.Markup));
        Assert.Equal(7, page.FindAll(".email-audit-table thead th").Count);
        var cells = page.FindAll(".email-audit-table tbody tr").Single().Children;
        Assert.Equal(7, cells.Length);
        Assert.Equal("Time", cells[0].GetAttribute("data-label"));
        Assert.Equal("Organization / mailbox", cells[2].GetAttribute("data-label"));
        Assert.Equal("Ticket", cells[5].GetAttribute("data-label"));
        Assert.DoesNotContain("exact-message", page.Find(".email-audit-table").TextContent);
        page.Find("button[aria-label='Audit details']").Click();
        Assert.Contains("exact-message", page.Find("[aria-label='Audit details'] .workspace-diagnostics").TextContent);
        var outcome = page.FindComponents<MudSelect<string>>().Single(x => x.Instance.Label == "Outcome");
        await outcome.InvokeAsync(() => outcome.Instance.ValueChanged.InvokeAsync("Failed"));
        page.FindAll("button").Single(x => x.TextContent.Trim() == "Apply").Click();
        page.WaitForAssertion(() => Assert.Equal(InboundEmailProcessingStatus.Failed, queries.Last().Status));
        Assert.Equal(25, queries[0].PageSize);
        Assert.NotEmpty(page.FindAll(".mud-table-pagination"));
    }

    private sealed class AuditLookupApi : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("https://synthetic.example/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
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
