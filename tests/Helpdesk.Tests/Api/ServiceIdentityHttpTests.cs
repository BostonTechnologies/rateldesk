using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class ServiceIdentityHttpTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Scoped_pairing_token_replays_original_committed_receipt_and_survives_restart(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h); var token = await PairingBusinessFixture.TokenAsync(h, client);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("at+jwt", jwt.Header.Typ); Assert.Equal("RS256", jwt.Header.Alg);
        Assert.DoesNotContain(jwt.Claims, x => x.Type is "roles" || x.Type == ClaimTypes.Role || x.Value == "owner");
        Assert.Equal(client.MappingId.ToString("D"), jwt.Claims.Single(x => x.Type == ServiceIdentityClaims.MappingId).Value);
        using var first = await h.CreateIncidentAsync("pairing-replay", credential: token); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var receipt = await first.Content.ReadAsStringAsync(); var counts = await h.CountsAsync();
        await h.RestartAsync(); using var retry = await h.CreateIncidentAsync("pairing-replay", credential: token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode); Assert.Equal(receipt, await retry.Content.ReadAsStringAsync()); Assert.Equal(counts, await h.CountsAsync());
        using var capabilities = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        Assert.Equal(client.MappingId.ToString("D"), JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync()).RootElement.GetProperty("sourceNamespaceId").GetString());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Draft_pairing_cannot_issue_or_use_any_business_token(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var draft = await PairingBusinessFixture.CreateAsync(h, draft: true);
        using var refused = await PairingBusinessFixture.RequestTokenAsync(h, draft, [ServiceIdentityScopes.IncidentCreate]);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode); Assert.Equal(0, (await h.CountsAsync()).Incidents);
        using var manual = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-clients", new { name = "Discarded manual setup" });
        Assert.Equal(HttpStatusCode.NotFound, manual.StatusCode);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Narrow_scope_foreign_mapping_and_machine_to_human_escalation_are_rejected(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true); var client = await PairingBusinessFixture.CreateAsync(h);
        using var escalation = await PairingBusinessFixture.RequestTokenAsync(h, client, [ServiceIdentityScopes.IncidentCreate, "Incident.Write"]); Assert.Equal(HttpStatusCode.BadRequest, escalation.StatusCode);
        var read = await PairingBusinessFixture.TokenAsync(h, client, [ServiceIdentityScopes.IncidentReceipts]);
        using var create = await h.CreateIncidentAsync("scope-denied", credential: read); Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        var token = await PairingBusinessFixture.TokenAsync(h, client); var foreign = h.Payload(); foreign.CustomerId = Guid.NewGuid().ToString("D");
        using var refused = await h.CreateIncidentAsync("foreign", foreign, credential: token); Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var management = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Get, "/api/v1/admin/system-connections/"); Assert.Equal(HttpStatusCode.Forbidden, management.StatusCode);
        using var unkeyed = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post, "/api/v1/incidents/", h.Payload()); Assert.Equal(HttpStatusCode.BadRequest, unkeyed.StatusCode);
        Assert.Equal(0, (await h.CountsAsync()).Incidents);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Cached_tokens_observe_current_owner_mapping_and_local_delete_on_other_replica(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true); var client = await PairingBusinessFixture.CreateAsync(h);
        var token = await PairingBusinessFixture.TokenAsync(h, client); await h.AddReplicaAsync();
        using var first = await h.CreateIncidentAsync("live-authority", replica: 1, credential: token); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await using (var scope = h.App.Services.CreateAsyncScope()) { var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>(); (await identity.Users.SingleAsync(x => x.Id == "owner")).IsInstanceAdministrator = false; await identity.SaveChangesAsync(); }
        using var ownerLost = await h.CreateIncidentAsync("live-authority", replica: 1, credential: token); Assert.Equal(HttpStatusCode.Unauthorized, ownerLost.StatusCode);
        await using (var scope = h.App.Services.CreateAsyncScope()) { var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>(); (await identity.Users.SingleAsync(x => x.Id == "owner")).IsInstanceAdministrator = true; await identity.SaveChangesAsync(); }
        using var deleted = await h.AdminAsync(HttpMethod.Delete, $"/api/v1/admin/system-connections/{client.PairId}/mappings/{client.MappingId:D}"); Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var cached = await h.CreateIncidentAsync("live-authority", replica: 1, credential: token); Assert.Equal(HttpStatusCode.Unauthorized, cached.StatusCode);
        using var repeated = await h.AdminAsync(HttpMethod.Delete, $"/api/v1/admin/system-connections/{client.PairId}/mappings/{client.MappingId:D}"); Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(1, (await h.CountsAsync()).Incidents);
    }
    [Theory][InlineData(false, false)][InlineData(true, false)][InlineData(false, true)][InlineData(true, true)]
    public async Task Original_pair_owner_must_remain_authorized_even_when_another_admin_saved_mapping(bool postgres, bool removeRole)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true); var client = await PairingBusinessFixture.CreateAsync(h);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>(); identity.Users.Add(new ApplicationUser { Id = "saving-admin", UserName = "saving-admin", Email = "second@example.test", IsEnabled = true, IsInstanceAdministrator = true }); await identity.SaveChangesAsync();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>(); (await db.Set<SystemConnection>().SingleAsync()).OwnerId = "saving-admin"; await db.SaveChangesAsync();
        }
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>(); var original = await identity.Users.SingleAsync(x => x.Id == "owner"); if (removeRole) original.IsInstanceAdministrator = false; else original.IsEnabled = false; await identity.SaveChangesAsync();
        }
        using var old = await h.CreateIncidentAsync("pair-owner-revoked", credential: token); Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var issued = await PairingBusinessFixture.RequestTokenAsync(h, client, [ServiceIdentityScopes.IncidentReceipts]); Assert.Equal(HttpStatusCode.Unauthorized, issued.StatusCode);
        Assert.Equal(0, (await h.CountsAsync()).Incidents);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Signed_wrong_claims_expiry_and_algorithms_fail_and_key_rotation_preserves_live_tokens(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true); var client = await PairingBusinessFixture.CreateAsync(h); var token = await PairingBusinessFixture.TokenAsync(h, client);
        foreach (var change in new[] { "issuer", "audience", "purpose", "peer", "tenant", "scope", "mapping", "algorithm", "expired" })
        { using var denied = await PairingBusinessFixture.SendAsync(h, await AlterTokenAsync(h, token, change), HttpMethod.Get, "/api/v1/integrations/netratel/capabilities"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        using var jwks = await h.Client.GetAsync("/.well-known/jwks.json"); Assert.Equal(HttpStatusCode.OK, jwks.StatusCode);
        foreach (var key in JsonDocument.Parse(await jwks.Content.ReadAsStringAsync()).RootElement.GetProperty("keys").EnumerateArray()) foreach (var name in new[] { "d", "p", "q", "dp", "dq", "qi", "k" }) Assert.False(key.TryGetProperty(name, out _));
        await using (var scope = h.App.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<ServiceSigningKeyStore>().RotateAsync();
        using var old = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities"); Assert.Equal(HttpStatusCode.OK, old.StatusCode);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Missing_protected_signing_key_fails_closed_and_recovers_without_identity_reset(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true); var client = await PairingBusinessFixture.CreateAsync(h); var token = await PairingBusinessFixture.TokenAsync(h, client);
        string stored = ""; await h.EditAsync(async db => { var key = await db.Set<ServiceSigningKey>().SingleAsync(); stored = key.ProtectedPrivateKey; key.ProtectedPrivateKey = "unreadable"; });
        using var denied = await PairingBusinessFixture.RequestTokenAsync(h, client, [ServiceIdentityScopes.IncidentReceipts]); Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        await h.EditAsync(async db => (await db.Set<ServiceSigningKey>().SingleAsync()).ProtectedPrivateKey = stored);
        var restored = await PairingBusinessFixture.TokenAsync(h, client); Assert.Equal(new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Kid, new JwtSecurityTokenHandler().ReadJwtToken(restored).Header.Kid);
    }
    private static async Task<string> AlterTokenAsync(IncidentReceiverTests.Harness h, string token, string change)
    {
        var original = new JwtSecurityTokenHandler().ReadJwtToken(token); var claims = original.Claims.Where(x => x.Type is not ("iss" or "aud" or "nbf" or "exp" or "iat")).ToList();
        var type = change switch { "purpose" => "token_use", "peer" => ServiceIdentityClaims.PeerInstanceId, "tenant" => ServiceIdentityClaims.OrganizationId, "scope" => "scope", "mapping" => ServiceIdentityClaims.MappingId, _ => null };
        if (type is not null) { claims.RemoveAll(x => x.Type == type); claims.Add(new(type, "unapproved")); }
        await using var scope = h.App.Services.CreateAsyncScope(); using var key = await scope.ServiceProvider.GetRequiredService<ServiceSigningKeyStore>().GetSigningKeyAsync(); var now = DateTime.UtcNow;
        var altered = new JwtSecurityToken(change == "issuer" ? "https://foreign.example.test" : original.Issuer, change == "audience" ? "unapproved" : original.Audiences.Single(), claims,
            change == "expired" ? now.AddMinutes(-10) : now.AddSeconds(-1), change == "expired" ? now.AddMinutes(-2) : now.AddMinutes(4), new SigningCredentials(key.Key, change == "algorithm" ? SecurityAlgorithms.RsaSha384 : SecurityAlgorithms.RsaSha256));
        altered.Header["typ"] = "at+jwt"; return new JwtSecurityTokenHandler().WriteToken(altered);
    }
}
