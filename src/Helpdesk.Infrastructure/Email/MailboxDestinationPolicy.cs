using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Resolves once, validates every address, and connects to the validated address.</summary>
public sealed class MailboxDestinationPolicy(IConfiguration configuration)
{
    public async Task<Socket> ConnectAsync(string host, int port, CancellationToken ct)
        => await ConnectAsync(host, port, "EmailIngestion:AllowedPrivateHosts", ct);

    public async Task<Socket> ConnectSmtpAsync(string host, int port, CancellationToken ct)
        => await ConnectAsync(host, port, "EmailSending:AllowedPrivateHosts", ct);

    private async Task<Socket> ConnectAsync(string host, int port, string allowlistKey, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var allowed = configuration.GetSection(allowlistKey).Get<string[]>() ?? [];
        var allowPrivate = allowed.Contains(host, StringComparer.OrdinalIgnoreCase);
        if (addresses.Length == 0 || addresses.Any(ip => !IsAllowed(ip, allowPrivate)))
            throw new InvalidOperationException("Mailbox destination is not allowed by the operator egress policy.");

        var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], port), ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsAllowed(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast || address.IsIPv6LinkLocal)
            return false;
        if (bytes.Length == 4)
        {
            if (bytes[0] == 0 || bytes[0] >= 224 || (bytes[0] == 169 && bytes[1] == 254)) return false;
            var isPrivate = bytes[0] == 10 || bytes[0] == 127 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
            return !isPrivate || allowPrivate;
        }
        return (!(IPAddress.IsLoopback(address) || address.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc)) || allowPrivate;
    }
}
