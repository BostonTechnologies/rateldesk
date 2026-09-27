using System.Net;

namespace Helpdesk.Infrastructure.Persistence.Connectivity;

/// <summary>
/// Common validation for administrator-selected outbound integration targets.
/// Private RFC1918 addresses remain valid for deliberate private deployments;
/// link-local, multicast, unspecified, and well-known metadata targets do not.
/// </summary>
public static class IntegrationEndpointPolicy
{
    private static readonly string[] AllowedSignalRQueryKeys =
    [
        "negotiateVersion",
        "useStatefulReconnect",
        "id",
        "access_token"
    ];

    private static readonly string[] ReservedMetadataHosts =
    [
        "metadata",
        "metadata.google.internal",
        "instance-data.ec2.internal"
    ];

    public static void Validate(Uri uri, string fieldName, bool allowPrivateHttp = false)
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

        if (uri.Scheme == "http" && (!allowPrivateHttp || IsPublicIp(uri.Host)))
        {
            throw new ArgumentException($"{fieldName} must use HTTPS unless explicit private HTTP is enabled.", fieldName);
        }
    }

    public static bool IsAllowed(Uri uri, bool allowPrivateHttp = false) =>
        uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 &&
        !IsRejectedHost(uri.Host) &&
        (uri.Scheme == "https" || (allowPrivateHttp && !IsPublicIp(uri.Host)));

    public static void ValidateResolvedAddresses(
        Uri uri,
        IReadOnlyCollection<IPAddress> addresses,
        string fieldName,
        bool allowPrivateHttp)
    {
        if (!IsAllowed(uri, allowPrivateHttp))
            throw new ArgumentException($"{fieldName} is not allowed by the outbound integration policy.", fieldName);
        ValidateResolvedAddressesCore(uri, addresses, fieldName, allowPrivateHttp);
    }

    public static void ValidateSignalRResolvedAddresses(
        Uri configuredEndpoint,
        Uri requestUri,
        IReadOnlyCollection<IPAddress> addresses,
        string fieldName,
        bool allowPrivateHttp)
    {
        ValidateSignalRRequest(configuredEndpoint, requestUri, allowPrivateHttp);
        ValidateResolvedAddressesCore(requestUri, addresses, fieldName, allowPrivateHttp);
    }

    public static void ValidateSignalRRequest(Uri configuredEndpoint, Uri requestUri, bool allowPrivateHttp)
    {
        Validate(configuredEndpoint, "Netclaw endpoint", allowPrivateHttp);

        if (requestUri.UserInfo.Length != 0 || requestUri.Fragment.Length != 0 ||
            requestUri.Scheme is not ("http" or "https" or "ws" or "wss"))
            throw new ArgumentException("The SignalR request target is not an allowed HTTP or WebSocket URI.", nameof(requestUri));

        var configuredScheme = NetworkScheme(configuredEndpoint);
        if (NetworkScheme(requestUri) != configuredScheme ||
            !string.Equals(requestUri.Host, configuredEndpoint.Host, StringComparison.OrdinalIgnoreCase) ||
            requestUri.Port != configuredEndpoint.Port)
            throw new ArgumentException("The SignalR request target does not match the configured Netclaw authority.", nameof(requestUri));

        var configuredPath = TrimTrailingSlash(configuredEndpoint.AbsolutePath);
        var requestPath = TrimTrailingSlash(requestUri.AbsolutePath);
        if (!string.Equals(requestPath, configuredPath, StringComparison.Ordinal) &&
            !string.Equals(requestPath, configuredPath + "/negotiate", StringComparison.Ordinal))
            throw new ArgumentException("The SignalR request target does not match the configured Netclaw hub path.", nameof(requestUri));

        if (!IsAllowedSignalRQuery(requestUri.Query))
            throw new ArgumentException("The SignalR request contains an unexpected query parameter.", nameof(requestUri));

        if (NetworkScheme(requestUri) == "http" && !allowPrivateHttp)
            throw new ArgumentException("SignalR plaintext HTTP requires explicit private-network opt-in.", nameof(requestUri));
    }

    private static void ValidateResolvedAddressesCore(
        Uri uri,
        IReadOnlyCollection<IPAddress> addresses,
        string fieldName,
        bool allowPrivateHttp)
    {
        if (addresses.Count == 0)
            throw new ArgumentException($"{fieldName} did not resolve to an address.", fieldName);
        if (addresses.Any(IsRejectedAddress))
            throw new ArgumentException($"{fieldName} resolved to a reserved link-local, multicast, unspecified, or metadata address.", fieldName);
        if (NetworkScheme(uri) == "http" && (!allowPrivateHttp || addresses.Any(address => !IsPrivateNetworkAddress(address))))
            throw new ArgumentException($"{fieldName} resolved to a public address while private HTTP is enabled.", fieldName);
    }

    public static bool IsPrivateNetworkAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
        return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }

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
        return IsRejectedAddress(address);
    }

    private static bool IsRejectedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
            (bytes[0] == 0 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] >= 224);
    }

    private static bool IsPublicIp(string host)
    {
        return IPAddress.TryParse(host.TrimEnd('.'), out var address) && !IsPrivateNetworkAddress(address);
    }

    private static string NetworkScheme(Uri uri) => uri.Scheme switch
    {
        "ws" => "http",
        "wss" => "https",
        _ => uri.Scheme
    };

    private static string TrimTrailingSlash(string path)
        => path.Length > 1 ? path.TrimEnd('/') : path;

    private static bool IsAllowedSignalRQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        foreach (var component in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            var encodedName = separator < 0 ? component : component[..separator];
            var name = Uri.UnescapeDataString(encodedName.Replace('+', ' '));
            if (!AllowedSignalRQueryKeys.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            if (separator == 0) return false;
        }

        return true;
    }
}
