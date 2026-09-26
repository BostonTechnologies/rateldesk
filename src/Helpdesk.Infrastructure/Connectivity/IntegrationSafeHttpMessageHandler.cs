using System.Net;
using System.Net.Sockets;

namespace Helpdesk.Infrastructure.Persistence.Connectivity;

public static class IntegrationSafeHttpMessageHandler
{
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateHttpOption = new("RatelDesk.AllowPrivateHttp");

    public static SocketsHttpHandler Create(bool allowPrivateHttp = false, Uri? protocolEndpoint = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        handler.ConnectCallback = (context, cancellationToken) => ConnectAsync(context, cancellationToken, allowPrivateHttp, protocolEndpoint);
        return handler;
    }

    public static async ValueTask ValidateSignalRTargetAsync(
        Uri configuredEndpoint,
        Uri requestUri,
        bool allowPrivateHttp,
        CancellationToken cancellationToken)
    {
        IntegrationEndpointPolicy.ValidateSignalRRequest(configuredEndpoint, requestUri, allowPrivateHttp);
        var addresses = await ResolveAddressesAsync(requestUri.Host, cancellationToken);
        IntegrationEndpointPolicy.ValidateSignalRResolvedAddresses(
            configuredEndpoint,
            requestUri,
            addresses,
            "Netclaw SignalR endpoint",
            allowPrivateHttp);
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken,
        bool defaultAllowPrivateHttp,
        Uri? protocolEndpoint)
    {
        var request = context.InitialRequestMessage;
        var requestUri = request?.RequestUri
            ?? throw new HttpRequestException("The outbound integration request did not contain a URI.");
        var allowPrivateHttp = defaultAllowPrivateHttp || request.Options.TryGetValue(AllowPrivateHttpOption, out var allowed) && allowed;
        if (protocolEndpoint is not null)
            IntegrationEndpointPolicy.ValidateSignalRRequest(protocolEndpoint, requestUri, allowPrivateHttp);
        var addresses = await ResolveAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (protocolEndpoint is null)
            IntegrationEndpointPolicy.ValidateResolvedAddresses(requestUri, addresses, "Outbound integration endpoint", allowPrivateHttp);
        else
            IntegrationEndpointPolicy.ValidateSignalRResolvedAddresses(protocolEndpoint, requestUri, addresses, "Netclaw SignalR endpoint", allowPrivateHttp);

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
            }
        }

        throw new HttpRequestException("The outbound integration endpoint could not be reached.");
    }

    private static async ValueTask<IReadOnlyList<IPAddress>> ResolveAddressesAsync(string host, CancellationToken cancellationToken)
        => IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
}
