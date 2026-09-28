using System.Net;
using Helpdesk.Infrastructure.Persistence.Connectivity;

namespace Helpdesk.Infrastructure.Connectivity;

/// <summary>Maps the daemon address printed by Netclaw to its canonical session hub endpoint.</summary>
public static class NetclawEndpointNormalizer
{
    public const string SessionPath = "/hub/session";
    public const string PairingExchangePath = "/api/pair/exchange";

    public static string? Normalize(string? value, string fieldName, bool allowPrivateHttp = false)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var endpoint = value.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("Enter the Netclaw daemon address shown by \"netclaw daemon pair\".", fieldName);

        var path = uri.AbsolutePath;
        if (path != "/" && path != SessionPath && path != SessionPath + "/")
            throw new ArgumentException("Enter the Netclaw daemon address shown by \"netclaw daemon pair\" or its /hub/session URL.", fieldName);

        if (uri.Scheme == Uri.UriSchemeHttp &&
            (!allowPrivateHttp || !IsPrivateHttpLiteral(uri)))
            throw new ArgumentException("This address is HTTP but is not a permitted private Netclaw address.", fieldName);

        IntegrationEndpointPolicy.Validate(uri, fieldName, allowPrivateHttp);
        var builder = new UriBuilder(uri)
        {
            Path = SessionPath,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    public static string ToDaemonAddress(string sessionEndpoint)
    {
        ArgumentNullException.ThrowIfNull(sessionEndpoint);
        if (!Uri.TryCreate(sessionEndpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("A canonical Netclaw session endpoint is required.", nameof(sessionEndpoint));
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string? TryGetDaemonAddress(string? sessionEndpoint)
    {
        if (!Uri.TryCreate(sessionEndpoint, UriKind.Absolute, out var uri))
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static Uri BuildPairingExchangeEndpoint(Uri sessionEndpoint, bool allowPrivateHttp)
    {
        ArgumentNullException.ThrowIfNull(sessionEndpoint);
        if (!sessionEndpoint.IsAbsoluteUri)
            throw new ArgumentException("A canonical Netclaw session endpoint must be absolute.", nameof(sessionEndpoint));
        var canonical = Normalize(sessionEndpoint.AbsoluteUri, "Endpoint", allowPrivateHttp)
            ?? throw new ArgumentException("A Netclaw daemon address is required.", nameof(sessionEndpoint));
        return new UriBuilder(canonical)
        {
            Path = PairingExchangePath,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
    }

    public static string? TryNormalize(string? value, bool allowPrivateHttp = false)
    {
        try
        {
            return Normalize(value, "Endpoint", allowPrivateHttp);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static bool IsPrivateHttpLiteral(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && IsPrivateHttpLiteral(uri);

    public static bool IsPrivateHttpLiteral(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.Scheme == Uri.UriSchemeHttp &&
           IPAddress.TryParse(uri.Host, out var address) &&
           IntegrationEndpointPolicy.IsPrivateNetworkAddress(address);
    }
}
