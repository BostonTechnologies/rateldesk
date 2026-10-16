namespace HelpDesk.NewWeb.Services;

/// <summary>Paths shared by serving route generation, its request guard and setup preflight.</summary>
public static class WebGatewayPaths
{
    public const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";
    public static bool IsMcpPath(string path) => path.Equals("/mcp", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/mcp/", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(MetadataPath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(MetadataPath + "/", StringComparison.OrdinalIgnoreCase);
}
