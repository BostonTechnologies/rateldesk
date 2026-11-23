using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace Helpdesk.ServiceDefaults.Security;

public sealed class PublicIngressIdentity(IDataProtectionProvider keys)
{
    public const string RelayHeader = "X-RatelDesk-Client-Proof";
    private readonly ITimeLimitedDataProtector _protector = keys.CreateProtector("RatelDesk.WebPublicIngress.v1")
        .ToTimeLimitedDataProtector();

    public static string? FromAddress(IPAddress? address)
    {
        if (address is null)
            return null;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        bytes.AsSpan(8).Clear();
        return new IPAddress(bytes).ToString();
    }

    public string? CreateProof(string? client) => client is null ? null : _protector.Protect(client, TimeSpan.FromSeconds(45));

    public void AddProof(HttpRequestMessage request, string? client)
    {
        request.Headers.Remove(RelayHeader);
        if (CreateProof(client) is { } proof)
            request.Headers.TryAddWithoutValidation(RelayHeader, proof);
    }

    internal string? ReadProof(string proof)
    {
        if (proof.Length is 0 or > 512)
            return null;
        try
        {
            var value = _protector.Unprotect(proof);
            return IPAddress.TryParse(value, out var address) ? FromAddress(address) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string Client(HttpContext context) =>
        context.Features.Get<VerifiedClient>()?.Partition ?? FromAddress(context.Connection.RemoteIpAddress) ?? "unknown";

    public static void UseApiIngressIdentity(IApplicationBuilder app, bool trustWeb)
    {
        app.Use(async (context, next) =>
        {
            var header = context.Request.Headers[RelayHeader];
            context.Request.Headers.Remove(RelayHeader);
            if (trustWeb && header.Count == 1 &&
                context.RequestServices.GetService(typeof(PublicIngressIdentity)) is PublicIngressIdentity identity &&
                identity.ReadProof(header.ToString()) is { } client)
                context.Features.Set(new VerifiedClient(client));
            await next(context);
        });
    }

    public static void UseTrustedForwarding(IApplicationBuilder app, IPAddress[] peers, System.Net.IPNetwork[] networks)
    {
        if (peers.Length == 0 && networks.Length == 0)
            return;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            ForwardLimit = 4
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var peer in peers)
            options.KnownProxies.Add(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer);
        foreach (var network in networks)
            options.KnownIPNetworks.Add(network);
        app.Use(async (context, next) =>
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"];
            if (forwarded.Sum(value => value?.Length ?? 0) > 1024)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await next(context);
        });
        app.UseForwardedHeaders(options);
    }

    private sealed record VerifiedClient(string Partition);
}
