using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace HelpDesk.NewWeb.Components.Layout;

/// <summary>Uses the same selection for the framework's active class and aria-current.</summary>
public sealed class HelpdeskRouteLink : NavLink
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Parameter] public string Route { get; set; } = string.Empty;
    [Parameter] public string[] ExcludedRoutes { get; set; } = [];
    [Parameter] public string[] Aliases { get; set; } = [];

    protected override bool ShouldMatch(string uriAbsolute)
    {
        var path = NavigationRoute.Normalize(Navigation.ToBaseRelativePath(uriAbsolute));
        if (ExcludedRoutes.Any(route => NavigationRoute.IsBranch(path, route))) return false;
        if (Aliases.Any(route => NavigationRoute.IsBranch(path, route))) return true;
        return Match is NavLinkMatch.All
            ? string.Equals(path, NavigationRoute.Normalize(Route), StringComparison.OrdinalIgnoreCase)
            : NavigationRoute.IsBranch(path, Route);
    }
}
