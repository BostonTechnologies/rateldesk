using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Helpdesk.Mcp.Configuration;

public sealed partial record HelpdeskMcpTarget(string Instance, Uri ApiBaseUrl)
{
    public const string InstanceEnvironmentVariable = "RATELDESK_MCP_INSTANCE";
    public const string ConfigurationEnvironmentVariable = "RATELDESK_MCP_CONFIG";

    public string CanonicalApiBaseUrl => ApiBaseUrl.AbsoluteUri.TrimEnd('/');

    public HelpdeskMcpHostContext ToHostContext(
        string transport,
        string resourceUri = HelpdeskMcpHostContext.DefaultResourceUri,
        string catalogRevision = McpOperationCatalog.Revision)
        => new(Instance, transport, CanonicalApiBaseUrl, resourceUri, catalogRevision);

    public static HelpdeskMcpTarget Resolve(
        AgentClientConfiguration configuration,
        string? configurationPath,
        string? instanceOverride = null,
        string? expectedEndpointOverride = null)
    {
        var instance = (string.IsNullOrWhiteSpace(instanceOverride) ? Environment.GetEnvironmentVariable(InstanceEnvironmentVariable) : instanceOverride)?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(instance) || !InstanceLabel().IsMatch(instance))
            throw new AgentClientValidationException($"{InstanceEnvironmentVariable} must be a lowercase instance label containing letters, digits, and hyphens.");

        var expectedEndpointVariable = $"RATELDESK_MCP_{instance.ToUpperInvariant()}_API_BASE_URL";
        var canonicalExpected = Environment.GetEnvironmentVariable("RATELDESK_MCP_EXPECTED_API_BASE_URL");
        var expectedEndpointName = !string.IsNullOrWhiteSpace(expectedEndpointOverride) ? "Helpdesk:Mcp:ExpectedApiBaseUrl" : canonicalExpected is null ? expectedEndpointVariable : "RATELDESK_MCP_EXPECTED_API_BASE_URL";
        var expectedEndpoint = ParseUri(string.IsNullOrWhiteSpace(expectedEndpointOverride) ? canonicalExpected ?? Environment.GetEnvironmentVariable(expectedEndpointVariable) : expectedEndpointOverride, expectedEndpointName);
        var configuredEndpoint = ParseUri(configuration.ApiBaseUrl, "the isolated MCP configuration apiBaseUrl");

        if (!Equivalent(expectedEndpoint, configuredEndpoint))
            throw new AgentClientValidationException($"{InstanceEnvironmentVariable} '{instance}' requires the isolated MCP configuration apiBaseUrl to match {expectedEndpointName}.");

        return new HelpdeskMcpTarget(instance, configuredEndpoint);
    }

    public bool MatchesApiBaseUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var candidate) && IsHttpUri(candidate) && Equivalent(ApiBaseUrl, candidate);

    public HelpdeskMcpTargetInfo ToInfo() => new(Instance, CanonicalApiBaseUrl);

    private static Uri ParseUri(string? value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsHttpUri(uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new AgentClientValidationException($"{name} must be an absolute http or https URL without credentials, query, or fragment.");
        }

        return uri;
    }

    private static bool IsHttpUri(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
           || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool Equivalent(Uri left, Uri right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

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

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex InstanceLabel();
}

public sealed record HelpdeskMcpTargetInfo(
    [property: JsonPropertyName("instance")] string Instance,
    [property: JsonPropertyName("apiBaseUrl")] string ApiBaseUrl);
