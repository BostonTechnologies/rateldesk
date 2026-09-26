using System.Net;
using System.Net.Sockets;

namespace Helpdesk.Infrastructure.Persistence.Connectivity;

public static class IntegrationSafeHttpMessageHandler
{
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateHttpOption = new("RatelDesk.AllowPrivateHttp");

    public static SocketsHttpHandler Create(bool allowPrivateHttp = false)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        handler.ConnectCallback = (context, cancellationToken) => ConnectAsync(context, cancellationToken, allowPrivateHttp);
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken,
        bool defaultAllowPrivateHttp)
    {
        var request = context.InitialRequestMessage;
        var requestUri = request?.RequestUri
            ?? throw new HttpRequestException("The outbound integration request did not contain a URI.");
        var allowPrivateHttp = defaultAllowPrivateHttp || request.Options.TryGetValue(AllowPrivateHttpOption, out var allowed) && allowed;
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        IntegrationEndpointPolicy.ValidateResolvedAddresses(requestUri, addresses, "Outbound integration endpoint", allowPrivateHttp);

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
}
