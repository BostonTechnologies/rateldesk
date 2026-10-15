extern alias NewWeb;

using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;
using MudBlazor.Services;
using DelegatedNavigation = NewWeb::HelpDesk.NewWeb.Components.Layout.TenantAdministrationNavigation;

namespace Helpdesk.Tests.NewWeb;

public sealed class TenantAdministrationNavigationRenderedTests
{
    [Theory]
    [InlineData("Tenant.Users.Manage", "/admin/users")]
    [InlineData("Tenant.Roles.Assign", "/admin/organizations,/admin/roles,/admin/users")]
    [InlineData("Tenant.Settings.Manage", "/admin/organizations")]
    [InlineData("Tenant.Users.Manage,Tenant.Settings.Manage", "/admin/organizations,/admin/users")]
    [InlineData("Tenant.Users.Manage,Tenant.Roles.Assign", "/admin/organizations,/admin/roles,/admin/users")]
    [InlineData("Incident.Read", "")]
    [InlineData("HelpdeskAdmin,Tenant.Users.Manage", "")]
    [InlineData("", "")]
    public async Task Delegated_navigation_exposes_only_destinations_permitted_by_each_role(string roles, string expectedRoutes)
    {
        await using var context = new NavigationContext(roles.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var menu = context.Render<DelegatedNavigation>();

        Assert.Equal(expectedRoutes.Split(',', StringSplitOptions.RemoveEmptyEntries), Hrefs(menu));
        Assert.Equal(string.IsNullOrEmpty(expectedRoutes) ? 0 : 2, menu.FindComponents<MudNavGroup>().Count);
    }

    [Fact]
    public async Task Users_only_deep_link_is_selected_and_expanded_then_permission_changes_remove_it()
    {
        await using var context = new NavigationContext("Tenant.Users.Manage");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/users?search=synthetic");
        var menu = context.Render<DelegatedNavigation>();
        var team = Assert.Single(menu.FindAll("a[aria-current='page']"));
        Assert.Equal("/admin/users", team.GetAttribute("href"));
        Assert.Equal("0", team.GetAttribute("tabindex"));
        Assert.Equal("Team", team.TextContent.Trim());
        Assert.All(menu.FindComponents<MudNavGroup>(), group =>
            Assert.Equal("true", group.Find("button").GetAttribute("aria-expanded")));

        context.Authorization.SetRoles("Tenant.Settings.Manage");
        menu.WaitForAssertion(() =>
        {
            Assert.Equal(new[] { "/admin/organizations" }, Hrefs(menu));
            Assert.Empty(menu.FindAll("a[aria-current='page']"));
        });

        context.Authorization.SetNotAuthorized();
        menu.WaitForAssertion(() =>
        {
            Assert.Empty(Hrefs(menu));
            Assert.Empty(menu.FindComponents<MudNavGroup>());
        });
    }

    private static string[] Hrefs(IRenderedComponent<DelegatedNavigation> menu) => menu.FindAll("a[href]")
        .Select(link => link.GetAttribute("href")!).OrderBy(route => route).ToArray();

    private sealed class NavigationContext : BunitContext
    {
        public BunitAuthorizationContext Authorization { get; }

        public NavigationContext(params string[] roles)
        {
            Services.AddMudServices();
            JSInterop.Mode = JSRuntimeMode.Loose;
            Authorization = AddAuthorization();
            // Retain mutable test claims while evaluating real authorization policies.
            Services.RemoveAll<IAuthorizationService>();
            Services.RemoveAll<IAuthorizationPolicyProvider>();
            Services.AddAuthorizationCore();
            Authorization.SetAuthorized("Synthetic delegated administrator");
            Authorization.SetRoles(roles);
        }
    }
}
