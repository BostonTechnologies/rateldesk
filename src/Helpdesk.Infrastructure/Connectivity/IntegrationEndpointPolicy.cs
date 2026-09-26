using System.Net;

namespace Helpdesk.Infrastructure.Persistence.Connectivity;

/// <summary>
/// Common validation for administrator-selected outbound integration targets.
/// Private RFC1918 addresses remain valid for deliberate private deployments;
/// link-local, multicast, unspecified, and well-known metadata targets do not.
/// </summary>
public static class IntegrationEndpointPolicy
{
    private static readonly string[] ReservedMetadataHosts =
    [
        "metadata",
        "metadata.google.internal",
        "instance-data.ec2.internal"
    ];

    public static void Validate(Uri uri, string fieldName)
    {
        if (uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            throw new ArgumentException($"{fieldName} must be an absolute HTTP or HTTPS URL without credentials, a query string, or a fragment.", fieldName);
        }

        if (IsRejectedHost(uri.Host))
        {
            throw new ArgumentException($"{fieldName} targets a reserved link-local, multicast, unspecified, or metadata address.", fieldName);
        }
    }

    public static bool IsAllowed(Uri uri) =>
        uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 &&
        !IsRejectedHost(uri.Host);

    private static bool IsRejectedHost(string host)
    {
        var normalizedHost = host.TrimEnd('.');
        if (ReservedMetadataHosts.Contains(normalizedHost, StringComparer.OrdinalIgnoreCase) ||
            normalizedHost.EndsWith(".metadata.google.internal", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(normalizedHost, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
            (bytes[0] == 0 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] >= 224);
    }
}
