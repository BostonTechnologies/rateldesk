using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Helpdesk.Tests.Api;

/// <summary>Real issuer/receiver HTTP requests and durable SQLite/PostgreSQL grants, including replicas.</summary>
[Collection("Incident receiver")]
public sealed class ServiceIdentityHttpTests
{
    private static readonly string[] IncidentScopes =
        [ServiceIdentityScopes.IncidentCreate, ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.IncidentTargets];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deployment_owned_credentials_reject_shadowing_and_preserve_source_after_secret_change(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var configuration = h.App.Services.GetRequiredService<IConfiguration>();
        foreach (var pair in new Dictionary<string, string>
        {
            ["ClientId"] = "deployment-receiver", ["ClientSecret"] = "synthetic-deployment-secret-at-least-32-characters",
            ["OrganizationId"] = h.OrganizationId, ["PeerInstanceId"] = "netratel-deployed", ["PeerTenantId"] = "17",
            ["Scopes:0"] = ServiceIdentityScopes.IncidentReceipts, ["CustomerIds:0"] = h.CustomerId,
            ["SourceInstanceId"] = h.SourceId.ToString("D")
        }) configuration["ServiceIdentity:Clients:0:" + pair.Key] = pair.Value;
        h.App.Services.GetRequiredService<IOptionsMonitorCache<ServiceIdentityOptions>>().Clear();
        var original = await TokenAsync(h, "deployment-receiver", "synthetic-deployment-secret-at-least-32-characters", [ServiceIdentityScopes.IncidentReceipts]);
        using var list = await AdminAsync(h, HttpMethod.Get, "/api/v1/admin/service-clients");
        var deployed = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Single(x => x.GetProperty("clientId").GetString() == "deployment-receiver");
        Assert.Equal("deployment", deployed.GetProperty("source").GetString());
        Assert.True(deployed.GetProperty("readOnly").GetBoolean());
        var id = deployed.GetProperty("id").GetGuid();
        using var shadow = await AdminAsync(h, HttpMethod.Post, "/api/v1/admin/service-clients", new
        {
            name = "Attempted collision", clientId = "DEPLOYMENT-RECEIVER", organizationId = h.OrganizationId,
            peerInstanceId = "different-peer", peerTenantId = "99", scopes = IncidentScopes,
            customerIds = new[] { h.CustomerId }, sourceInstanceId = h.SourceId
        });
        Assert.Equal(HttpStatusCode.Conflict, shadow.StatusCode);
        using var managedMutation = await AdminAsync(h, HttpMethod.Post, $"/api/v1/admin/service-clients/{id:D}/rotate", new { expectedCredentialRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, managedMutation.StatusCode);
        configuration["ServiceIdentity:Clients:0:ClientSecret"] = "synthetic-replacement-deployment-secret-at-least-32-characters";
        h.App.Services.GetRequiredService<IOptionsMonitorCache<ServiceIdentityOptions>>().Clear();
        var successor = await TokenAsync(h, "deployment-receiver", "synthetic-replacement-deployment-secret-at-least-32-characters", [ServiceIdentityScopes.IncidentReceipts]);
        using var cap = await ResourceAsync(h, successor, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        Assert.Equal(HttpStatusCode.OK, cap.StatusCode);
        Assert.Equal(h.NamespaceId.ToString("D"), JsonDocument.Parse(await cap.Content.ReadAsStringAsync()).RootElement.GetProperty("sourceNamespaceId").GetString());
        using var stale = await ResourceAsync(h, original, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_reciprocal_identity_cannot_obtain_or_use_business_authority(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true, serviceLinks: true);
        CreatedServiceClient pending;
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>().CurrentValue;
            var attemptId = Guid.NewGuid().ToString("N");
            var linkId = Guid.NewGuid().ToString("D");
            var descriptorHash = new string('b', 64);
            var localSnapshot = PendingSnapshot(settings, "rateldesk", settings.InstanceId);
            var peerSnapshot = PendingSnapshot(new ServiceIdentityOptions { Enabled = true, Issuer = "https://netratel.example.test", Audience = "netratel.api",
                ApiBaseUrl = "https://netratel.example.test", WebBaseUrl = "https://netratel-web.example.test", InstanceId = "netratel-pending" }, "netratel", "netratel-pending");
            var summary = new ServiceLinkGrantSummary
            {
                AttemptId = attemptId, LinkId = linkId, DescriptorHash = descriptorHash, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                InitiatorInstanceId = peerSnapshot.InstanceId, ResponderInstanceId = localSnapshot.InstanceId, InitiatorEndpointSnapshot = peerSnapshot, ResponderEndpointSnapshot = localSnapshot,
                Grants = [new() { DirectionId = ServiceLinkContract.InitiatorToResponder, CallerSnapshot = "initiator", TargetSnapshot = "responder", CallerProduct = "netratel",
                    CallerInstanceId = peerSnapshot.InstanceId, CallerTenantId = "17", TargetProduct = "rateldesk", TargetInstanceId = settings.InstanceId, TargetTenantId = h.OrganizationId,
                    Issuer = settings.Issuer, Audience = settings.Audience, Scopes = IncidentScopes, Capabilities = ["rateldesk.incident-create.v1"], SourceInstanceId = h.SourceId.ToString("D"), SourceNamespaceId = h.NamespaceId.ToString("D"),
                    ResourceConstraints = new() { OrganizationId = h.OrganizationId, CustomerIds = [h.CustomerId] } },
                    new() { DirectionId = ServiceLinkContract.ResponderToInitiator, CallerSnapshot = "responder", TargetSnapshot = "initiator", CallerProduct = "rateldesk", CallerInstanceId = settings.InstanceId,
                        CallerTenantId = h.OrganizationId, TargetProduct = "netratel", TargetInstanceId = peerSnapshot.InstanceId, TargetTenantId = "17", Issuer = peerSnapshot.OauthIssuer, Audience = peerSnapshot.Audience,
                        Capabilities = ["netratel.orchestration.v1"], Scopes = ["netratel.orchestration.read", "netratel.orchestration.invoke"], ResourceConstraints = new() { TenantId = "17" } }]
            };
            var grantHash = ServiceLinkCanonicalJson.HashObject(summary);
            pending = await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().CreatePendingAsync(new(
                "Pending consent", h.OrganizationId, "netratel-pending", "17", IncidentScopes, [h.CustomerId], h.SourceId,
                LinkId: linkId, AttemptId: attemptId, GrantHash: grantHash,
                DescriptorHash: descriptorHash, DirectionId: "initiator_to_responder",
                ResourceConstraintsJson: JsonSerializer.Serialize(new ServiceLinkResourceConstraints
                { OrganizationId = h.OrganizationId, CustomerIds = [h.CustomerId] })), "owner");
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Set<ServiceLinkAttempt>().Add(new() { AttemptId = attemptId, LinkId = linkId, Role = "responder", LocalTenantId = h.OrganizationId,
                PeerInstanceId = peerSnapshot.InstanceId, PeerTenantId = "17", InboundPrincipalId = pending.Principal.Id, GrantHash = grantHash, DescriptorHash = descriptorHash,
                GrantSummaryJson = JsonSerializer.Serialize(summary), LifecycleState = "approved", ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds() });
            await db.SaveChangesAsync();
        }
        using var refused = await RequestTokenAsync(h, pending.Principal.ClientId, pending.ClientSecret, IncidentScopes);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var control = await TokenAsync(h, pending.Principal.ClientId, pending.ClientSecret, [ServiceIdentityScopes.Verify, ServiceIdentityScopes.Control]);
        using var blocked = await h.CreateIncidentAsync("pending", credential: control);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        await using (var scope = h.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().SetStatusAsync(pending.Principal.Id, "in_doubt");
        var recovery = await TokenAsync(h, pending.Principal.ClientId, pending.ClientSecret, [ServiceIdentityScopes.Control]);
        using var stillBlocked = await h.CreateIncidentAsync("pending", credential: recovery);
        Assert.Equal(HttpStatusCode.Forbidden, stillBlocked.StatusCode);
        Assert.Equal(0, (await h.CountsAsync()).Incidents);
    }

    private static ServiceLinkMetadata PendingSnapshot(ServiceIdentityOptions settings, string product, string instance) => new()
    {
        Product = product, ProductVersion = "test", InstanceId = instance, WebBaseUrl = settings.WebBaseUrl, ApiBaseUrl = settings.ApiBaseUrl,
        OauthIssuer = settings.Issuer, Audience = settings.Audience, TokenEndpoint = settings.ApiBaseUrl + "/connect/token",
        OauthMetadataUrl = settings.ApiBaseUrl + "/.well-known/oauth-authorization-server", JwksUri = settings.ApiBaseUrl + "/.well-known/jwks.json",
        ServiceLinkEndpoint = settings.ApiBaseUrl + ServiceLinkContract.EndpointPath, ApprovalEndpoint = settings.WebBaseUrl + "/account/integration-credentials/link/approve",
        CallbackEndpoint = settings.WebBaseUrl + "/account/integration-credentials/link/callback"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_client_issues_narrow_tokens_reconciles_same_receipt_and_survives_rotation_restart(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var created = await CreateManualAsync(h);
        var id = created.GetProperty("client").GetProperty("id").GetGuid();
        var clientId = created.GetProperty("client").GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("clientSecret").GetString()!;
        Assert.Equal(h.NamespaceId, created.GetProperty("client").GetProperty("sourceNamespaceId").GetGuid());
        Assert.False(clientId.StartsWith("rdk_", StringComparison.Ordinal));
        Assert.Equal("https://receiver.example.test/services", created.GetProperty("issuer").GetString());
        Assert.Equal("https://receiver.example.test/connect/token", created.GetProperty("tokenEndpoint").GetString());
        Assert.Equal("rateldesk.services", created.GetProperty("audience").GetString());

        using var list = await AdminAsync(h, HttpMethod.Get, "/api/v1/admin/service-clients");
        var listing = await list.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(secret, listing);
        Assert.DoesNotContain("clientSecret", listing, StringComparison.OrdinalIgnoreCase);
        var token = await TokenAsync(h, clientId, secret, IncidentScopes);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("service:" + id.ToString("N"), jwt.Subject);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == ClaimTypes.Role || c.Type == "roles" || c.Value == "owner");
        Assert.Equal("at+jwt", jwt.Header.Typ);
        Assert.Equal("RS256", jwt.Header.Alg);

        using var first = await h.CreateIncidentAsync("service-lost-response", credential: token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var accepted = await first.Content.ReadAsStringAsync();
        var counts = await h.CountsAsync();
        using var replay = await h.CreateIncidentAsync("service-lost-response", credential: token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(accepted, await replay.Content.ReadAsStringAsync());
        using var lookup = await ResourceAsync(h, token, HttpMethod.Get,
            "/api/v1/integrations/netratel/incident-receipts/service-lost-response");
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        Assert.Equal(accepted, await lookup.Content.ReadAsStringAsync());
        Assert.Equal(counts, await h.CountsAsync());
        using var capabilities = await ResourceAsync(h, token, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        var capability = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("oauth_client_credentials", capability.GetProperty("authenticationModes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(h.NamespaceId.ToString("D"), capability.GetProperty("sourceNamespaceId").GetString());
        using var target = await ResourceAsync(h, token, HttpMethod.Post, "/api/v1/integrations/netratel/targets/validate", new
        { organizationId = h.OrganizationId, customerId = h.CustomerId, categoryIds = new[] { h.CategoryId } });
        Assert.Equal(HttpStatusCode.OK, target.StatusCode);

        using var rotated = await AdminAsync(h, HttpMethod.Post, $"/api/v1/admin/service-clients/{id:D}/rotate", new { expectedCredentialRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var replacement = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync()).RootElement.Clone();
        var nextSecret = replacement.GetProperty("clientSecret").GetString()!;
        Assert.NotEqual(secret, nextSecret);
        Assert.Equal(clientId, replacement.GetProperty("client").GetProperty("clientId").GetString());
        Assert.Equal(id, replacement.GetProperty("client").GetProperty("id").GetGuid());
        Assert.Equal(h.NamespaceId, replacement.GetProperty("client").GetProperty("sourceNamespaceId").GetGuid());
        using var stale = await AdminAsync(h, HttpMethod.Post, $"/api/v1/admin/service-clients/{id:D}/rotate", new { expectedCredentialRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var nextToken = await TokenAsync(h, clientId, nextSecret, IncidentScopes);
        await h.RestartAsync();
        var restartedToken = await TokenAsync(h, clientId, nextSecret, IncidentScopes);
        Assert.Equal(jwt.Header.Kid, new JwtSecurityTokenHandler().ReadJwtToken(restartedToken).Header.Kid);
        using var afterRestart = await h.CreateIncidentAsync("service-lost-response", credential: nextToken);
        Assert.Equal(HttpStatusCode.OK, afterRestart.StatusCode);
        Assert.Equal(accepted, await afterRestart.Content.ReadAsStringAsync());
        Assert.Equal(counts, await h.CountsAsync());
        await h.EditAsync(async db => (await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == 1)).RetireAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1));
        using var obsolete = await ResourceAsync(h, token, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, obsolete.StatusCode);
        await using var scope = h.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var persisted = await db.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == id).ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, row => { Assert.NotEqual(secret, row.SecretHash); Assert.NotEqual(nextSecret, row.SecretHash); Assert.Equal(64, row.SecretHash.Length); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_tokens_refuse_escalation_foreign_targets_and_both_missing_headers(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await CreateManualAsync(h);
        var clientId = client.GetProperty("client").GetProperty("clientId").GetString()!;
        var secret = client.GetProperty("clientSecret").GetString()!;
        using var badScope = await RequestTokenAsync(h, clientId, secret, [ServiceIdentityScopes.IncidentCreate, "Incident.Write"]);
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
        Assert.Equal("invalid_scope", JsonDocument.Parse(await badScope.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        using var badSecret = await RequestTokenAsync(h, clientId, new string('x', 43), IncidentScopes);
        Assert.True(badSecret.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);
        var token = await TokenAsync(h, clientId, secret, IncidentScopes);
        using var missingHeaders = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/") { Content = JsonContent.Create(h.Payload()) };
        missingHeaders.Headers.Authorization = new("Bearer", token);
        using var unkeyed = await h.Client.SendAsync(missingHeaders);
        Assert.Equal(HttpStatusCode.BadRequest, unkeyed.StatusCode);
        using var broadIncidentRead = await ResourceAsync(h, token, HttpMethod.Get, "/api/v1/incidents/");
        Assert.Equal(HttpStatusCode.Forbidden, broadIncidentRead.StatusCode);
        using var admin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/service-clients");
        admin.Headers.Authorization = new("Bearer", token);
        using var forbiddenAdmin = await h.Client.SendAsync(admin);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAdmin.StatusCode);
        using var foreignSource = await ResourceAsync(h, token, HttpMethod.Get, "/api/v1/integrations/netratel/incident-receipts/missing", source: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, foreignSource.StatusCode);
        var foreign = h.Payload(); foreign.OrganizationId = Guid.NewGuid().ToString("D"); foreign.CustomerId = Guid.NewGuid().ToString("D");
        using var foreignTarget = await h.CreateIncidentAsync("foreign-target", foreign, credential: token);
        Assert.Equal(HttpStatusCode.Forbidden, foreignTarget.StatusCode);
        var onlyRead = await TokenAsync(h, clientId, secret, [ServiceIdentityScopes.IncidentReceipts]);
        using var insufficient = await h.CreateIncidentAsync("scope-denied", credential: onlyRead);
        Assert.Equal(HttpStatusCode.Forbidden, insufficient.StatusCode);
        Assert.Equal(0, (await h.CountsAsync()).Incidents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_tokens_observe_revocation_and_current_mapping_on_another_replica(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await CreateManualAsync(h);
        var id = client.GetProperty("client").GetProperty("id").GetGuid();
        var token = await TokenAsync(h, client.GetProperty("client").GetProperty("clientId").GetString()!, client.GetProperty("clientSecret").GetString()!, IncidentScopes);
        await h.AddReplicaAsync();
        using var first = await h.CreateIncidentAsync("current-authority", replica: 1, credential: token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        foreach (var entity in new[] { "organization", "customer", "source" })
        {
            await h.SetEnabledAsync(entity, false);
            using var denied = await h.CreateIncidentAsync("current-authority", replica: 1, credential: token);
            Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            await h.SetEnabledAsync(entity, true);
        }
        await h.EditAsync(async db => (await db.IncidentReceiverPrincipalBindings.SingleAsync(x => x.PrincipalKind == "service_principal")).IsEnabled = false);
        using var lostBinding = await h.CreateIncidentAsync("current-authority", replica: 1, credential: token);
        Assert.Equal(HttpStatusCode.Forbidden, lostBinding.StatusCode);
        await h.EditAsync(async db => (await db.IncidentReceiverPrincipalBindings.SingleAsync(x => x.PrincipalKind == "service_principal")).IsEnabled = true);
        using var revoked = await AdminAsync(h, HttpMethod.Post, $"/api/v1/admin/service-clients/{id:D}/revoke");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using var cached = await h.CreateIncidentAsync("current-authority", replica: 1, credential: token);
        Assert.Equal(HttpStatusCode.Unauthorized, cached.StatusCode);
        Assert.Equal(1, (await h.CountsAsync()).Receipts);
        using var ordinary = await h.CreateIncidentAsync("existing-personal");
        Assert.Equal(HttpStatusCode.Created, ordinary.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validly_signed_wrong_claims_and_expiry_are_refused_and_jwks_rotation_preserves_live_tokens(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await CreateManualAsync(h);
        var token = await TokenAsync(h, client.GetProperty("client").GetProperty("clientId").GetString()!, client.GetProperty("clientSecret").GetString()!, IncidentScopes);
        foreach (var change in new[] { "issuer", "audience", "purpose", "peer", "tenant", "scope", "algorithm", "expired" })
        {
            var altered = await AlterTokenAsync(h, token, change);
            using var refused = await ResourceAsync(h, altered, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        using var jwks = await h.Client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, jwks.StatusCode);
        var jwksJson = JsonDocument.Parse(await jwks.Content.ReadAsStringAsync()).RootElement;
        Assert.All(jwksJson.GetProperty("keys").EnumerateArray(), key =>
        {
            Assert.Equal("RSA", key.GetProperty("kty").GetString());
            foreach (var privateName in new[] { "d", "p", "q", "dp", "dq", "qi", "k" }) Assert.False(key.TryGetProperty(privateName, out _));
        });
        await using (var scope = h.App.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<ServiceSigningKeyStore>().RotateAsync();
        using var oldValid = await ResourceAsync(h, token, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        Assert.Equal(HttpStatusCode.OK, oldValid.StatusCode);
        var afterRotation = await TokenAsync(h, client.GetProperty("client").GetProperty("clientId").GetString()!, client.GetProperty("clientSecret").GetString()!, IncidentScopes);
        Assert.NotEqual(new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Kid, new JwtSecurityTokenHandler().ReadJwtToken(afterRotation).Header.Kid);
        using var currentJwks = await h.Client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(2, JsonDocument.Parse(await currentJwks.Content.ReadAsStringAsync()).RootElement.GetProperty("keys").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_protected_signing_key_fails_closed_and_recovers_the_same_issuer_key(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await CreateManualAsync(h);
        var clientId = client.GetProperty("client").GetProperty("clientId").GetString()!;
        var secret = client.GetProperty("clientSecret").GetString()!;
        var original = await TokenAsync(h, clientId, secret, IncidentScopes);
        var kid = new JwtSecurityTokenHandler().ReadJwtToken(original).Header.Kid;
        string protectedKey = string.Empty;
        await h.EditAsync(async db =>
        {
            var key = await db.Set<ServiceSigningKey>().SingleAsync();
            protectedKey = key.ProtectedPrivateKey;
            key.ProtectedPrivateKey = "unavailable-protected-key";
        });
        await h.RestartAsync();
        using (var unavailable = await RequestTokenAsync(h, clientId, secret, IncidentScopes))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("temporarily_unavailable", JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
            Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
        }
        using (var jwks = await h.Client.GetAsync("/.well-known/jwks.json"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, jwks.StatusCode);
        await h.EditAsync(async db =>
        {
            var key = Assert.Single(await db.Set<ServiceSigningKey>().ToListAsync());
            Assert.Equal(kid, key.Kid);
            key.ProtectedPrivateKey = protectedKey;
        });
        await h.RestartAsync();
        var recovered = await TokenAsync(h, clientId, secret, IncidentScopes);
        Assert.Equal(kid, new JwtSecurityTokenHandler().ReadJwtToken(recovered).Header.Kid);
        using var priorToken = await ResourceAsync(h, original, HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        Assert.Equal(HttpStatusCode.OK, priorToken.StatusCode);
    }

    private static async Task<JsonElement> CreateManualAsync(IncidentReceiverTests.Harness h)
    {
        using var response = await AdminAsync(h, HttpMethod.Post, "/api/v1/admin/service-clients", new
        {
            name = "Synthetic NetRatel", organizationId = h.OrganizationId, peerInstanceId = "netratel-synthetic",
            peerTenantId = "17", scopes = IncidentScopes, customerIds = new[] { h.CustomerId }, sourceInstanceId = h.SourceId
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> AdminAsync(IncidentReceiverTests.Harness h, HttpMethod method, string path, object? payload = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Synthetic-Admin", "true");
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await h.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> RequestTokenAsync(IncidentReceiverTests.Harness h, string clientId, string secret, string[] scopes) =>
        h.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret, ["scope"] = string.Join(' ', scopes) }));

    private static async Task<string> TokenAsync(IncidentReceiverTests.Harness h, string clientId, string secret, string[] scopes)
    {
        using var response = await RequestTokenAsync(h, clientId, secret, scopes);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> ResourceAsync(IncidentReceiverTests.Harness h, string token, HttpMethod method, string path, object? body = null, Guid? source = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-NetRatel-Source-Instance", (source ?? h.SourceId).ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await h.Client.SendAsync(request);
    }

    private static async Task<string> AlterTokenAsync(IncidentReceiverTests.Harness h, string token, string change)
    {
        var original = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var claims = original.Claims.Where(x => x.Type is not ("iss" or "aud" or "nbf" or "exp" or "iat")).ToList();
        var type = change switch
        {
            "purpose" => "token_use", "peer" => ServiceIdentityClaims.PeerInstanceId,
            "tenant" => ServiceIdentityClaims.OrganizationId, "scope" => "scope", _ => null
        };
        if (type is not null) { claims.RemoveAll(x => x.Type == type); claims.Add(new(type, "unapproved-value")); }
        await using var scope = h.App.Services.CreateAsyncScope();
        using var key = await scope.ServiceProvider.GetRequiredService<ServiceSigningKeyStore>().GetSigningKeyAsync();
        var now = DateTime.UtcNow;
        var altered = new JwtSecurityToken(change == "issuer" ? "https://foreign.example.test" : original.Issuer,
            change == "audience" ? "unapproved-audience" : original.Audiences.Single(), claims,
            change == "expired" ? now.AddMinutes(-10) : now.AddSeconds(-1), change == "expired" ? now.AddMinutes(-2) : now.AddMinutes(4),
            new SigningCredentials(key.Key, change == "algorithm" ? SecurityAlgorithms.RsaSha384 : SecurityAlgorithms.RsaSha256));
        altered.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(altered);
    }
}
