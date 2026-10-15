extern alias NewWeb;

using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;
using MudBlazor.Interfaces;
using MudBlazor.Services;
using NavMenu = NewWeb::HelpDesk.NewWeb.Components.Layout.NavMenu;
using NavLink = NewWeb::HelpDesk.NewWeb.Components.Layout.HelpdeskNavLink;

namespace Helpdesk.Tests.NewWeb;

public sealed class NavMenuRenderedTests
{
    private static readonly string[] Account = ["home", "auth", "notifications", "/account/integration-credentials", "/logout"];
    private static readonly string[] GlobalAdministration =
    [
        "/admin/users", "/admin/customers", "/admin/organizations", "/admin/support-routing", "/admin/tenant-sla-settings",
        "/admin/roles", "/admin/email-settings", "/admin/pending-emails", "/admin/email-rules", "/admin/templates", "/admin/layouts",
        "/admin/automation/integration", "/admin/automation", "/admin/requests/services", "/admin/ai-assistant-webhooks", "/admin/categories", "/admin/branding"
    ];

    [Fact]
    public void Instance_administrator_retains_every_destination_once_including_account_integration()
    {
        using var context = Context("HelpdeskAdmin");
        var menu = context.Render<NavMenu>();
        var expected = Account.Concat(GlobalAdministration).Concat([
            "self-service", "self-service/my-requests", "incidents", "requests", "tasks", "changes",
            "/ops", "assets", "/admin/resources/data-management"
        ]).OrderBy(route => route).ToArray();

        Assert.Equal(expected, Hrefs(menu));
        Assert.Single(menu.FindAll("nav[aria-label='Primary navigation']"));
        Assert.Equal(1, menu.FindComponents<MudNavGroup>().Count(group => group.Instance.Title == "Administration"));
        Assert.Contains("A very long account name for the customer support team", menu.Markup);
    }

