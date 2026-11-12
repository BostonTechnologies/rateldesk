namespace HelpDesk.NewWeb.Components.Layout;

/// <summary>Remembers each section's list URL within the current Self Service layout.</summary>
public sealed class SelfServiceNavigationState
{
    public string CatalogUrl { get; private set; } = "/self-service";
    public string MyRequestsUrl { get; private set; } = "/self-service/my-requests";

    public void Remember(string relativeUrl)
    {
        var path = relativeUrl.Split('?', '#')[0].TrimEnd('/');
        if (string.Equals(path, "self-service/my-requests", StringComparison.OrdinalIgnoreCase))
        {
            MyRequestsUrl = "/" + relativeUrl;
        }
        else if (string.Equals(path, "self-service", StringComparison.OrdinalIgnoreCase) ||
                 (path.StartsWith("self-service/", StringComparison.OrdinalIgnoreCase) &&
                  path.Count(c => c == '/') == 1))
        {
            CatalogUrl = "/" + relativeUrl;
        }
    }
}
