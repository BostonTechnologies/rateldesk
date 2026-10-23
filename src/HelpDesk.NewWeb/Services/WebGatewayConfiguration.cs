using System.Globalization;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HelpDesk.NewWeb.Services;

/// <summary>
/// One YARP configuration boundary: generated defaults, then operator configuration in provider order.
/// Route/cluster objects merge by case-insensitive identity; arrays and destination sets replace per layer.
/// Gateway settings are startup-only. Explicit ReverseProxy file changes retain YARP reload behavior.
/// </summary>
public sealed class WebGatewayConfiguration : IDisposable
{
    private readonly IConfiguration _source;
    private readonly EffectiveProvider _provider;
    private readonly IDisposable _subscription;
    private ILogger? _logger;
    private readonly object _reloadLock = new();

    public ResolvedWebGatewayOptions Options { get; }
    public IConfigurationRoot EffectiveConfiguration { get; }

    public WebGatewayConfiguration(IConfiguration configuration)
    {
        _source = configuration;
        Options = WebGatewayOptions.Resolve(configuration);
        _provider = new EffectiveProvider(Compose());
        EffectiveConfiguration = new ConfigurationBuilder().Add(new EffectiveSource(_provider)).Build();
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, Reload);
    }

    public void SetLogger(ILogger logger) => _logger = logger;

    private Dictionary<string, string?> Compose()
    {
        var values = Defaults();
        if (_source is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                var layer = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                Read(provider, "ReverseProxy", layer);
                Apply(values, layer);
            }
        }
        else
        {
            Apply(values, _source.GetSection("ReverseProxy").AsEnumerable()
                .Where(item => item.Key.StartsWith("ReverseProxy:", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase));
        }

        foreach (var route in Options.RemoveRoutes) Remove(values, $"ReverseProxy:Routes:{route}");
        foreach (var cluster in Options.RemoveClusters) Remove(values, $"ReverseProxy:Clusters:{cluster}");
        if (Options.McpHardDisabled)
        {
            // The request guard also covers custom broad routes and prevents accidental HTML fallback.
            foreach (var path in values.Where(item => item.Key.EndsWith(":Match:Path", StringComparison.OrdinalIgnoreCase))
                .Where(item => IsMcpRoutePattern(item.Value))
                .Select(item => item.Key[..^":Match:Path".Length]).ToArray())
                Remove(values, path);
        }

        foreach (var entry in values.Where(item => item.Key.StartsWith("ReverseProxy:Clusters:", StringComparison.OrdinalIgnoreCase)
            && item.Key.Contains(":Destinations:", StringComparison.OrdinalIgnoreCase)
            && item.Key.EndsWith(":Address", StringComparison.OrdinalIgnoreCase)))
            WebGatewayOptions.Backend(entry.Value, "ReverseProxy destination Address");
        return values;
    }

    private Dictionary<string, string?> Defaults()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Route(string id, string path, string cluster = "apiCluster")
        {
            result[$"ReverseProxy:Routes:{id}:ClusterId"] = cluster;
            result[$"ReverseProxy:Routes:{id}:Match:Path"] = path;
        }
        Route("api-openapi-route", "/api/openapi/{**catch-all}");
        result["ReverseProxy:Routes:api-openapi-route:Transforms:0:PathRemovePrefix"] = "/api";
        result["ReverseProxy:Routes:api-openapi-route:Transforms:1:RequestHeader"] = "X-Forwarded-Prefix";
        result["ReverseProxy:Routes:api-openapi-route:Transforms:1:Set"] = "/api";
        Route("api-v1-route", "/api/v1/{**catch-all}");
        result["ReverseProxy:Routes:api-v1-route:Transforms:0:RequestHeader"] = "X-Forwarded-Prefix";
        result["ReverseProxy:Routes:api-v1-route:Transforms:0:Set"] = "/api";
        Route("api-incidents-assets-route", "/api/incidents/{**catch-all}");
        Route("api-worklogs-assets-route", "/api/worklogs/{**catch-all}");
        Route("api-email-template-assets-route", "/api/email-templates/{**catch-all}");
        Route("api-tenant-branding-assets-route", "/api/tenants/{**catch-all}");
        Route("email-brand-route", "/email-brand/{**catch-all}");
        result["ReverseProxy:Clusters:apiCluster:Destinations:api1:Address"] = Options.ApiBaseUri.AbsoluteUri;

        if (Options.McpEnabled)
        {
            Route("mcpRoute", "/mcp/{**catch-all}", "mcpCluster");
            if (Options.McpAuthenticationMode == "authentik")
                Route("mcpMetadataRoute", WebGatewayPaths.MetadataPath, "mcpCluster");
            result["ReverseProxy:Clusters:mcpCluster:Destinations:mcp1:Address"] = Options.McpDestination!.AbsoluteUri;
            result["ReverseProxy:Clusters:mcpCluster:HttpRequest:AllowResponseBuffering"] = "false";
            result["ReverseProxy:Clusters:mcpCluster:HttpRequest:ActivityTimeout"] = Options.McpActivityTimeout.ToString("c", CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static void Read(IConfigurationProvider provider, string parent, Dictionary<string, string?> values)
    {
        if (provider.TryGet(parent, out var value)) values[parent] = value;
        foreach (var child in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
            Read(provider, parent + ":" + child, values);
    }

    private static void Apply(Dictionary<string, string?> values, Dictionary<string, string?> layer)
    {
        var replacements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in layer.Keys)
        {
            var segments = key.Split(':');
            if (segments.Length > 3 && segments[1].Equals("Clusters", StringComparison.OrdinalIgnoreCase) &&
                segments[3].Equals("Destinations", StringComparison.OrdinalIgnoreCase))
                replacements.Add(string.Join(':', segments.Take(4)));
            if (segments.Length > 3 && segments[1].Equals("Routes", StringComparison.OrdinalIgnoreCase))
            {
                if (segments[3].Equals("Transforms", StringComparison.OrdinalIgnoreCase))
                    replacements.Add(string.Join(':', segments.Take(4)));
                if (segments.Length > 4 && segments[3].Equals("Match", StringComparison.OrdinalIgnoreCase) &&
                    new[] { "Hosts", "Methods", "Headers", "QueryParameters" }.Contains(segments[4], StringComparer.OrdinalIgnoreCase))
                    replacements.Add(string.Join(':', segments.Take(5)));
            }
        }
        foreach (var prefix in replacements) Remove(values, prefix);
        foreach (var entry in layer) values[entry.Key] = entry.Value;
    }

    private static void Remove(Dictionary<string, string?> values, string prefix)
    {
        foreach (var key in values.Keys.Where(key => key.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
            values.Remove(key);
    }

    private static bool IsMcpRoutePattern(string? pattern)
    {
        // ASP.NET route templates also accept rootless patterns; inspection must not reject them.
        if (string.IsNullOrEmpty(pattern)) return false;
        var rooted = pattern.StartsWith('/') ? pattern : "/" + pattern;
        return WebGatewayPaths.IsMcpPath(rooted);
    }

    private void Reload()
    {
        lock (_reloadLock)
        {
            try { _provider.Replace(Compose()); }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
            {
                // Configuration can contain credentials in advanced destinations; log no values or exception text.
                _logger?.LogError("Rejected an invalid Web gateway reload. The previous configuration remains active; review ReverseProxy settings.");
            }
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        (EffectiveConfiguration as IDisposable)?.Dispose();
    }

    private sealed class EffectiveSource(EffectiveProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    private sealed class EffectiveProvider(Dictionary<string, string?> values) : ConfigurationProvider
    {
        public override void Load() => Data = values;
        public void Replace(Dictionary<string, string?> replacement)
        {
            Data = replacement;
            OnReload();
        }
    }
}
