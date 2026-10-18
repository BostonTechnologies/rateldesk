using Helpdesk.AgentClient;
using System.Text.Json.Serialization;

namespace Helpdesk.Mcp.Configuration;

/// <summary>
/// Immutable identity of a Helpdesk MCP host. It is supplied by the transport
/// composition root and deliberately contains no mutable configuration or secrets.
/// </summary>
public sealed record HelpdeskMcpHostContext
{
    public const string DefaultResourceUri = "helpdesk://server/status";

    public HelpdeskMcpHostContext(
        string instance,
        string transport,
        string canonicalApiBaseUrl,
        string resourceUri,
        string catalogRevision)
    {
        if (string.IsNullOrWhiteSpace(instance))
            throw new AgentClientValidationException("Helpdesk MCP instance is required.");
        if (string.IsNullOrWhiteSpace(transport))
            throw new AgentClientValidationException("Helpdesk MCP transport is required.");
        if (!Uri.TryCreate(canonicalApiBaseUrl, UriKind.Absolute, out var apiBaseUrl) || !IsHttpUri(apiBaseUrl))
            throw new AgentClientValidationException("Helpdesk MCP canonical API URL must be an absolute http or https URL.");
        if (!Uri.TryCreate(resourceUri, UriKind.Absolute, out _))
            throw new AgentClientValidationException("Helpdesk MCP resource URI must be absolute.");
        if (string.IsNullOrWhiteSpace(catalogRevision))
            throw new AgentClientValidationException("Helpdesk MCP catalog revision is required.");

        Instance = instance;
        Transport = transport;
        CanonicalApiBaseUrl = Normalize(apiBaseUrl);
        ResourceUri = resourceUri;
        CatalogRevision = catalogRevision;
    }

    [JsonPropertyName("instance")]
    public string Instance { get; }

    [JsonPropertyName("transport")]
    public string Transport { get; }

    [JsonPropertyName("apiBaseUrl")]
    public string CanonicalApiBaseUrl { get; }

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; }

    [JsonPropertyName("catalogRevision")]
    public string CatalogRevision { get; }

    /// <summary>The active transport authentication mode, supplied by its composition root.</summary>
    [JsonPropertyName("ingressAuthenticationMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IngressAuthenticationMode { get; init; }

    public bool MatchesApiBaseUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var candidate)
           && IsHttpUri(candidate)
           && string.Equals(CanonicalApiBaseUrl, Normalize(candidate), StringComparison.Ordinal);

    public HelpdeskMcpTargetInfo ToTargetInfo() => new(Instance, CanonicalApiBaseUrl);

    private static bool IsHttpUri(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
           || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Path = uri.AbsolutePath.TrimEnd('/')
        };

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
