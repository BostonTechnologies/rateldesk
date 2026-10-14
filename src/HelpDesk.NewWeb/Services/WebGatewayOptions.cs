using System.Net;

namespace HelpDesk.NewWeb.Services;

/// <summary>Deployment settings resolved once at startup; no backend discovery or failover.</summary>
public sealed class WebGatewayOptions
{
    public string? PublicOrigin { get; set; }
    public WebGatewayMcpOptions Mcp { get; set; } = new();
    public string[] RemoveRoutes { get; set; } = [];
    public string[] RemoveClusters { get; set; } = [];
    public string[] TrustedProxies { get; set; } = [];
    public string[] TrustedNetworks { get; set; } = [];

    public static ResolvedWebGatewayOptions Resolve(IConfiguration configuration)
    {
        var options = configuration.GetSection("WebGateway").Get<WebGatewayOptions>() ?? new();
        options.Mcp ??= new();
        options.RemoveRoutes ??= [];
        options.RemoveClusters ??= [];
        options.TrustedProxies ??= [];
        options.TrustedNetworks ??= [];
        var api = Backend(configuration["ApiBaseUrl"], "ApiBaseUrl");
        Uri? origin = null;
        if (!string.IsNullOrWhiteSpace(options.PublicOrigin))
        {
            if (!Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out origin) ||
                origin.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(origin.Host) ||
                origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 ||
                origin.Query.Length != 0 || origin.Fragment.Length != 0)
                throw new InvalidOperationException("WebGateway:PublicOrigin must be an absolute HTTPS origin without a path, credentials, query or fragment.");
        }

        Uri? resource = null;
        if (!string.IsNullOrWhiteSpace(options.Mcp.PublicResourceUri))
        {
            if (!Uri.TryCreate(options.Mcp.PublicResourceUri, UriKind.Absolute, out resource) ||
                resource.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(resource.Host) ||
                resource.AbsolutePath.TrimEnd('/') != "/mcp" || resource.UserInfo.Length != 0 ||
                resource.Query.Length != 0 || resource.Fragment.Length != 0)
                throw new InvalidOperationException("WebGateway:Mcp:PublicResourceUri must be an absolute HTTPS /mcp URI without credentials, query or fragment.");
            origin ??= new Uri(resource.GetLeftPart(UriPartial.Authority) + "/");
        }

        var mode = options.Mcp.AuthenticationMode?.Trim().ToLowerInvariant();
        if (mode is not ("gateway" or "authentik"))
            throw new InvalidOperationException("WebGateway:Mcp:AuthenticationMode must be gateway or authentik.");
        if (options.Mcp.ActivityTimeout < TimeSpan.FromSeconds(1) || options.Mcp.ActivityTimeout > TimeSpan.FromHours(24))
            throw new InvalidOperationException("WebGateway:Mcp:ActivityTimeout must be between one second and 24 hours.");
        var enabled = options.Mcp.Enabled && !options.Mcp.HardDisable;
        Uri? mcp = null;
        if (enabled)
        {
            if (origin is null)
                throw new InvalidOperationException("WebGateway:PublicOrigin or WebGateway:Mcp:PublicResourceUri is required when generating MCP routes.");
            mcp = Backend(options.Mcp.Destination, "WebGateway:Mcp:Destination");
        }

        var proxies = options.TrustedProxies.Select(value =>
            IPAddress.TryParse(value, out var address) ? address :
            throw new InvalidOperationException("WebGateway:TrustedProxies must contain IP addresses.")).ToArray();
        var networks = options.TrustedNetworks.Select(value =>
            System.Net.IPNetwork.TryParse(value, out var network) ? network :
            throw new InvalidOperationException("WebGateway:TrustedNetworks must contain CIDR networks.")).ToArray();
        foreach (var (identities, key) in new[] { (options.RemoveRoutes, "RemoveRoutes"), (options.RemoveClusters, "RemoveClusters") })
            if (identities.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains(':')) ||
                identities.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Length)
                throw new InvalidOperationException($"WebGateway:{key} must contain distinct, non-empty identities without configuration separators.");

        return new(api, origin, resource, enabled, options.Mcp.HardDisable, mcp, mode, options.Mcp.ActivityTimeout,
            options.RemoveRoutes, options.RemoveClusters, proxies, networks);
    }

    internal static Uri Backend(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException($"{key} must be an absolute HTTP or HTTPS backend URL without credentials, query or fragment.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}

public sealed class WebGatewayMcpOptions
{
    public bool Enabled { get; set; }
    public bool HardDisable { get; set; }
    public string? Destination { get; set; }
    public string? PublicResourceUri { get; set; }
    public string AuthenticationMode { get; set; } = "gateway";
    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed record ResolvedWebGatewayOptions(
    Uri ApiBaseUri, Uri? PublicOrigin, Uri? ConfiguredMcpResource, bool McpEnabled, bool McpHardDisabled, Uri? McpDestination,
    string McpAuthenticationMode, TimeSpan McpActivityTimeout, string[] RemoveRoutes, string[] RemoveClusters,
    IPAddress[] TrustedProxies, System.Net.IPNetwork[] TrustedNetworks)
{
    public Uri? PublicMcpResource => ConfiguredMcpResource ?? (PublicOrigin is null ? null : new(PublicOrigin, "/mcp"));
}
