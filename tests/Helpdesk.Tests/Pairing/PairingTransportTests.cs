using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class PairingTransportTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("stage")]
    [InlineData("code")]
    [InlineData("reference")]
    [InlineData("status")]
    [InlineData("reflected-code")]
    public async Task Readiness_evidence_is_allowlisted_and_formatted_locally_before_forwarding(string variant)
    {
        var reference = "a" + Guid.NewGuid().ToString("N")[1..];
        var diagnostic = new PairingReadinessDiagnostic("receiver-capabilities", "receiver-endpoint-outside-approved-api-base", reference, 502);
        diagnostic = variant switch
        {
            "stage" => diagnostic with { Stage = "https://private.example.test/stage" },
            "code" => diagnostic with { Code = "private-body" },
            "reference" => diagnostic with { Reference = diagnostic.Reference.ToUpperInvariant() },
            "status" => diagnostic with { HttpStatus = 999 },
            "reflected-code" => diagnostic with { Reference = "11111111abcdefab2222222233333333" },
            _ => diagnostic
        };
        Assert.Equal(variant is "valid" or "reflected-code", PairingReadinessDiagnostics.IsValid(diagnostic));
        using var handler = new WireHandler(_ => new(HttpStatusCode.BadGateway)
        {
            Content = JsonContent.Create(new { code = "business_validation_failed", message = "Raw private detail https://private.example.test/body", diagnostic })
        });
        using var client = new HttpClient(handler);
        var transport = new PairingTransport(new SingleClient(client));
        var offered = new PairingExchangeRequest("ABCD-EFAB", Guid.NewGuid().ToString("D"), null!, new string('s', 43));
        var failure = await Assert.ThrowsAsync<PairingFailure>(() => transport.SendAsync<bool>("https://peer.example.test", "/exchange", HttpMethod.Post, offered, null, null, default));
        Assert.Equal("business_validation_failed", failure.Code);
        Assert.Equal(502, failure.Status);
        if (variant == "valid")
        {
            Assert.Equal(diagnostic, failure.Diagnostic);
            Assert.Equal(PairingReadinessDiagnostics.Describe(diagnostic), failure.Message);
            Assert.Contains("HTTP 502", failure.Message);
        }
        else Assert.Null(failure.Diagnostic);
        Assert.DoesNotContain("private.example.test", failure.Message);
        Assert.DoesNotContain("private-body", failure.Message);
        Assert.DoesNotContain("abcdefab", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("private.example.test:5030", "https://private.example.test:5030")]
    [InlineData("http://10.20.30.40:5030/", "http://10.20.30.40:5030")]
    [InlineData("http://[fd12:3456::9]:5030", "http://[fd12:3456::9]:5030")]
    [InlineData("https://private.example.test/", "https://private.example.test")]
    public void Ordinary_origins_support_private_http_https_ports_and_missing_scheme(string entered, string expected) => Assert.Equal(expected, PairingTransport.Origin(entered));

    [Theory]
    [InlineData("https://metadata.google.internal")]
    [InlineData("http://169.254.169.254")]
    [InlineData("https://[fd00:ec2::254]")]
    [InlineData("https://user:secret@private.example.test")]
    [InlineData("https://private.example.test/path")]
    [InlineData("https://private.example.test?token=unsafe")]
    [InlineData("http://8.8.8.8")]
    public void Dangerous_or_ambiguous_origins_are_rejected(string entered) => Assert.Throws<PairingFailure>(() => PairingTransport.Origin(entered));

    [Fact]
    public async Task Canonical_api_must_prove_fresh_nonce_with_pinned_complete_metadata_before_exchange()
    {
        using var first = RSA.Create(2048); using var second = RSA.Create(2048); var calls = 0;
        var metadata = new PairingMetadata(PairingContract.Version, "netratel", Guid.NewGuid().ToString("D"), "Synthetic", "https://web.example.test", "https://api.example.test", Guid.NewGuid().ToString("D"), Convert.ToBase64String(first.ExportSubjectPublicKeyInfo()));
        using var handler = new WireHandler(request =>
        {
            calls++; var nonce = request.Headers.GetValues("X-Pairing-Nonce").Single(); Assert.Equal(43, nonce.Length); Assert.DoesNotContain('=', nonce);
            Assert.Null(request.Headers.Authorization); Assert.Equal("/api/pairing/v1/metadata", request.RequestUri!.AbsolutePath);
            var selected = request.RequestUri.Host == "web.example.test" ? metadata : metadata with { SigningPublicKey = Convert.ToBase64String(second.ExportSubjectPublicKeyInfo()) };
            var key = request.RequestUri.Host == "web.example.test" ? first : second;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PairingMetadataProof(selected, nonce, Convert.ToBase64String(key.SignData(PairingTransport.ProofPayload(selected, nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)))) };
        });
        using var client = new HttpClient(handler); var transport = new PairingTransport(new SingleClient(client));
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<PairingFailure>(() => transport.DiscoverAsync(metadata.WebOrigin, default))).Code); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Reflected_peer_failures_redact_code_normalized_code_and_credentials_and_bound_log_code()
    {
        var secret = new string('s', 43); var code = "ABCD-EFGH";
        using var handler = new WireHandler(_ => new(HttpStatusCode.BadGateway) { Content = JsonContent.Create(new { code = secret, message = code + " " + code.Replace("-", "") + " " + secret + " client_secret=synthetic-secret" }) });
        using var client = new HttpClient(handler); var transport = new PairingTransport(new SingleClient(client));
        var exchange = new PairingExchangeRequest(code, Guid.NewGuid().ToString("D"), null!, secret);
        var failure = await Assert.ThrowsAsync<PairingFailure>(() => transport.SendAsync<bool>("https://peer.example.test", "/exchange", HttpMethod.Post, exchange, secret, Guid.NewGuid().ToString("D"), default));
        Assert.Equal("peer_rejected", failure.Code); Assert.DoesNotContain(code, failure.Message); Assert.DoesNotContain(code.Replace("-", ""), failure.Message); Assert.DoesNotContain(secret, failure.Message); Assert.DoesNotContain("synthetic-secret", failure.Message);
    }

    [Theory]
    [InlineData("ABCD-EFGH", "abcd-efgh")]
    [InlineData("ABCD-EFGH", "abCdEfGh")]
    [InlineData("abcd-efgh", "ABCDEFGH")]
    [InlineData("a-b-c-d-e-f-g-h", "AB-CD--EF-GH")]
    [InlineData("  abcd--efgh  ", "A-B-C-D-E-F-G-H")]
    public async Task Every_accepted_code_representation_is_redacted_from_peer_error_message_and_log_code(string offered, string reflected)
    {
        using var handler = new WireHandler(_ => new(HttpStatusCode.BadGateway) { Content = JsonContent.Create(new { code = reflected, message = "Rejected " + reflected }) });
        using var client = new HttpClient(handler); var transport = new PairingTransport(new SingleClient(client));
        var request = new PairingExchangeRequest(offered, Guid.NewGuid().ToString("D"), null!, new string('s', 43));
        var failure = await Assert.ThrowsAsync<PairingFailure>(() => transport.SendAsync<bool>("https://peer.example.test", "/exchange", HttpMethod.Post, request, null, null, default));
        Assert.Equal("peer_rejected", failure.Code); Assert.Equal("Rejected [redacted]", failure.Message);
    }

    [Fact]
    public async Task Whole_credential_is_redacted_before_a_code_substring_inside_it()
    {
        var secret = new string('a', 15) + "ABCDEFGH" + new string('b', 20);
        using var handler = new WireHandler(_ => new(HttpStatusCode.BadGateway) { Content = JsonContent.Create(new { code = "peer_busy", message = secret }) });
        using var client = new HttpClient(handler); var transport = new PairingTransport(new SingleClient(client));
        var request = new PairingExchangeRequest("ABCD-EFGH", Guid.NewGuid().ToString("D"), null!, secret);
        var failure = await Assert.ThrowsAsync<PairingFailure>(() => transport.SendAsync<bool>("https://peer.example.test", "/exchange", HttpMethod.Post, request, null, null, default));
        Assert.Equal("peer_busy", failure.Code); Assert.Equal("[redacted]", failure.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_private_dns_https_uses_standard_hostname_and_certificate_validation(bool trusted)
    {
        using var caKey = RSA.Create(2048); var caRequest = new CertificateRequest("CN=Synthetic Pairing Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true)); caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = RSA.Create(2048); var leafRequest = new CertificateRequest("CN=pairing-private.example.test", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("pairing-private.example.test"); leafRequest.CertificateExtensions.Add(san.Build()); leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var publicLeaf = leafRequest.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16)); using var leaf = publicLeaf.CopyWithPrivateKey(leafKey);
        using var signing = RSA.Create(2048);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(leaf)));
        await using var app = builder.Build(); PairingMetadata? metadata = null;
        app.MapGet("/api/pairing/v1/metadata", (Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            var nonce = request.Headers["X-Pairing-Nonce"].ToString(); return new PairingMetadataProof(metadata!, nonce, Convert.ToBase64String(signing.SignData(PairingTransport.ProofPayload(metadata!, nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        });
        await app.StartAsync(); var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        var origin = "https://pairing-private.example.test:" + port;
        metadata = new(PairingContract.Version, "netratel", Guid.NewGuid().ToString("D"), "Private HTTPS", origin, origin, Guid.NewGuid().ToString("D"), Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo()));
        var hooks = new IntegrationConnectionHooks((_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("10.20.30.40")]), async (_, targetPort, ct) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); await socket.ConnectAsync(IPAddress.Loopback, targetPort, ct); return new NetworkStream(socket, ownsSocket: true);
        });
        using var handler = IntegrationSafeHttpMessageHandler.CreateCore(true, null, hooks);
        if (trusted) handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, CustomTrustStore = { ca }, RevocationMode = X509RevocationMode.NoCheck };
        using var client = new HttpClient(handler); var transport = new PairingTransport(new SingleClient(client));
        if (trusted) Assert.Equal(metadata, await transport.DiscoverAsync(origin, default));
        else Assert.Equal("peer_unavailable", (await Assert.ThrowsAsync<PairingFailure>(() => transport.DiscoverAsync(origin, default))).Code);
    }

    [Fact]
    public async Task Dns_resolution_rejects_metadata_before_connecting()
    {
        var connections = 0;
        var hooks = new IntegrationConnectionHooks((_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("169.254.169.254")]), (_, _, _) => { connections++; throw new InvalidOperationException(); });
        using var handler = IntegrationSafeHttpMessageHandler.CreateCore(true, null, hooks); using var client = new HttpClient(handler);
        var transport = new PairingTransport(new SingleClient(client)); await Assert.ThrowsAsync<PairingFailure>(() => transport.DiscoverAsync("https://rebound.example.test", default)); Assert.Equal(0, connections);
    }
    private sealed class SingleClient(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class WireHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request)); }
}
