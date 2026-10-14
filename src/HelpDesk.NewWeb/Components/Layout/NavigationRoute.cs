namespace HelpDesk.NewWeb.Components.Layout;

/// <summary>Path segment matching shared by rendered links and their containing groups.</summary>
internal static class NavigationRoute
{
    internal static string Normalize(string path) => path.Split(['?', '#'], 2)[0].Trim('/');

    internal static bool IsBranch(string path, string route)
    {
        var target = Normalize(route);
        if (!target.Contains('*'))
            return string.Equals(path, target, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase);

        // Existing tenant branding uses one varying tenant segment. Keep its
        // ownership narrower than the entire /admin/tenants branch.
        var targetSegments = target.Split('/');
        var pathSegments = path.Split('/');
        if (pathSegments.Length < targetSegments.Length) return false;
        for (var index = 0; index < targetSegments.Length; index++)
            if (targetSegments[index] is "*" ? pathSegments[index].Length == 0
                : !string.Equals(pathSegments[index], targetSegments[index], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
