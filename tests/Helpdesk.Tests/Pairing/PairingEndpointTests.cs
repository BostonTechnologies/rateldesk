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
    public async Task Final_save_preserves_safe_peer_readiness_correlation_and_same_draft_for_retry(bool peerHttpFailure)
    {
        var reference = "1234567890abcdef1234567890abcdef";
        var diagnostic = new { stage = "receiver-capabilities", code = "receiver-endpoint-outside-approved-api-base", reference, httpStatus = (int?)null };
        using var peer = new PairingTestPeer(Guid.NewGuid())
        {
            TestSuccess = false,
            TestStatus = peerHttpFailure ? HttpStatusCode.BadGateway : HttpStatusCode.OK,
            TestMessage = "Unsafe peer detail https://private.example.test/path?client_secret=private-body",
            TestDiagnostic = diagnostic
        };
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(false, serviceIdentity: true, pairingPeer: peer);
        using var pairedResponse = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/system-connections/pair",
            new PairingConnectRequest(peer.Metadata.WebOrigin, "ABCD-EFGH", Guid.NewGuid().ToString("D")));
        Assert.Equal(HttpStatusCode.OK, pairedResponse.StatusCode);
        var pair = (await pairedResponse.Content.ReadFromJsonAsync<PairingConnectionDto>())!;
        var mapping = new PairingMapping(Guid.NewGuid().ToString("D"), pair.PairId, "Both capabilities", "17", h.OrganizationId, h.CustomerId, true, true);
        var route = $"/api/v1/admin/system-connections/{pair.PairId}/mappings/{mapping.Id}";

        using var failed = await h.AdminAsync(HttpMethod.Put, route, mapping);
        Assert.Equal(peerHttpFailure ? HttpStatusCode.BadGateway : HttpStatusCode.Conflict, failed.StatusCode);
        using var failure = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
        var error = failure.RootElement;
        Assert.Equal("business_validation_failed", error.GetProperty("code").GetString());
        Assert.Equal(reference, error.GetProperty("reference").GetString());
        Assert.Equal("receiver-capabilities", error.GetProperty("diagnostic").GetProperty("stage").GetString());
        Assert.Equal("receiver-endpoint-outside-approved-api-base", error.GetProperty("diagnostic").GetProperty("code").GetString());
        Assert.Equal(reference, error.GetProperty("diagnostic").GetProperty("reference").GetString());
        Assert.Contains("receiver capabilities", error.GetProperty("message").GetString());
        Assert.Contains("receiver-endpoint-outside-approved-api-base", error.GetProperty("message").GetString());
        Assert.DoesNotContain("private.example.test", failure.RootElement.GetRawText());
        Assert.DoesNotContain("private-body", failure.RootElement.GetRawText());
        using var listed = await h.AdminAsync(HttpMethod.Get, "/api/v1/admin/system-connections/");
        var draft = Assert.Single((await listed.Content.ReadFromJsonAsync<PairingConnectionDto[]>())!);
        Assert.Equal(mapping, draft.Mapping);
        Assert.Equal("Systems paired", draft.Status);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<SystemConnection>().SingleAsync();
            Assert.Equal("draft", row.State);
        }
        Assert.Equal(0, (await h.CountsAsync()).Incidents);

        peer.TestStatus = HttpStatusCode.OK;
        peer.TestSuccess = true;
        peer.TestDiagnostic = null;
        peer.TestMessage = "Authenticated selected capability check passed.";
        using var retried = await h.AdminAsync(HttpMethod.Put, route, mapping);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var connected = (await retried.Content.ReadFromJsonAsync<PairingConnectionDto>())!;
        Assert.Equal(mapping, connected.Mapping);
        Assert.Equal(pair.PairId, connected.PairId);
        Assert.Equal("Connected", connected.Status);
        Assert.Equal(0, (await h.CountsAsync()).Incidents);

        peer.TestSuccess = false;
        peer.TestDiagnostic = diagnostic;
        peer.TestMessage = "Unsafe test detail https://private.example.test/path?client_secret=private-body";
        using var failedTest = await h.AdminAsync(HttpMethod.Post, route + "/test");
        Assert.Equal(HttpStatusCode.OK, failedTest.StatusCode);
        var testResult = (await failedTest.Content.ReadFromJsonAsync<PairingTestResult>())!;
        Assert.False(testResult.Success);
        Assert.Equal(reference, testResult.Diagnostic!.Reference);
        Assert.Contains("Reference: " + reference, testResult.Message);
        Assert.DoesNotContain("private.example.test", testResult.Message);
        using var refreshed = await h.AdminAsync(HttpMethod.Get, "/api/v1/admin/system-connections/");
        var lastTest = Assert.Single((await refreshed.Content.ReadFromJsonAsync<PairingConnectionDto[]>())!).LastTest!;
        Assert.False(lastTest.Success);
        Assert.Equal(testResult.Message, lastTest.Message);
        Assert.Contains("Reference: " + reference, lastTest.Message);

        peer.TestSuccess = true;
        using var successfulTest = await h.AdminAsync(HttpMethod.Post, route + "/test");
        var success = (await successfulTest.Content.ReadFromJsonAsync<PairingTestResult>())!;
        Assert.True(success.Success);
        Assert.Null(success.Diagnostic);
        Assert.DoesNotContain("readiness failed", success.Message);
        Assert.DoesNotContain(reference, success.Message);
        Assert.DoesNotContain("private.example.test", success.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_metadata_and_small_exchange_expose_no_business_or_browser_secrets(bool postgres)
    {
        using var peer = new PairingTestPeer(Guid.NewGuid());
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true, pairingPeer: peer, publishedInstanceId: Guid.NewGuid());
        var nonce = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, "/api/pairing/v1/metadata"); metadataRequest.Headers.Add("X-Pairing-Nonce", nonce);
        using var metadataResponse = await h.Client.SendAsync(metadataRequest); Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        Assert.Equal("no-store", metadataResponse.Headers.CacheControl!.ToString());
        var proof = (await metadataResponse.Content.ReadFromJsonAsync<PairingMetadataProof>())!;
        Assert.Equal(h.PublishedId.ToString("D"), proof.Metadata.InstallationId); Assert.Equal(h.ReceiverId.ToString("D"), proof.Metadata.ReceiverInstanceId); Assert.NotEqual(proof.Metadata.InstallationId, proof.Metadata.ReceiverInstanceId); Assert.Equal(nonce, proof.Nonce);
        using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(proof.Metadata.SigningPublicKey), out _);
        Assert.True(rsa.VerifyData(PairingTransport.ProofPayload(proof.Metadata, nonce), Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(rsa.VerifyData(PairingTransport.ProofPayload(proof.Metadata with { ReceiverInstanceId = Guid.NewGuid().ToString("D") }, nonce), Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
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
