using System.Net;
using System.Net.Sockets;

namespace Helpdesk.Infrastructure.Persistence.Connectivity;

internal sealed record IntegrationConnectionHooks(
    Func<string, CancellationToken, ValueTask<IReadOnlyList<IPAddress>>> Resolve,
    Func<IPAddress, int, CancellationToken, ValueTask<Stream>> Connect);

public static class IntegrationSafeHttpMessageHandler
{
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateHttpOption = new("RatelDesk.AllowPrivateHttp");

    public static SocketsHttpHandler Create(bool allowPrivateHttp = false, Uri? protocolEndpoint = null)
        => CreateCore(allowPrivateHttp, protocolEndpoint, null);

    public static SocketsHttpHandler CreateServiceLink(Func<bool>? currentAllowPrivateHttp = null, bool allowPrivateHttp = false)
        => CreateServiceLinkCore(currentAllowPrivateHttp, allowPrivateHttp, null);

    internal static SocketsHttpHandler CreateServiceLinkCore(
        Func<bool>? currentAllowPrivateHttp,
        bool allowPrivateHttp,
        IntegrationConnectionHooks? hooks)
    {
        var handler = CreateCore(allowPrivateHttp, null, hooks, currentAllowPrivateHttp, serviceLink: true);
        // An approved private HTTPS socket cannot survive opt-in removal.
        handler.PooledConnectionLifetime = TimeSpan.Zero;
        handler.PooledConnectionIdleTimeout = TimeSpan.Zero;
        return handler;
    }

    internal static SocketsHttpHandler CreateCore(
        bool allowPrivateHttp,
        Uri? protocolEndpoint,
        IntegrationConnectionHooks? hooks,
        Func<bool>? currentAllowPrivateHttp = null,
        bool serviceLink = false)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        handler.ConnectCallback = (context, cancellationToken) => ConnectAsync(context, cancellationToken, allowPrivateHttp, protocolEndpoint, hooks, currentAllowPrivateHttp, serviceLink);
        return handler;
    }

    internal static HttpMessageHandler CreateSignalRHandler(
        bool allowPrivateHttp,
        Uri configuredEndpoint,
        IntegrationConnectionHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(configuredEndpoint);

        return WrapSignalRHandler(CreateCore(allowPrivateHttp, configuredEndpoint, hooks), configuredEndpoint, allowPrivateHttp);
    }

    internal static HttpMessageHandler WrapSignalRHandler(
        HttpMessageHandler innerHandler,
        Uri configuredEndpoint,
        bool allowPrivateHttp)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        ArgumentNullException.ThrowIfNull(configuredEndpoint);

        return new SignalRRequestValidationHandler(configuredEndpoint, allowPrivateHttp)
        {
            InnerHandler = innerHandler
        };
    }

    public static async ValueTask ValidateSignalRTargetAsync(
        Uri configuredEndpoint,
        Uri requestUri,
        bool allowPrivateHttp,
        CancellationToken cancellationToken)
        => await ValidateSignalRTargetAsync(configuredEndpoint, requestUri, allowPrivateHttp, cancellationToken, null);

    internal static async ValueTask ValidateSignalRTargetAsync(
        Uri configuredEndpoint,
        Uri requestUri,
        bool allowPrivateHttp,
        CancellationToken cancellationToken,
        IntegrationConnectionHooks? hooks)
    {
        IntegrationEndpointPolicy.ValidateSignalRRequest(configuredEndpoint, requestUri, allowPrivateHttp);
        var addresses = await ResolveAddressesAsync(requestUri.Host, cancellationToken, hooks);
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
        Uri? protocolEndpoint,
        IntegrationConnectionHooks? hooks,
        Func<bool>? currentAllowPrivateHttp,
        bool serviceLink)
    {
        var request = context.InitialRequestMessage;
        var requestUri = request?.RequestUri
            ?? throw new HttpRequestException("The outbound integration request did not contain a URI.");
        var allowPrivateHttp = currentAllowPrivateHttp is not null ? currentAllowPrivateHttp() :
            defaultAllowPrivateHttp || request.Options.TryGetValue(AllowPrivateHttpOption, out var allowed) && allowed;
        if (protocolEndpoint is not null)
            IntegrationEndpointPolicy.ValidateSignalRRequest(protocolEndpoint, requestUri, allowPrivateHttp);
        var addresses = await ResolveAddressesAsync(context.DnsEndPoint.Host, cancellationToken, hooks);
        // A reload during DNS resolution must also remove the old request's opt-in.
        if (currentAllowPrivateHttp is not null) allowPrivateHttp = currentAllowPrivateHttp();
        if (serviceLink)
            IntegrationEndpointPolicy.ValidateServiceLinkResolvedAddresses(requestUri, addresses, "Service link endpoint", allowPrivateHttp);
        else if (protocolEndpoint is null)
            IntegrationEndpointPolicy.ValidateResolvedAddresses(requestUri, addresses, "Outbound integration endpoint", allowPrivateHttp);
        else
            IntegrationEndpointPolicy.ValidateSignalRResolvedAddresses(protocolEndpoint, requestUri, addresses, "Netclaw SignalR endpoint", allowPrivateHttp);

        SocketException? lastConnectionError = null;
        foreach (var address in addresses)
        {
            if (currentAllowPrivateHttp is not null)
            {
                allowPrivateHttp = currentAllowPrivateHttp();
                if (serviceLink)
                    IntegrationEndpointPolicy.ValidateServiceLinkResolvedAddresses(requestUri, addresses, "Service link endpoint", allowPrivateHttp);
                else if (protocolEndpoint is null)
                    IntegrationEndpointPolicy.ValidateResolvedAddresses(requestUri, addresses, "Outbound integration endpoint", allowPrivateHttp);
                else
                    IntegrationEndpointPolicy.ValidateSignalRResolvedAddresses(protocolEndpoint, requestUri, addresses, "Netclaw SignalR endpoint", allowPrivateHttp);
            }
            try
            {
                return hooks is null
                    ? await OpenSocketAsync(address, context.DnsEndPoint.Port, cancellationToken)
                    : await hooks.Connect(address, context.DnsEndPoint.Port, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException exception)
            {
                lastConnectionError = exception;
            }
        }

        throw new HttpRequestException("The outbound integration endpoint could not be reached.", lastConnectionError);
    }

    private static async ValueTask<Stream> OpenSocketAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<IReadOnlyList<IPAddress>> ResolveAddressesAsync(
        string host,
        CancellationToken cancellationToken,
        IntegrationConnectionHooks? hooks)
        => IPAddress.TryParse(host, out var literal)
            ? [literal]
            : hooks is null
                ? await Dns.GetHostAddressesAsync(host, cancellationToken)
                : await hooks.Resolve(host, cancellationToken);

    private sealed class SignalRRequestValidationHandler(Uri configuredEndpoint, bool allowPrivateHttp) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri;
            if (requestUri is null || !requestUri.IsAbsoluteUri)
                throw new HttpRequestException("The SignalR request did not contain an absolute URI.");

            var requestAllowsPrivateHttp = allowPrivateHttp ||
                request.Options.TryGetValue(AllowPrivateHttpOption, out var allowed) && allowed;
            IntegrationEndpointPolicy.ValidateSignalRRequest(configuredEndpoint, requestUri, requestAllowsPrivateHttp);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
