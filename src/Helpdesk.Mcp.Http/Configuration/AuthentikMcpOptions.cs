using Microsoft.Extensions.Options;

namespace Helpdesk.Mcp.Http.Configuration;

public sealed class AuthentikMcpOptions
{
    public const string SectionName = "Authentication:AuthentikMcp";

    public string Authority { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string[] RequiredScopes { get; init; } = [];
    public string[] RequiredGroups { get; init; } = [];
}

public sealed class HelpdeskMcpHttpOptions
{
    public const string SectionName = "Helpdesk:Mcp";

    public string ConfigurationPath { get; init; } = string.Empty;
    public string Instance { get; init; } = string.Empty;
    public string ExpectedApiBaseUrl { get; init; } = string.Empty;
    public string PublicResourceUri { get; init; } = string.Empty;
    public string[] AllowedOrigins { get; init; } = [];
    /// <summary>Ingress mode: <c>gateway</c> for paired local credentials or <c>authentik</c> for external OAuth.</summary>
    public string AuthenticationMode { get; init; } = "authentik";
    /// <summary>Maximum time allowed for the paired-credential exchange, including the response body.</summary>
    public int DelegationTimeoutSeconds { get; init; } = 20;
}

/// <summary>The HTTP serving validator is also used by deployment preflight.</summary>
public sealed class HelpdeskMcpHttpOptionsValidator : IValidateOptions<HelpdeskMcpHttpOptions>
{
    public ValidateOptionsResult Validate(string? name, HelpdeskMcpHttpOptions options)
    {
        var failures = new List<string>();
        if (!AuthentikMcpOptionsValidator.IsCanonicalMcpResource(options.PublicResourceUri) ||
            !Uri.TryCreate(options.PublicResourceUri, UriKind.Absolute, out var resource) ||
            resource.UserInfo.Length != 0 || resource.Query.Length != 0 || resource.Fragment.Length != 0)
            failures.Add("Helpdesk:Mcp:PublicResourceUri must be an absolute HTTPS /mcp URI without credentials, query or fragment.");
        if (options.AllowedOrigins is not { Length: > 0 } || options.AllowedOrigins.Any(origin => !AuthentikMcpOptionsValidator.IsAllowedOrigin(origin) || new Uri(origin).UserInfo.Length != 0))
            failures.Add("Helpdesk:Mcp:AllowedOrigins must contain one or more absolute HTTP(S) origins without paths or credentials.");
        if (!AuthentikMcpOptionsValidator.IsAuthenticationMode(options.AuthenticationMode.Trim().ToLowerInvariant()))
            failures.Add("Helpdesk:Mcp:AuthenticationMode must be gateway or authentik.");
        if (options.DelegationTimeoutSeconds is < 1 or > 60)
            failures.Add("Helpdesk:Mcp:DelegationTimeoutSeconds must be between 1 and 60.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class AuthentikMcpOptionsValidator(IOptions<HelpdeskMcpHttpOptions> mcpOptions)
    : IValidateOptions<AuthentikMcpOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthentikMcpOptions options)
    {
        var failures = new List<string>();
        if (!IsAbsoluteHttps(options.Authority)) failures.Add("Authentication:AuthentikMcp:Authority must be an absolute HTTPS URI.");
        if (!IsAbsoluteHttps(options.Audience)) failures.Add("Authentication:AuthentikMcp:Audience must be an absolute HTTPS URI.");
        if (!IsCanonicalMcpResource(mcpOptions.Value.PublicResourceUri)) failures.Add("Helpdesk:Mcp:PublicResourceUri must be an absolute HTTPS /mcp URI.");
        if (!string.Equals(Normalize(options.Audience), Normalize(mcpOptions.Value.PublicResourceUri), StringComparison.Ordinal))
            failures.Add("Authentication:AuthentikMcp:Audience must equal Helpdesk:Mcp:PublicResourceUri.");
        ValidateValues(options.RequiredScopes, "RequiredScopes", failures);
        ValidateValues(options.RequiredGroups, "RequiredGroups", failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateValues(string[] values, string name, ICollection<string> failures)
    {
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace)) failures.Add($"Authentication:AuthentikMcp:{name} must contain non-empty values.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) failures.Add($"Authentication:AuthentikMcp:{name} must not contain duplicates.");
    }

    internal static bool IsAbsoluteHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    internal static bool IsCanonicalMcpResource(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.AbsolutePath.TrimEnd('/'), "/mcp", StringComparison.Ordinal);
    /// <summary>
    /// Determines whether a browser origin is a complete HTTP(S) origin rather
    /// than a resource URL. Hosts use this to validate their allow-list before
    /// accepting MCP browser requests.
    /// </summary>
    public static bool IsAllowedOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(uri.Host)
        && (uri.AbsolutePath is "" or "/")
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
    public static bool IsAuthenticationMode(string value) => value is "gateway" or "authentik";
    internal static string Normalize(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsoluteUri.TrimEnd('/') : value;
}
