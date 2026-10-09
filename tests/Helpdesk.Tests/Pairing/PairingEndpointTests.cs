using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Pairing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class PairingEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_metadata_and_small_exchange_expose_no_business_or_browser_secrets(bool postgres)
    {
        using var peer = new PairingTestPeer(Guid.NewGuid());
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true, pairingPeer: peer);
        var nonce = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, "/api/pairing/v1/metadata"); metadataRequest.Headers.Add("X-Pairing-Nonce", nonce);
        using var metadataResponse = await h.Client.SendAsync(metadataRequest); Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        Assert.Equal("no-store", metadataResponse.Headers.CacheControl!.ToString());
        var proof = (await metadataResponse.Content.ReadFromJsonAsync<PairingMetadataProof>())!;
        Assert.Equal(h.ReceiverId.ToString("D"), proof.Metadata.InstallationId); Assert.Equal(nonce, proof.Nonce);
        using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(proof.Metadata.SigningPublicKey), out _);
        Assert.True(rsa.VerifyData(PairingTransport.ProofPayload(proof.Metadata, nonce), Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var unauthenticated = await h.Client.GetAsync("/api/v1/admin/system-connections/"); Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var generated = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/system-connections/code"); Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var code = (await generated.Content.ReadFromJsonAsync<PairingCodeDto>())!; var offered = peer.Exchange(code.Code);
        using var exchanged = await h.Client.PostAsJsonAsync("/api/pairing/v1/exchange", offered); Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var result = (await exchanged.Content.ReadFromJsonAsync<PairingExchangeResponse>())!;
        using var directoryRequest = Setup(HttpMethod.Get, "/api/pairing/v1/directory", peer.Metadata.InstallationId, result.InboundSecret, SystemPairingService.Hash(offered.InboundSecret));
        using var directory = await h.Client.SendAsync(directoryRequest); Assert.Equal(HttpStatusCode.OK, directory.StatusCode);
        var choices = (await directory.Content.ReadFromJsonAsync<PairingDirectory>())!;
        Assert.Contains(choices.Tenants, x => x.Id == h.OrganizationId); Assert.Contains(choices.Customers, x => x.Id == h.CustomerId);
        using var list = await h.AdminAsync(HttpMethod.Get, "/api/v1/admin/system-connections/"); var browserJson = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(result.InboundSecret, browserJson, StringComparison.Ordinal); Assert.DoesNotContain(offered.InboundSecret, browserJson, StringComparison.Ordinal); Assert.DoesNotContain(code.Code, browserJson, StringComparison.Ordinal);
        using var business = Setup(HttpMethod.Get, "/api/v1/incidents/", peer.Metadata.InstallationId, result.InboundSecret, SystemPairingService.Hash(offered.InboundSecret));
        using var blocked = await h.Client.SendAsync(business); Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        await using var scope = h.App.Services.CreateAsyncScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<SystemConnection>().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bad_challenge_exchange_and_generation_return_safe_actionable_reference_without_secret(bool postgres)
    {
        using var peer = new PairingTestPeer(Guid.NewGuid());
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true, pairingPeer: peer);
        using var missing = await h.Client.GetAsync("/api/pairing/v1/metadata"); await FailureAsync(missing, "invalid_challenge");
        using var invalid = await h.Client.PostAsJsonAsync("/api/pairing/v1/exchange", peer.Exchange("AAAA-AAAA")); await FailureAsync(invalid, "pairing_code_rejected");
        using var generated = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/system-connections/code"); var code = (await generated.Content.ReadFromJsonAsync<PairingCodeDto>())!;
        var offered = peer.Exchange(code.Code);
        using var wrongSignature = await h.Client.PostAsJsonAsync("/api/pairing/v1/exchange", offered with { Signature = "invalid" }); await FailureAsync(wrongSignature, "caller_identity_unproven");
        using var exchanged = await h.Client.PostAsJsonAsync("/api/pairing/v1/exchange", offered); var result = (await exchanged.Content.ReadFromJsonAsync<PairingExchangeResponse>())!;
        using var staleRequest = Setup(HttpMethod.Get, "/api/pairing/v1/directory", peer.Metadata.InstallationId, result.InboundSecret, new string('0', 64));
        using var stale = await h.Client.SendAsync(staleRequest); await FailureAsync(stale, "pairing_generation_changed");
        Assert.DoesNotContain(result.InboundSecret, await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static HttpRequestMessage Setup(HttpMethod method, string path, string peerId, string secret, string callerHash)
    {
        var request = new HttpRequestMessage(method, path); request.Headers.Authorization = new("Pairing", secret); request.Headers.Add(PairingContract.PeerHeader, peerId); request.Headers.Add("X-Pairing-Caller", callerHash); return request;
    }
    private static async Task FailureAsync(HttpResponseMessage response, string code)
    {
        Assert.False(response.IsSuccessStatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString()); Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("message").GetString())); Assert.Equal(32, body.RootElement.GetProperty("reference").GetString()!.Length);
    }
}
