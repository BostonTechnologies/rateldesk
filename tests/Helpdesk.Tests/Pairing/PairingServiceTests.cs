using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Pairing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class PairingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Code_is_five_minutes_replaced_and_single_use_with_exact_bounded_restart_retry(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId); var clock = new PairingTestClock();
        PairingExchangeRequest accepted;
        PairingExchangeResponse result;
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var service = peer.Service(scope.ServiceProvider, clock);
            var replaced = await service.GenerateAsync("owner", default); var code = await service.GenerateAsync("owner", default);
            Assert.Equal(clock.GetUtcNow().AddMinutes(5), code.ExpiresAtUtc);
            Assert.Equal("pairing_code_rejected", (await Assert.ThrowsAsync<PairingFailure>(() => service.ExchangeAsync(peer.Exchange(replaced.Code), default))).Code);
            Assert.Equal("pairing_code_rejected", (await Assert.ThrowsAsync<PairingFailure>(() => service.ExchangeAsync(peer.Exchange("AAAA-AAAA"), default))).Code);
            accepted = peer.Exchange(code.Code); result = await service.ExchangeAsync(accepted, default);
            Assert.Equal(result, await service.ExchangeAsync(accepted, default));
            Assert.Equal("pairing_code_used", (await Assert.ThrowsAsync<PairingFailure>(() => service.ExchangeAsync(peer.Exchange(code.Code), default))).Code);
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var saved = await db.Set<InstallationPairingCode>().SingleAsync();
            Assert.DoesNotContain(code.Code.Replace("-", ""), saved.CodeHash, StringComparison.Ordinal);
            Assert.DoesNotContain(result.InboundSecret, (await db.Set<PairingRedemption>().SingleAsync()).ProtectedResponse!, StringComparison.Ordinal);
            Assert.Empty(await db.Set<ServicePrincipalRegistration>().ToListAsync());
            Assert.Empty(await db.Set<SystemConnection>().ToListAsync());
        }
        await h.RestartAsync();
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var service = peer.Service(scope.ServiceProvider, clock);
            var nextCode = await service.GenerateAsync("owner", default);
            Assert.Equal(result, await service.ExchangeAsync(accepted, default));
            clock.Advance(TimeSpan.FromMinutes(11));
            Assert.Equal("pairing_code_used", (await Assert.ThrowsAsync<PairingFailure>(() => service.ExchangeAsync(accepted, default))).Code);
            var expiring = await service.GenerateAsync("owner", default); clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal("pairing_code_rejected", (await Assert.ThrowsAsync<PairingFailure>(() => service.ExchangeAsync(peer.Exchange(expiring.Code), default))).Code);
            await service.CleanupAsync(default);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<PairingRedemption>().ToListAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_one_concurrent_distinct_redemption_commits(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId); var clock = new PairingTestClock();
        PairingCodeDto code;
        await using (var scope = h.App.Services.CreateAsyncScope()) code = await peer.Service(scope.ServiceProvider, clock).GenerateAsync("owner", default);
        async Task<bool> RedeemAsync()
        {
            await using var scope = h.App.Services.CreateAsyncScope();
            try { await peer.Service(scope.ServiceProvider, clock).ExchangeAsync(peer.Exchange(code.Code), default); return true; }
            catch (Exception ex) when (ex is PairingFailure || PairingDatabaseConflict.IsConflict(ex)) { return false; }
        }
        var results = await Task.WhenAll(RedeemAsync(), RedeemAsync());
        Assert.Single(results.Where(x => x));
        await using var verify = h.App.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Single(await db.Set<SystemPair>().ToListAsync());
        Assert.Empty(await db.Set<ServicePrincipalRegistration>().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_code_attempts_are_bounded_across_contexts_and_only_new_generation_resets_them(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId); var clock = new PairingTestClock(); PairingCodeDto code;
        await using (var scope = h.App.Services.CreateAsyncScope()) code = await peer.Service(scope.ServiceProvider, clock).GenerateAsync("owner", default);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var scope = h.App.Services.CreateAsyncScope();
            Assert.Equal("pairing_code_rejected", (await Assert.ThrowsAsync<PairingFailure>(() => peer.Service(scope.ServiceProvider, clock).ExchangeAsync(peer.Exchange("1111-1111"), default))).Code);
        }
        await using var verify = h.App.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<HelpdeskDbContext>(); var row = await db.Set<InstallationPairingCode>().SingleAsync();
        Assert.Equal(5, row.FailedAttempts); Assert.True(row.ExpiresAtUtc <= clock.GetUtcNow()); Assert.Equal("", row.CodeHash);
        var service = peer.Service(verify.ServiceProvider, clock);
        Assert.Equal("pairing_code_rejected", (await Assert.ThrowsAsync<PairingFailure>(() => service.ExchangeAsync(peer.Exchange(code.Code), default))).Code);
        var fresh = await service.GenerateAsync("owner", default); Assert.Equal(0, row.FailedAttempts);
        Assert.NotNull(await service.ExchangeAsync(peer.Exchange(fresh.Code), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_save_retains_same_draft_and_retry_then_distinct_customer_namespace_and_local_first_delete(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId); var clock = new PairingTestClock();
        string pairId; PairingMapping first;
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var service = peer.Service(scope.ServiceProvider, clock); var code = await service.GenerateAsync("owner", default);
            pairId = (await service.ExchangeAsync(peer.Exchange(code.Code), default)).PairId;
            first = new(Guid.NewGuid().ToString("D"), pairId, "Incidents A", "17", h.OrganizationId, h.CustomerId, true, false);
            peer.FailSave = true;
            Assert.Equal("peer_busy", (await Assert.ThrowsAsync<PairingFailure>(() => service.SaveAsync(pairId, first, "owner", default))).Code);
            Assert.Equal(first, Assert.Single(await service.ListAsync("owner", default)).Mapping);
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>(); var row = await db.Set<SystemConnection>().SingleAsync();
            var op = row.SaveOperationId; var principal = row.InboundPrincipalId;
            var credential = SystemPairingService.Read<PairingBusinessCredential>(scope.ServiceProvider.GetRequiredService<IntegrationProviderSecretProtector>().Unprotect(row.ProtectedInboundCredential!));
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().AuthenticateClientAsync(credential.ClientId, credential.ClientSecret));
            peer.FailSave = false;
            Assert.Equal("Connected", (await service.SaveAsync(pairId, first, "owner", default)).Status);
            Assert.Equal(op, row.SaveOperationId); Assert.Equal(principal, row.InboundPrincipalId);
            Assert.True((await service.TestAsync(pairId, Guid.Parse(first.Id), "owner", default)).Success);
            var customer = Guid.NewGuid().ToString("D");
            db.Customers.Add(new() { Id = customer, OrganizationId = h.OrganizationId, Name = "Second customer", Email = "second@example.test", IsEnabled = true }); await db.SaveChangesAsync();
            var second = first with { Id = Guid.NewGuid().ToString("D"), Name = "Incidents B", RatelDeskCustomerId = customer };
            await service.SaveAsync(pairId, second, "owner", default);
            Assert.Equal(2, (await service.ListAsync("owner", default)).Count);
            var sources = await db.IncidentReceiverSources.Where(x => x.SourceInstanceId == h.SourceId).ToListAsync();
            Assert.Equal(3, sources.Count); Assert.Contains(sources, x => x.SourceNamespaceId == Guid.Parse(first.Id)); Assert.Contains(sources, x => x.SourceNamespaceId == Guid.Parse(second.Id));
            var before = await h.CountsAsync();
            peer.Unavailable = true;
            await service.DeleteAsync(pairId, Guid.Parse(first.Id), "owner", true, default);
            Assert.Single(await service.ListAsync("owner", default));
            await service.DeleteAsync(pairId, Guid.Parse(second.Id), "owner", true, default);
            Assert.Empty(await service.ListAsync("owner", default)); Assert.Equal(before, await h.CountsAsync());
            await service.CleanupAsync(default); Assert.All(await db.Set<PairingCleanup>().ToListAsync(), x => Assert.Equal(1, x.Attempts));
            Assert.Equal("pairing_code_used", (await Assert.ThrowsAsync<PairingFailure>(() => service.ExchangeAsync(peer.Exchange(code.Code), default))).Code);
        }
        await h.RestartAsync();
        await using var restart = h.App.Services.CreateAsyncScope();
        Assert.Empty(await peer.Service(restart.ServiceProvider, clock).ListAsync("owner", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_form_is_retained_without_grant_and_original_owner_live_authority_is_required(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId); var clock = new PairingTestClock();
        await using var scope = h.App.Services.CreateAsyncScope(); var service = peer.Service(scope.ServiceProvider, clock);
        var code = await service.GenerateAsync("owner", default); var exchange = peer.Exchange(code.Code); var paired = await service.ExchangeAsync(exchange, default);
        var bad = new PairingMapping(Guid.NewGuid().ToString("D"), paired.PairId, "Retained selection", "17", h.OrganizationId, "missing-customer", true, false);
        Assert.Equal("customer_not_authorized", (await Assert.ThrowsAsync<PairingFailure>(() => service.SaveAsync(paired.PairId, bad, "owner", default))).Code);
        Assert.Equal(bad, Assert.Single(await service.ListAsync("owner", default)).Mapping);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServicePrincipalRegistration>().ToListAsync());
        await h.ChangeCredentialAsync("owner-disabled");
        Assert.Equal("owner_unavailable", (await Assert.ThrowsAsync<PairingFailure>(() => service.AuthenticateAsync(peer.Metadata.InstallationId, paired.InboundSecret, SystemPairingService.Hash(exchange.InboundSecret), default))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_before_first_save_tombstones_absent_mapping_while_other_mapping_remains_active(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId); var clock = new PairingTestClock();
        await using var scope = h.App.Services.CreateAsyncScope(); var service = peer.Service(scope.ServiceProvider, clock);
        var code = await service.GenerateAsync("owner", default); var paired = await service.ExchangeAsync(peer.Exchange(code.Code), default);
        var active = new PairingMapping(Guid.NewGuid().ToString("D"), paired.PairId, "Preserved mapping", "17", h.OrganizationId, h.CustomerId, true, false);
        await service.SaveAsync(paired.PairId, active, "owner", default);
        var delayed = active with { Id = Guid.NewGuid().ToString("D"), Name = "Deleted before save" };
        await service.DeleteAsync(paired.PairId, Guid.Parse(delayed.Id), "owner", false, default);
        var pair = await service.PairAsync(paired.PairId, default);
        var request = new PairingSaveRequest(Guid.NewGuid().ToString("D"), 1, delayed, null);
        Assert.Equal("mapping_deleted", (await Assert.ThrowsAsync<PairingFailure>(() => service.AcceptSaveAsync(pair, Guid.Parse(delayed.Id), request, default))).Code);
        Assert.Equal(active, Assert.Single(await service.ListAsync("owner", default)).Mapping);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Correcting_failed_customer_selection_reuses_card_and_retargets_only_unused_source_namespace(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId); var clock = new PairingTestClock();
        await using var scope = h.App.Services.CreateAsyncScope(); var service = peer.Service(scope.ServiceProvider, clock); var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var code = await service.GenerateAsync("owner", default); var paired = await service.ExchangeAsync(peer.Exchange(code.Code), default);
        var mapping = new PairingMapping(Guid.NewGuid().ToString("D"), paired.PairId, "Correct this selection", "17", h.OrganizationId, h.CustomerId, true, false);
        peer.FailSave = true; await Assert.ThrowsAsync<PairingFailure>(() => service.SaveAsync(paired.PairId, mapping, "owner", default));
        var secondCustomer = Guid.NewGuid().ToString("D"); db.Customers.Add(new() { Id = secondCustomer, OrganizationId = h.OrganizationId, Name = "Correct customer", Email = "correct@example.test", IsEnabled = true }); await db.SaveChangesAsync();
        var corrected = mapping with { RatelDeskCustomerId = secondCustomer }; peer.FailSave = false;
        Assert.Equal("Connected", (await service.SaveAsync(paired.PairId, corrected, "owner", default)).Status);
        Assert.Equal(corrected, Assert.Single(await service.ListAsync("owner", default)).Mapping);
        var source = await db.IncidentReceiverSources.SingleAsync(x => x.SourceNamespaceId == Guid.Parse(mapping.Id)); Assert.Equal(secondCustomer, source.CustomerId); Assert.Equal(2, source.Revision);
        Assert.Empty(await db.IncidentCreateReceipts.ToListAsync()); Assert.Equal(1, await db.Set<ServicePrincipalRegistration>().CountAsync(x => x.Status == "active"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Renewed_peer_generation_revokes_old_incident_callback_tokens_and_cleanup_in_both_initiation_directions(bool postgres, bool localInitiates)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId); var clock = new PairingTestClock();
        await using var scope = h.App.Services.CreateAsyncScope(); var service = peer.Service(scope.ServiceProvider, clock); var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var code = await service.GenerateAsync("owner", default); var oldOffer = peer.Exchange(code.Code); var original = await service.ExchangeAsync(oldOffer, default);
        var mapping = new PairingMapping(Guid.NewGuid().ToString("D"), original.PairId, "Original scoped connection", "17", h.OrganizationId, h.CustomerId, true, true);
        await service.SaveAsync(original.PairId, mapping, "owner", default);
        var row = await db.Set<SystemConnection>().SingleAsync(); var credential = SystemPairingService.Read<PairingBusinessCredential>(scope.ServiceProvider.GetRequiredService<IntegrationProviderSecretProtector>().Unprotect(row.ProtectedInboundCredential!));
        using var issued = await h.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = credential.ClientId, ["client_secret"] = credential.ClientSecret, ["scope"] = string.Join(' ', credential.Scopes) }));
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode); var token = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
        var oldGeneration = (await service.PairAsync(original.PairId, default)).Generation;
        if (localInitiates)
        {
            await service.ConnectAsync(new(peer.Metadata.WebOrigin, "XXXX-YYYY", Guid.NewGuid().ToString("D")), "owner", default);
            Assert.Equal(original.InboundSecret, peer.OfferedInboundSecret);
        }
        else
        {
            var fresh = await service.GenerateAsync("owner", default); var renewed = await service.ExchangeAsync(peer.Exchange(fresh.Code), default); Assert.NotEqual(original.InboundSecret, renewed.InboundSecret);
        }
        var current = await service.PairAsync(original.PairId, default); Assert.Equal(oldGeneration + 1, current.Generation);
        Assert.Null(Assert.Single(await service.ListAsync("owner", default)).Mapping);
        using var incident = await h.CreateIncidentAsync("old-generation", credential: token); Assert.Equal(HttpStatusCode.Unauthorized, incident.StatusCode);
        using var callback = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/orchestration/provider/callback", new { }); Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        if (!localInitiates) Assert.Equal("pairing_revoked", (await Assert.ThrowsAsync<PairingFailure>(() => service.AuthenticateAsync(peer.Metadata.InstallationId, original.InboundSecret, SystemPairingService.Hash(oldOffer.InboundSecret), default))).Code);
        if (localInitiates) Assert.Equal("pairing_generation_changed", (await Assert.ThrowsAsync<PairingFailure>(() => service.AuthenticateAsync(peer.Metadata.InstallationId, original.InboundSecret, SystemPairingService.Hash(oldOffer.InboundSecret), default))).Code);
        Assert.Equal(0, (await h.CountsAsync()).Incidents);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Peer_test_failures_and_persisted_results_mask_every_representation_of_the_saved_code(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        using var peer = new PairingTestPeer(h.SourceId) { TestMessage = "abCdEfGh a-b--C-d-eF-G-h ABCD-EFGH" };
        await using var scope = h.App.Services.CreateAsyncScope(); var service = peer.Service(scope.ServiceProvider, new PairingTestClock());
        var pair = await service.ConnectAsync(new(peer.Metadata.WebOrigin, "abcd-efgh", Guid.NewGuid().ToString("D")), "owner", default);
        var mapping = new PairingMapping(Guid.NewGuid().ToString("D"), pair.PairId, "Safe peer message", "17", h.OrganizationId, h.CustomerId, true, false);
        peer.TestSuccess = false;
        Assert.Equal("[redacted] [redacted] [redacted]", (await Assert.ThrowsAsync<PairingFailure>(() => service.SaveAsync(pair.PairId, mapping, "owner", default))).Message);
        peer.TestSuccess = true;
        var saved = await service.SaveAsync(pair.PairId, mapping, "owner", default); Assert.Equal("Connected", saved.Status);
        Assert.Equal("[redacted] [redacted] [redacted]", (await service.TestAsync(pair.PairId, Guid.Parse(mapping.Id), "owner", default)).Message);
        await h.RestartAsync();
        await using var refreshed = h.App.Services.CreateAsyncScope();
        Assert.Equal("[redacted] [redacted] [redacted]", Assert.Single(await peer.Service(refreshed.ServiceProvider, new PairingTestClock()).ListAsync("owner", default)).LastTest!.Message);
    }
}