    [Fact]
    public void Ticket_categories_canonical_menu_destination_and_legacy_alias_both_resolve_to_a_routable_component()
    {
        var routes = typeof(NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Categories)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template).ToArray();
        Assert.Contains("/admin/categories", routes);
        Assert.Contains("/admin/categories0", routes);
    }

    [Theory]
    [InlineData("SelfService.User", "self-service,self-service/my-requests", "Portal")]
    [InlineData("Incident.Read", "incidents", "Workspace")]
    [InlineData("Incident.Manager", "incidents,/ops", "Workspace,Operations")]
    [InlineData("Request.User", "requests", "Workspace")]
    [InlineData("Request.Read", "requests,tasks,/ops", "Workspace,Operations")]
    [InlineData("Request.Manager", "requests,tasks,/ops", "Workspace,Operations")]
    [InlineData("Change.Read", "changes", "Workspace")]
    [InlineData("Change.Manager", "changes,/ops", "Workspace,Operations")]
    [InlineData("DataManagement.Admin", "/admin/resources/data-management", "Resources")]
    [InlineData("Tenant.Roles.Assign", "/admin/users,/admin/organizations,/admin/roles", "Administration,Accounts & Orgs")]
    [InlineData("Tenant.Settings.Manage", "/admin/organizations", "Administration,Accounts & Orgs")]
    public void Scoped_roles_keep_exact_leaf_visibility_and_hide_empty_groups(string role, string routes, string groups)
    {
        using var context = Context(role);
        var menu = context.Render<NavMenu>();
        Assert.Equal(Account.Concat(routes.Split(',')).OrderBy(route => route), Hrefs(menu));
        Assert.Equal(groups.Split(',').Append("Operations").Distinct().OrderBy(title => title), menu.FindComponents<MudNavGroup>().Select(group => group.Instance.Title).OrderBy(title => title));
        if (!groups.Contains("Administration", StringComparison.Ordinal))
            Assert.DoesNotContain("Administration", menu.FindAll(".nav-section-heading").Select(element => element.TextContent));
    }

    [Theory]
    [InlineData("/self-service", "self-service")]
    [InlineData("/self-service/?catalog=all#list", "self-service")]
    [InlineData("/self-service/my-requests", "self-service/my-requests")]
    [InlineData("/self-service/my-requests/detail/example?mode=view#comments", "self-service/my-requests")]
    [InlineData("/admin/automation", "/admin/automation")]
    [InlineData("/admin/automation/edit/example", "/admin/automation")]
    [InlineData("/admin/automation/integration", "/admin/automation/integration")]
    [InlineData("/admin/automation/integration/orchestrator/", "/admin/automation/integration")]
    [InlineData("/admin/integrations?section=all", "/admin/automation/integration")]
    [InlineData("/settings/connectivity", "/admin/automation/integration")]
    [InlineData("/admin/orchestration/orchestration", "/admin/automation/integration")]
    [InlineData("/tenant-settings?organizationId=example", "/admin/organizations")]
    [InlineData("/incidents/detail/example", "incidents")]
    [InlineData("/requests/create", "requests")]
    [InlineData("/tasks/edit/example", "tasks")]
    [InlineData("/changes/example", "changes")]
    [InlineData("/ops/slas/details/example", "/ops")]
    [InlineData("/admin/email-rules/edit/example", "/admin/email-rules")]
    [InlineData("/account/integration-credentials/approve?code=SAFE-CODE", "/account/integration-credentials")]
    [InlineData("/account/authenticator", "auth")]
    [InlineData("/account/change-password", "auth")]
    [InlineData("/admin/categories0", "/admin/categories")]
    [InlineData("/admin/tenants/42/branding", "/admin/branding")]
    public void Actual_framework_anchors_select_only_the_owning_leaf(string location, string expectedHref)
    {
        using var context = Context("HelpdeskAdmin");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(location);
        var menu = context.Render<NavMenu>();
        AssertCurrent(menu, expectedHref);
    }

    [Theory]
    [InlineData("/self-service-other")]
    [InlineData("/admin/automation-extra")]
    [InlineData("/incidents-old")]
    [InlineData("/admin/tenants/42/details")]
    public void Route_prefixes_require_a_path_segment_boundary(string location)
    {
        using var context = Context("HelpdeskAdmin");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(location);
        var menu = context.Render<NavMenu>();
        Assert.Empty(menu.FindAll("a.active"));
        Assert.Empty(menu.FindAll("a[aria-current='page']"));
    }

    [Theory]
    [InlineData("/self-service/my-requests", "Portal")]
    [InlineData("/incidents/detail/example", "Workspace")]
    [InlineData("/ops/slas", "Operations")]
    [InlineData("/admin/resources/data-management", "Resources")]
    [InlineData("/admin/users", "Administration,Accounts & Orgs")]
    [InlineData("/admin/email-rules", "Administration,Email Settings")]
    [InlineData("/settings/connectivity", "Administration,Automation")]
    public void Deep_links_expand_required_parent_groups(string route, string expandedTitles)
    {
        using var context = Context("HelpdeskAdmin");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        var menu = context.Render<NavMenu>();
        Assert.Equal(expandedTitles.Split(',').OrderBy(title => title), menu.FindComponents<MudNavGroup>().Where(group => group.Find("button").GetAttribute("aria-expanded") == "true").Select(group => group.Instance.Title).OrderBy(title => title));
        foreach (var href in menu.FindAll("a[aria-current='page']")) Assert.Equal("0", href.GetAttribute("tabindex"));
    }

    [Fact]
    public async Task Collapse_survives_rerender_query_and_permission_updates_and_back_navigation_opens_the_owner()
    {
        using var context = Context("HelpdeskAdmin");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/admin/email-rules");
        var menu = context.Render<NavMenu>();
        var email = Group(menu, "Email Settings");
        email.Find("button").Click();
        menu.Render();
        await menu.InvokeAsync(() => navigation.NavigateTo("/admin/email-rules?search=a#rule"));
        menu.WaitForAssertion(() => Assert.Equal("false", Group(menu, "Email Settings").Find("button").GetAttribute("aria-expanded")));
        Assert.Equal("-1", menu.Find("a[href='/admin/email-rules']").GetAttribute("tabindex"));
        AssertCurrent(menu, "/admin/email-rules");

        Group(menu, "Workspace").Find("button").Click();
        await menu.InvokeAsync(() => navigation.NavigateTo("/incidents"));
        await menu.InvokeAsync(() => navigation.NavigateTo("/admin/email-rules"));
        menu.WaitForAssertion(() =>
        {
            Assert.Equal("true", Group(menu, "Email Settings").Find("button").GetAttribute("aria-expanded"));
            Assert.Equal("true", Group(menu, "Workspace").Find("button").GetAttribute("aria-expanded"));
            AssertCurrent(menu, "/admin/email-rules");
        });
        var authorization = context.Authorization;
        authorization.SetAuthorized("A very long account name for the customer support team");
        authorization.SetRoles("Incident.Read");
        menu.WaitForAssertion(() =>
        {
            Assert.Empty(menu.FindAll("a[href^='/admin/']"));
            Assert.DoesNotContain("Administration", menu.FindAll(".nav-section-heading").Select(element => element.TextContent));
            Assert.Single(menu.FindAll("a[href='incidents']"));
            Assert.Equal("true", Group(menu, "Workspace").Find("button").GetAttribute("aria-expanded"));
        });
    }

    [Fact]
    public void Delegated_administration_expands_on_legacy_organization_alias_without_duplicate_global_routes()
    {
        using var context = Context("Tenant.Roles.Assign");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/tenant-administration?organizationId=example");
        var menu = context.Render<NavMenu>();
        AssertCurrent(menu, "/admin/organizations");
        Assert.Equal("true", Group(menu, "Administration").Find("button").GetAttribute("aria-expanded"));
        Assert.Equal("true", Group(menu, "Accounts & Orgs").Find("button").GetAttribute("aria-expanded"));
        Assert.DoesNotContain("/admin/customers", Hrefs(menu));
        Assert.DoesNotContain("/admin/tenant-sla-settings", Hrefs(menu));
    }

    [Fact]
    public void Account_integration_remains_reachable_without_business_roles_and_logout_removes_authorized_destinations()
    {
        using var context = Context();
        var menu = context.Render<NavMenu>();
        Assert.Equal(Account.OrderBy(route => route), Hrefs(menu));
        Assert.Equal("Operations", Assert.Single(menu.FindComponents<MudNavGroup>()).Instance.Title);
        var authorization = context.Authorization;
        authorization.SetNotAuthorized();
        menu.WaitForAssertion(() => Assert.Equal(new[] { "/login", "home" }.OrderBy(route => route), Hrefs(menu)));
    }

    [Fact]
    public async Task Real_link_click_notifies_the_drawer_navigation_receiver()
    {
        using var context = Context();
        var receiver = new Receiver();
        var link = context.Render<CascadingValue<INavigationEventReceiver>>(parameters => parameters
            .Add(parameter => parameter.Value, receiver)
            .AddChildContent<NavLink>(linkParameters => linkParameters.Add(parameter => parameter.Href, "home").AddChildContent("Home")));
        link.Find("a").Click();
        await link.InvokeAsync(() => Task.CompletedTask);
        Assert.Equal(1, receiver.Navigations);
    }

    private static NavigationTestContext Context(params string[] roles) => new(roles);

    private sealed class NavigationTestContext : BunitContext
    {
        public BunitAuthorizationContext Authorization { get; }
        public NavigationTestContext(params string[] roles)
        {
            Services.AddMudServices();
            JSInterop.Mode = JSRuntimeMode.Loose;
            Authorization = AddAuthorization();
            // Keep bUnit's mutable claims provider, while exercising the same
            // authorization handlers as production. Its fake service changes
            // state after notifying subscribers during SetNotAuthorized.
            Services.RemoveAll<IAuthorizationService>();
            Services.RemoveAll<IAuthorizationPolicyProvider>();
            Services.AddAuthorizationCore();
            Authorization.SetAuthorized("A very long account name for the customer support team");
            Authorization.SetRoles(roles);
        }
    }

    private static string[] Hrefs(IRenderedComponent<NavMenu> menu) => menu.FindAll("a[href]").Select(link => link.GetAttribute("href")!).OrderBy(route => route).ToArray();
    private static IRenderedComponent<MudNavGroup> Group(IRenderedComponent<NavMenu> menu, string title) => Assert.Single(menu.FindComponents<MudNavGroup>(), group => group.Instance.Title == title);
    private static void AssertCurrent(IRenderedComponent<NavMenu> menu, string href)
    {
        Assert.Equal(href, Assert.Single(menu.FindAll("a.active")).GetAttribute("href"));
        Assert.Equal(href, Assert.Single(menu.FindAll("a[aria-current='page']")).GetAttribute("href"));
    }
    private sealed class Receiver : INavigationEventReceiver
    {
        public int Navigations { get; private set; }
        public Task OnNavigation() { Navigations++; return Task.CompletedTask; }
    }
}