internal sealed class PairingTestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan value) => now += value;
}

// An explicitly synthetic wire peer isolates the production setup service and both DB providers.
// Published interoperability and business acceptance use the separate native two-source harness.
internal sealed class PairingTestPeer : HttpMessageHandler, IHttpClientFactory
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RSA key = RSA.Create(2048);
    public PairingMetadata Metadata { get; }
    public bool FailSave { get; set; }
    public bool Unavailable { get; set; }
    public string TestMessage { get; set; } = "Synthetic selected read check";
    public bool TestSuccess { get; set; } = true;
    public string InboundSecret { get; } = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public string? OfferedInboundSecret { get; private set; }
    public PairingTestPeer(Guid producer)
    {
        Metadata = new(PairingContract.Version, "netratel", Guid.NewGuid().ToString("D"), "Synthetic peer", "https://peer.example.test", "https://peer-api.example.test", producer.ToString("D"), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }
    public PairingExchangeRequest Exchange(string code)
    {
        var request = new PairingExchangeRequest(code, Guid.NewGuid().ToString("D"), Metadata, Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)));
        return request with { Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
    }
    public SystemPairingService Service(IServiceProvider sp, TimeProvider clock) => new(sp.GetRequiredService<HelpdeskDbContext>(), new(this), sp.GetRequiredService<PairingAuthority>(), sp.GetRequiredService<IntegrationProviderSecretProtector>(), sp.GetRequiredService<IServicePublicSettingsResolver>(), sp.GetRequiredService<ServiceSigningKeyStore>(), sp.GetRequiredService<IServicePrincipalRegistry>(), clock);
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Unavailable && !request.RequestUri!.AbsolutePath.EndsWith("/metadata", StringComparison.Ordinal)) throw new HttpRequestException("Synthetic unavailable peer");
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/metadata", StringComparison.Ordinal))
        {
            var nonce = request.Headers.GetValues("X-Pairing-Nonce").Single();
            return Ok(new PairingMetadataProof(Metadata, nonce, Convert.ToBase64String(key.SignData(PairingTransport.ProofPayload(Metadata, nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))));
        }
        if (path.EndsWith("/directory", StringComparison.Ordinal)) return Ok(new PairingDirectory([new("17", "Synthetic tenant")], []));
        if (path.EndsWith("/exchange", StringComparison.Ordinal))
        {
            var offered = (await request.Content!.ReadFromJsonAsync<PairingExchangeRequest>(Json, ct))!; OfferedInboundSecret = offered.InboundSecret;
            var response = new PairingExchangeResponse(SystemPairingService.PairId(offered.Peer.InstallationId, Metadata.InstallationId), Metadata, InboundSecret);
            return Ok(response with { Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, Json)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) });
        }
        if (path.EndsWith("/test", StringComparison.Ordinal)) return Ok(new PairingTestResult(TestSuccess, TestMessage, DateTimeOffset.UtcNow));
        if (request.Method == HttpMethod.Put)
        {
            if (FailSave) return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { code = "peer_busy", message = "The synthetic peer is temporarily unavailable. Retry this saved connection." }) };
            var saved = await request.Content!.ReadFromJsonAsync<PairingSaveRequest>(Json, ct);
            var automation = saved!.Mapping.RunAutomation ? new PairingBusinessCredential("nrpair_synthetic", InboundSecret, Metadata.ApiOrigin + "/connect/token", "netratel.services", Metadata.ApiOrigin + "/services", ["netratel.orchestration.read", "netratel.orchestration.invoke"], null, null) : null;
            return Ok(new PairingSaveResponse(saved.Mapping, automation));
        }
        throw new InvalidOperationException("Unexpected synthetic pairing route.");
    }
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
    protected override void Dispose(bool disposing) { if (disposing) key.Dispose(); base.Dispose(disposing); }
}
