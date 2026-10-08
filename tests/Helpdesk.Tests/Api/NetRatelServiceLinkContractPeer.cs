using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Tests.Api;

/// <summary>
/// Synthetic NetRatel protocol peer with real HTTP, form-encoded OAuth grants and RSA JWT checks.
/// This is shared-contract evidence, not interoperability evidence against a NetRatel product build.
/// </summary>
internal sealed class NetRatelServiceLinkContractPeer : IAsyncDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private WebApplication app = null!;
    private ServiceLinkRequestDescriptor descriptor = null!;
    private ServiceLinkGrantSummary summary = null!;
    private ServiceDirectionalCredential? inbound;
    private ServiceDirectionalCredential? outbound;
    private string? exchangeFingerprint;
    private ServiceLinkExchangeResponse? exchangeResponse;
    private ServiceLinkExchangeRequest? initiatorExchangeRequest;
    private ServiceLinkLifecycleRequest? rotationOffer;
    private string pairingCode = "";
    private string? initiatorReceipt;
    private string? responderReceipt;
    private string? commitId;
    private string? abortId;
    private bool initiatorRole;
    private string? codeVerifier;
    public string BrowserState { get; private set; } = "";
    private bool active;
    private bool revoked;
    private volatile bool losePreparedAcknowledgementResponses;
    private readonly ConcurrentQueue<string> preparedAcknowledgementOperations = new();
    private readonly ConcurrentQueue<string> exchangeRequestFingerprints = new();
    private readonly ConcurrentDictionary<string, string> preparedAcknowledgements = new(StringComparer.Ordinal);
    public string BaseUrl { get; }
    public string InstanceId { get; } = Guid.NewGuid().ToString("D");
    public string SourceInstanceId { get; } = Guid.NewGuid().ToString("D");
    public string TenantId { get; } = "7";
    public string LinkId => summary.LinkId;
    public string GrantHash { get; private set; } = "";
    public int TokenAcquisitions { get; private set; }
    public int AuthenticatedVerifications { get; private set; }
    public int AuthenticatedStatusReads { get; private set; }
    public bool LoseExchangeResponseOnce { get; set; }
    public bool LoseRotationOfferResponseOnce { get; set; }
    public bool HoldLifecycleStatus { get; set; }
    public bool LosePreparedAcknowledgementResponses
    {
        get => losePreparedAcknowledgementResponses;
        set => losePreparedAcknowledgementResponses = value;
    }
    public string[] PreparedAcknowledgementOperationIds => preparedAcknowledgementOperations.ToArray();
    public string[] ExchangeRequestFingerprints => exchangeRequestFingerprints.ToArray();
    public string? InvalidMetadataMember { get; set; }
    public ServiceLinkMetadata? DiscoveryMetadata { get; set; }
    public ServiceDirectionalCredential InboundCredential => inbound!;
    public ServiceDirectionalCredential OutboundCredential => outbound!;
    public bool HasOutboundCredential => outbound is not null;
    public ServiceLinkGrantSummary Summary => summary;
    public ServiceLinkExchangeRequest InitiatorExchangeRequest => initiatorExchangeRequest
        ?? throw new InvalidOperationException("The initial exchange request is required.");
    public ServiceLinkMetadata Metadata { get; }

    private NetRatelServiceLinkContractPeer()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}";
        Metadata = new()
        {
            Product = "netratel", ProductVersion = "synthetic-shared-contract", InstanceId = InstanceId,
            SourceInstanceId = SourceInstanceId, WebBaseUrl = BaseUrl, ApiBaseUrl = BaseUrl,
            OauthIssuer = BaseUrl, OauthMetadataUrl = BaseUrl + "/.well-known/oauth-authorization-server",
            TokenEndpoint = BaseUrl + "/connect/token", JwksUri = BaseUrl + "/.well-known/jwks",
            Audience = "netratel-test-api", ServiceLinkEndpoint = BaseUrl + ServiceLinkContract.EndpointPath,
            ApprovalEndpoint = BaseUrl + "/account/integration-credentials/link/approve",
            CallbackEndpoint = BaseUrl + "/account/integration-credentials/link/callback",
            PermissionProfiles = [new("netratel.orchestration.v1",
                ["netratel.orchestration.read", "netratel.orchestration.invoke"],
                [new("GET", "/api/v1/system/m2m/ping", "netratel.orchestration.read"),
                 new("GET", "/internal/health", "netratel.orchestration.read"),
                 new("GET", "/internal/catalog/jobs", "netratel.orchestration.read"),
                 new("GET", "/internal/catalog/tenants", "netratel.orchestration.read"),
                 new("GET", "/internal/catalog/request-definitions", "netratel.orchestration.read"),
                 new("POST", "/internal/ingest", "netratel.orchestration.invoke")]),
                new(ServiceLinkContract.IncidentOnlyCapability, [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope],
                [new("POST", ServiceLinkContract.EndpointPath + "/links/{link_id}/verify", ServiceLinkContract.VerifyScope),
                 new("GET", ServiceLinkContract.EndpointPath + "/links/{link_id}/status", ServiceLinkContract.ControlScope)])]
        };
    }

    public static async Task<NetRatelServiceLinkContractPeer> CreateAsync(
        Action<WebApplication, NetRatelServiceLinkContractPeer>? configure = null)
    {
        var peer = new NetRatelServiceLinkContractPeer();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls(peer.BaseUrl);
        peer.app = builder.Build();
        peer.Map(); configure?.Invoke(peer.app, peer); await peer.app.StartAsync();
        return peer;
    }

    /// <summary>Simulates an authenticated peer administrator approving the exact proposed ceiling.</summary>
    public async Task<ServiceLinkCallbackRequest> ApproveAsync(string initiatorBaseUrl, string attemptId,
        string browserState, string sessionBinding)
    {
        descriptor = await client.GetFromJsonAsync<ServiceLinkRequestDescriptor>(
            initiatorBaseUrl + ServiceLinkContract.EndpointPath + "/requests/" + attemptId, ServiceLinkCanonicalJson.Json)
            ?? throw new InvalidOperationException("Missing initiator descriptor.");
        if (descriptor.ExpectedResponderInstanceId != InstanceId ||
            ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash") != descriptor.DescriptorHash)
            throw new InvalidOperationException("Descriptor identity/hash differs.");
        var grants = descriptor.RequestedGrants.Select(grant => grant with
        {
            CallerTenantId = grant.CallerProduct == "netratel" ? TenantId : grant.CallerTenantId,
            TargetTenantId = grant.TargetProduct == "netratel" ? TenantId : grant.TargetTenantId,
            ResourceConstraints = grant.TargetProduct == "netratel"
                ? grant.ResourceConstraints with { TenantId = TenantId, RequestDefinitionIds = ServiceLinkValidation.IncidentOnlyGrant(grant) ? [] : ["synthetic-request-definition"] }
                : grant.ResourceConstraints
        }).ToArray();
        grants = ServiceLinkValidation.Grants(grants, descriptor.InitiatorEndpointSnapshot, Metadata);
        summary = new()
        {
            AttemptId = descriptor.AttemptId, LinkId = ServiceLinkValidation.NewId(),
            DescriptorHash = descriptor.DescriptorHash, ExpiresAt = descriptor.ExpiresAt,
            InitiatorInstanceId = descriptor.InitiatorInstanceId, ResponderInstanceId = InstanceId,
            InitiatorEndpointSnapshot = descriptor.InitiatorEndpointSnapshot,
            ResponderEndpointSnapshot = Metadata, Grants = grants
        };
        GrantHash = ServiceLinkCanonicalJson.HashObject(summary);
        pairingCode = ServiceLinkValidation.Proof();
        return new(attemptId, pairingCode, browserState, InstanceId, BaseUrl, sessionBinding);
    }

    public ServiceLinkRequestDescriptor PrepareInitiator(ServiceLinkMetadata responder, string? organizationId,
        string customerId, DateTimeOffset now, bool includeCallback = true, bool mismatchInitiatorTenant = false)
    {
        initiatorRole = true; codeVerifier = ServiceLinkValidation.Proof(); BrowserState = ServiceLinkValidation.Proof();
        descriptor = new()
        {
            AttemptId = ServiceLinkValidation.NewId(), ExpiresAt = ServiceLinkValidation.Timestamp(now.ToUnixTimeSeconds() + 120),
            InitiatorInstanceId = InstanceId, InitiatorTenantId = TenantId, ExpectedResponderInstanceId = responder.InstanceId,
            RequestedResponderTenantId = organizationId, InitiatorEndpointSnapshot = Metadata,
            ResponderEndpointSnapshot = responder, InitiatorCallbackEndpoint = Metadata.CallbackEndpoint,
            CodeChallenge = ServiceLinkValidation.Challenge(codeVerifier),
            RequestedGrants =
            [
                new()
                {
                    DirectionId = ServiceLinkContract.InitiatorToResponder, CallerSnapshot = "initiator", TargetSnapshot = "responder",
                    CallerProduct = "netratel", CallerInstanceId = InstanceId, CallerTenantId = TenantId,
                    TargetProduct = "rateldesk", TargetInstanceId = responder.InstanceId, TargetTenantId = organizationId ?? "",
                    Issuer = responder.OauthIssuer, Audience = responder.Audience,
                    Capabilities = includeCallback ? ["rateldesk.incident-create.v1", "rateldesk.orchestration.callback.v1"] : ["rateldesk.incident-create.v1"],
                    Scopes = includeCallback
                        ? ["rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.incidents.create", "rateldesk.orchestration.callback"]
                        : ["rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.incidents.create"],
                    ResourceConstraints = new() { OrganizationId = organizationId, CustomerIds = [customerId] },
                    SourceInstanceId = SourceInstanceId
                },
                new()
                {
                    DirectionId = ServiceLinkContract.ResponderToInitiator, CallerSnapshot = "responder", TargetSnapshot = "initiator",
                    CallerProduct = "rateldesk", CallerInstanceId = responder.InstanceId, CallerTenantId = organizationId ?? "",
                    TargetProduct = "netratel", TargetInstanceId = InstanceId, TargetTenantId = TenantId,
                    Issuer = BaseUrl, Audience = Metadata.Audience, Capabilities = ["netratel.orchestration.v1"],
                    Scopes = ["netratel.orchestration.invoke", "netratel.orchestration.read"],
                    ResourceConstraints = new() { TenantId = TenantId, RequestDefinitionIds = ["synthetic-request-definition"] }
                }
            ]
        };
        if (mismatchInitiatorTenant)
        {
            var foreignTenant = TenantId + "0";
            descriptor = descriptor with { RequestedGrants = descriptor.RequestedGrants.Select(grant => grant with
            {
                CallerTenantId = grant.CallerProduct == "netratel" ? foreignTenant : grant.CallerTenantId,
                TargetTenantId = grant.TargetProduct == "netratel" ? foreignTenant : grant.TargetTenantId,
                ResourceConstraints = grant.TargetProduct == "netratel" ? grant.ResourceConstraints with { TenantId = foreignTenant } : grant.ResourceConstraints
            }).ToArray() };
        }
        descriptor = descriptor with { DescriptorHash = ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash") };
        return descriptor;
    }

    public async Task ExchangeAsInitiatorAsync(ServiceLinkNavigation navigation, bool loseResponse = false)
    {
        await PrepareInitiatorExchangeAsync(navigation);
        using var exchanged = await RepeatInitiatorExchangeAsync(loseResponse: loseResponse);
        exchanged.EnsureSuccessStatusCode();
        exchangeResponse = await exchanged.Content.ReadFromJsonAsync<ServiceLinkExchangeResponse>() ?? throw new InvalidOperationException("Missing exchange.");
        outbound = exchangeResponse.CredentialForInitiator;
    }

    public async Task PrepareInitiatorExchangeAsync(ServiceLinkNavigation navigation)
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navigation.NavigationUrl).Query);
        pairingCode = query["pairing_code"].ToString();
        using var review = await client.PostAsJsonAsync(descriptor.ResponderEndpointSnapshot.ServiceLinkEndpoint + "/attempts/" + descriptor.AttemptId + "/review",
            new ServiceLinkReviewRequest(ServiceLinkContract.Version, descriptor.AttemptId, pairingCode, codeVerifier!, descriptor.DescriptorHash));
        review.EnsureSuccessStatusCode();
        var approved = await review.Content.ReadFromJsonAsync<ServiceLinkReviewResponse>() ?? throw new InvalidOperationException("Missing review.");
        summary = approved.GrantSummary; GrantHash = approved.GrantHash;
        var reverse = summary.Grants.Single(x => x.DirectionId == ServiceLinkContract.ResponderToInitiator);
        inbound = new()
        {
            ClientId = "synthetic-netratel-" + ServiceLinkValidation.NewId(), ClientSecret = ServiceLinkValidation.Proof(),
            Issuer = BaseUrl, TokenEndpoint = Metadata.TokenEndpoint, Audience = Metadata.Audience,
            Scopes = reverse.Scopes, CallerInstanceId = reverse.CallerInstanceId, CallerTenantId = reverse.CallerTenantId,
            TargetInstanceId = InstanceId, TargetTenantId = TenantId
        };
        initiatorExchangeRequest = new ServiceLinkExchangeRequest(ServiceLinkContract.Version, descriptor.AttemptId,
            pairingCode, codeVerifier!, descriptor.DescriptorHash, GrantHash, ServiceLinkValidation.NewId(), inbound);
    }

    public Task<HttpResponseMessage> RepeatInitiatorExchangeAsync(bool changeBody = false, bool reorderJson = false, bool loseResponse = false,
        bool reorderCredentialScopes = false, bool changeCredentialSecret = false, bool quoteCredentialRevision = false)
    {
        var request = initiatorExchangeRequest ?? throw new InvalidOperationException("The initial exchange request is required.");
        if (changeBody) request = request with { InitiatorConsentId = ServiceLinkValidation.NewId() };
        if (reorderCredentialScopes) request = request with { CredentialForResponder = request.CredentialForResponder with { Scopes = request.CredentialForResponder.Scopes.Reverse().ToArray() } };
        if (changeCredentialSecret) request = request with { CredentialForResponder = request.CredentialForResponder with { ClientSecret = ServiceLinkValidation.Proof() } };
        var endpoint = descriptor.ResponderEndpointSnapshot.ServiceLinkEndpoint + "/attempts/" + descriptor.AttemptId + "/exchange";
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (quoteCredentialRevision)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request))!;
            node["credential_for_responder"]!["credential_revision"] = request.CredentialForResponder.CredentialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            message.Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        }
        else if (!reorderJson) message.Content = JsonContent.Create(request);
        else
        {
            using var original = JsonDocument.Parse(JsonSerializer.Serialize(request));
            var reversed = original.RootElement.EnumerateObject().Reverse().ToDictionary(x => x.Name, x => x.Value.Clone());
            message.Content = new StringContent(JsonSerializer.Serialize(reversed), System.Text.Encoding.UTF8, "application/json");
        }
        if (loseResponse) message.Headers.Add("X-Synthetic-Lost-Response", "true");
        return client.SendAsync(message);
    }

    public void DecideAbort()
    {
        if (!initiatorRole || commitId is not null) throw new InvalidOperationException("Only the undecided coordinator may abort.");
        abortId ??= ServiceLinkValidation.NewId();
        active = false;
    }

    public async Task<HttpResponseMessage> ReviewAsInitiatorAsync(ServiceLinkNavigation navigation)
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navigation.NavigationUrl).Query);
        return await client.PostAsJsonAsync(descriptor.ResponderEndpointSnapshot.ServiceLinkEndpoint + "/attempts/" + descriptor.AttemptId + "/review",
            new ServiceLinkReviewRequest(ServiceLinkContract.Version, descriptor.AttemptId, query["pairing_code"].ToString(), codeVerifier!, descriptor.DescriptorHash));
    }

    public void DecideCommit()
    {
        if (initiatorReceipt is null || responderReceipt is null) throw new InvalidOperationException("Both real OAuth verification receipts are required.");
        commitId ??= ServiceLinkValidation.NewId();
    }

    public async Task DeliverDecidedCommitAsync()
    {
        if (commitId is null) throw new InvalidOperationException("The coordinator must durably decide before delivery.");
        using var tokenResponse = await client.PostAsync(outbound!.TokenEndpoint, TokenForm(outbound));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        using var committed = await SendAsync("commit", token, new()
        {
            OperationId = ServiceLinkValidation.NewId(), AttemptId = descriptor.AttemptId, LinkId = summary.LinkId,
            LinkRevision = 1, GrantHash = GrantHash, CommitId = commitId, DescriptorHash = descriptor.DescriptorHash,
            InitiatorVerificationReceiptId = initiatorReceipt, ResponderVerificationReceiptId = responderReceipt
        });
        committed.EnsureSuccessStatusCode(); active = true;
    }

    public Task DisconnectAsync() => app.StopAsync();

    public ServiceDirectionalCredential RotationCandidate => rotationOffer?.CredentialForCaller
        ?? throw new InvalidOperationException("The issuer has not delivered an offer.");

    public async Task CompleteOfferedRotationAsync(Func<Task> restartAfterActivationLoss, Func<Task> restartAfterSwitchLoss)
    {
        var offer = rotationOffer ?? throw new InvalidOperationException("Missing offer.");
        var candidate = offer.CredentialForCaller!;
        using var tokenResponse = await client.PostAsync(candidate.TokenEndpoint, TokenForm(candidate));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        using var verify = await SendAsync("verify", token, new()
        {
            OperationId = ServiceLinkValidation.NewId(), AttemptId = descriptor.AttemptId, LinkId = summary.LinkId,
            LinkRevision = 1, GrantHash = GrantHash, DirectionId = offer.DirectionId,
            CredentialRevision = candidate.CredentialRevision, RotationId = offer.RotationId
        });
        verify.EnsureSuccessStatusCode();
        var receipt = (await verify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verification_receipt_id").GetString();
        var transition = new ServiceLinkLifecycleRequest
        {
            OperationId = ServiceLinkValidation.NewId(), LinkId = summary.LinkId, LinkRevision = 1, GrantHash = GrantHash,
            RotationId = offer.RotationId, RotationPhase = "verified", DirectionId = offer.DirectionId,
            ExpectedCurrentCredentialRevision = offer.ExpectedCurrentCredentialRevision,
            SuccessorCredentialRevision = candidate.CredentialRevision, SuccessorVerificationReceiptId = receipt
        };
        try
        {
            using var lost = await SendAsync("rotate", token, transition, loseResponse: true);
            throw new InvalidOperationException($"The injected activation response was unexpectedly delivered: {(int)lost.StatusCode}.");
        }
        catch (HttpRequestException) { await restartAfterActivationLoss(); }
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get,
            (initiatorRole ? descriptor.ResponderEndpointSnapshot : descriptor.InitiatorEndpointSnapshot).ServiceLinkEndpoint + "/links/" + summary.LinkId + "/status");
        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var statusResponse = await client.SendAsync(statusRequest);
        statusResponse.EnsureSuccessStatusCode();
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        var activation = status.GetProperty("rotations").EnumerateArray().Single(x => x.GetProperty("rotation_id").GetString() == offer.RotationId)
            .GetProperty("activate_decision_id").GetString();
        outbound = candidate;
        using var successorTokenResponse = await client.PostAsync(candidate.TokenEndpoint, TokenForm(candidate));
        successorTokenResponse.EnsureSuccessStatusCode();
        var successorToken = (await successorTokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        var switchRequest = transition with
        {
            OperationId = ServiceLinkValidation.NewId(), RotationPhase = "switched", ActivateDecisionId = activation, CallerSwitchRevision = 2
        };
        try
        {
            using var lost = await SendAsync("rotate", successorToken, switchRequest, loseResponse: true);
            throw new InvalidOperationException($"The injected switch response was unexpectedly delivered: {(int)lost.StatusCode}.");
        }
        catch (HttpRequestException) { await restartAfterSwitchLoss(); }
        using var switched = await SendAsync("rotate", successorToken, switchRequest);
        switched.EnsureSuccessStatusCode();
    }

    private void Map()
    {
        app.MapGet(ServiceLinkContract.MetadataPath, () =>
        {
            if (InvalidMetadataMember is null) return Results.Json<object>(DiscoveryMetadata ?? Metadata);
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Metadata))!;
            switch (InvalidMetadataMember)
            {
                case "profile": node["permission_profiles"]![0] = null; break;
                case "operation": node["permission_profiles"]![0]!["operations"]![0] = null; break;
                default: node[InvalidMetadataMember] = null; break;
            }
            return Results.Json<object>(node);
        });
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
        {
            issuer = BaseUrl, token_endpoint = Metadata.TokenEndpoint, jwks_uri = Metadata.JwksUri,
            grant_types_supported = new[] { "client_credentials" }, token_endpoint_auth_methods_supported = new[] { "client_secret_post" }
        }));
        app.MapGet("/.well-known/jwks", () =>
        {
            var key = rsa.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "synthetic-peer-key",
                n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } });
        });
        app.MapPost("/connect/token", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            if (inbound is null || form["grant_type"] != "client_credentials" || form["client_id"] != inbound.ClientId ||
                !ServiceLinkValidation.Same(form["client_secret"], inbound.ClientSecret)) return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            var scopes = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var allowed = new[] { ServiceLinkContract.VerifyScope, ServiceLinkContract.ControlScope }
                .Concat(active && !revoked ? inbound.Scopes : []);
            if (scopes.Length == 0 || scopes.Any(scope => !allowed.Contains(scope, StringComparer.Ordinal)))
                return Results.Json(new { error = "invalid_scope" }, statusCode: 400);
            TokenAcquisitions++;
            var claims = new[] { new Claim("client_id", inbound.ClientId), new Claim("scope", string.Join(' ', scopes)),
                new Claim("link_id", summary.LinkId), new Claim("grant_hash", GrantHash), new Claim("tenant_id", TenantId),
                new Claim("auth_mode", "service"), new Claim("credential_revision", "1") };
            var token = new JwtSecurityToken(BaseUrl, Metadata.Audience, claims, DateTime.UtcNow.AddSeconds(-1),
                DateTime.UtcNow.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "synthetic-peer-key" }, SecurityAlgorithms.RsaSha256));
            return Results.Json(new { access_token = new JwtSecurityTokenHandler().WriteToken(token), token_type = "Bearer", expires_in = 300, scope = string.Join(' ', scopes) });
        });
        var protocol = app.MapGroup(ServiceLinkContract.EndpointPath);
        protocol.MapGet("/requests/{attemptId}", (string attemptId) =>
            initiatorRole && descriptor.AttemptId == attemptId ? Results.Json(descriptor) : Results.NotFound());
        protocol.MapPost("/attempts/{attemptId}/review", (string attemptId, ServiceLinkReviewRequest request) =>
            Proof(request, attemptId) ? Results.Json(new ServiceLinkReviewResponse(summary, GrantHash, "approved")) : Results.Unauthorized());
        protocol.MapPost("/attempts/{attemptId}/exchange", async (string attemptId, ServiceLinkExchangeRequest request, HttpContext http) =>
        {
            if (!Proof(request, attemptId) || request.GrantHash != GrantHash) return Results.Unauthorized();
            var fingerprint = ServiceLinkCanonicalJson.HashObject(request);
            exchangeRequestFingerprints.Enqueue(fingerprint);
            if (exchangeFingerprint is not null)
                return fingerprint == exchangeFingerprint ? Results.Json(exchangeResponse) : Results.Conflict();
            var reverse = summary.Grants.Single(x => x.DirectionId == ServiceLinkContract.ResponderToInitiator);
            ServiceLinkValidation.Credential(request.CredentialForResponder, reverse, summary.InitiatorEndpointSnapshot);
            outbound = request.CredentialForResponder;
            var forward = summary.Grants.Single(x => x.DirectionId == ServiceLinkContract.InitiatorToResponder);
            inbound = new()
            {
                ClientId = "synthetic-netratel-" + ServiceLinkValidation.NewId(), ClientSecret = ServiceLinkValidation.Proof(),
                Issuer = BaseUrl, TokenEndpoint = Metadata.TokenEndpoint, Audience = Metadata.Audience,
                Scopes = forward.Scopes, CallerInstanceId = forward.CallerInstanceId, CallerTenantId = forward.CallerTenantId,
                TargetInstanceId = InstanceId, TargetTenantId = TenantId
            };
            exchangeFingerprint = fingerprint;
            exchangeResponse = new(ServiceLinkContract.Version, attemptId, summary.LinkId, 1, GrantHash, "prepared", inbound);
            if (LoseExchangeResponseOnce)
            {
                LoseExchangeResponseOnce = false;
                http.Abort();
            }
            await Task.CompletedTask;
            return Results.Json(exchangeResponse);
        });
        protocol.MapPost("/links/{linkId}/verify", async (string linkId, ServiceLinkLifecycleRequest request, HttpContext http) =>
        {
            if (!Authorize(http, ServiceLinkContract.VerifyScope, linkId) || request.LinkId != linkId || request.GrantHash != GrantHash)
                return Results.Unauthorized();
            if (initiatorRole) responderReceipt ??= ServiceLinkValidation.NewId();
            else initiatorReceipt ??= ServiceLinkValidation.NewId();
            AuthenticatedVerifications++;
            if (initiatorRole ? initiatorReceipt is null : responderReceipt is null) await ProbeInitiatorAsync();
            return Results.Json(new { contract = ServiceLinkContract.Version, link_id = linkId, link_revision = 1,
                grant_hash = GrantHash, lifecycle_state = "verified", verification_receipt_id = initiatorRole ? responderReceipt : initiatorReceipt,
                caller_instance_id = inbound!.CallerInstanceId, caller_tenant_id = inbound.CallerTenantId,
                target_instance_id = InstanceId, target_tenant_id = TenantId, credential_revision = 1,
                rotation_id = (string?)null, verified_at = ServiceLinkValidation.Timestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) });
        });
        protocol.MapGet("/links/{linkId}/status", (string linkId, HttpContext http) =>
        {
            if (!Authorize(http, ServiceLinkContract.ControlScope, linkId)) return Results.Unauthorized();
            AuthenticatedStatusReads++;
            return HoldLifecycleStatus ? Results.StatusCode(503) : Results.Json(Status());
        });
        protocol.MapPost("/links/{linkId}/ack", (string linkId, ServiceLinkLifecycleRequest request, HttpContext http) =>
        {
            if (!Authorize(http, ServiceLinkContract.ControlScope, linkId)) return Results.Unauthorized();
            var acknowledgementId = ServiceLinkValidation.NewId();
            if (request.AckPhase == "prepared")
            {
                preparedAcknowledgementOperations.Enqueue(request.OperationId);
                acknowledgementId = preparedAcknowledgements.GetOrAdd(request.OperationId, acknowledgementId);
                if (LosePreparedAcknowledgementResponses) http.Abort();
            }
            return Results.Json(new { contract = ServiceLinkContract.Version, link_id = linkId, link_revision = 1,
                grant_hash = GrantHash, lifecycle_state = active ? "active" : "prepared", acknowledged_phase = request.AckPhase,
                acknowledgement_id = acknowledgementId });
        });
        protocol.MapPost("/links/{linkId}/commit", (string linkId, ServiceLinkLifecycleRequest request, HttpContext http) =>
        {
            if (!Authorize(http, ServiceLinkContract.ControlScope, linkId) || request.GrantHash != GrantHash ||
                request.DescriptorHash != descriptor.DescriptorHash || request.InitiatorVerificationReceiptId != initiatorReceipt ||
                request.ResponderVerificationReceiptId != responderReceipt || (commitId is not null && commitId != request.CommitId))
                return Results.Unauthorized();
            commitId = request.CommitId; active = true;
            return Results.Json(new { contract = ServiceLinkContract.Version, link_id = linkId, link_revision = 1,
                grant_hash = GrantHash, lifecycle_state = "active", decision = "commit", commit_id = commitId, local_inbound_active = true });
        });
        protocol.MapPost("/links/{linkId}/revoke", (string linkId, ServiceLinkLifecycleRequest request, HttpContext http) =>
        {
            if (!Authorize(http, ServiceLinkContract.ControlScope, linkId)) return Results.Unauthorized();
            revoked = true; active = false;
            return Results.Json(new { contract = ServiceLinkContract.Version, link_id = linkId, link_revision = 1,
                grant_hash = GrantHash, lifecycle_state = "revoked", revocation_id = request.RevocationId,
                local_business_revoked = true, local_sender_disabled = true });
        });
        protocol.MapPost("/links/{linkId}/rotate", (string linkId, ServiceLinkLifecycleRequest request, HttpContext http) =>
        {
            if (!Authorize(http, ServiceLinkContract.ControlScope, linkId) || !active || revoked || request.RotationPhase != "offer" ||
                request.CredentialForCaller is null) return Results.Unauthorized();
            var grant = summary.Grants.Single(x => x.TargetProduct == "rateldesk");
            var target = initiatorRole ? summary.ResponderEndpointSnapshot : summary.InitiatorEndpointSnapshot;
            ServiceLinkValidation.Credential(request.CredentialForCaller, grant, target);
            if (request.DirectionId != grant.DirectionId || request.ExpectedCurrentCredentialRevision != outbound!.CredentialRevision ||
                request.SuccessorCredentialRevision != request.CredentialForCaller.CredentialRevision) return Results.Conflict();
            if (rotationOffer is not null && ServiceLinkCanonicalJson.HashObject(rotationOffer) != ServiceLinkCanonicalJson.HashObject(request))
                return Results.Conflict();
            rotationOffer = request;
            if (LoseRotationOfferResponseOnce)
            {
                LoseRotationOfferResponseOnce = false;
                http.Abort();
            }
            return Results.Json(new { contract = ServiceLinkContract.Version, link_id = linkId, link_revision = 1,
                grant_hash = GrantHash, lifecycle_state = "active", rotation_id = request.RotationId,
                rotation_state = "prepared", current_credential_revision = outbound.CredentialRevision,
                successor_credential_revision = request.SuccessorCredentialRevision });
        });
        app.MapGet("/api/v1/system/m2m/ping", (HttpContext http) =>
            active && !revoked && Authorize(http, "netratel.orchestration.read", summary.LinkId)
                ? Results.Json(new { product = "netratel", instance_id = InstanceId, tenant_id = TenantId }) : Results.Unauthorized());
    }

    private async Task ProbeInitiatorAsync()
    {
        var credential = outbound ?? throw new InvalidOperationException("The outbound handoff was not persisted.");
        using var tokenResponse = await client.PostAsync(credential.TokenEndpoint, TokenForm(credential));
        tokenResponse.EnsureSuccessStatusCode();
        var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = tokenJson.GetProperty("access_token").GetString()!;
        using var prepared = await SendAsync("ack", token, new()
        {
            OperationId = ServiceLinkValidation.NewId(), AttemptId = descriptor.AttemptId, LinkId = summary.LinkId,
            LinkRevision = 1, GrantHash = GrantHash, AckPhase = "prepared", PeerOperationId = ServiceLinkValidation.NewId(),
            ExchangeResponseHash = ServiceLinkCanonicalJson.HashObject(exchangeResponse)
        });
        prepared.EnsureSuccessStatusCode();
        using var verified = await SendAsync("verify", token, new()
        {
            OperationId = ServiceLinkValidation.NewId(), AttemptId = descriptor.AttemptId, LinkId = summary.LinkId,
            LinkRevision = 1, GrantHash = GrantHash,
            DirectionId = initiatorRole ? ServiceLinkContract.InitiatorToResponder : ServiceLinkContract.ResponderToInitiator,
            CredentialRevision = 1
        });
        verified.EnsureSuccessStatusCode();
        var receipt = await verified.Content.ReadFromJsonAsync<JsonElement>();
        if (initiatorRole) initiatorReceipt = receipt.GetProperty("verification_receipt_id").GetString();
        else responderReceipt = receipt.GetProperty("verification_receipt_id").GetString();
    }

    private Task<HttpResponseMessage> SendAsync(string operation, string token, ServiceLinkLifecycleRequest body, bool loseResponse = false)
    {
        var message = new HttpRequestMessage(HttpMethod.Post,
            (initiatorRole ? descriptor.ResponderEndpointSnapshot : descriptor.InitiatorEndpointSnapshot).ServiceLinkEndpoint + "/links/" + summary.LinkId + "/" + operation)
        { Content = JsonContent.Create(ServiceLinkLifecycleProjection.Build(operation, body)) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (loseResponse) message.Headers.Add("X-Synthetic-Lost-Response", "true");
        return client.SendAsync(message);
    }

    private object Status() => new
    {
        contract = ServiceLinkContract.Version, link_id = summary.LinkId, link_revision = 1, grant_hash = GrantHash,
        lifecycle_state = abortId is not null ? "expired" : revoked ? "revoked" : active ? "active" : initiatorReceipt is null ? "prepared" : "verified",
        coordinator_instance_id = descriptor.InitiatorInstanceId, attempt_id = descriptor.AttemptId,
        decision = abortId is not null ? "abort" : commitId is null ? "undecided" : "commit", commit_id = commitId, abort_id = abortId,
        descriptor_hash = descriptor.DescriptorHash, local_inbound_ready = inbound is not null,
        local_outbound_persisted = outbound is not null, local_inbound_active = active && !revoked,
        local_business_sender_enabled = active && !revoked, peer_active_acknowledged = active && !revoked,
        initiator_verification_receipt_id = initiatorReceipt, responder_verification_receipt_id = responderReceipt,
        rotations = Array.Empty<object>()
    };

    private bool Proof(ServiceLinkReviewRequest request, string attemptId) =>
        request.Contract == ServiceLinkContract.Version && request.AttemptId == attemptId && attemptId == descriptor.AttemptId &&
        ServiceLinkValidation.Same(request.PairingCode, pairingCode) && request.DescriptorHash == descriptor.DescriptorHash &&
        ServiceLinkValidation.Same(ServiceLinkValidation.Challenge(request.CodeVerifier), descriptor.CodeChallenge);

    private static FormUrlEncodedContent TokenForm(ServiceDirectionalCredential credential) => new(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials", ["client_id"] = credential.ClientId,
        ["client_secret"] = credential.ClientSecret,
        ["scope"] = ServiceLinkContract.VerifyScope + " " + ServiceLinkContract.ControlScope
    });

    internal bool Authorize(HttpContext http, string scope, string linkId)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || inbound is null || summary.LinkId != linkId) return false;
        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(header[7..], new()
            {
                ValidIssuer = BaseUrl, ValidAudience = Metadata.Audience, IssuerSigningKey = new RsaSecurityKey(rsa),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidateLifetime = true, ClockSkew = TimeSpan.Zero
            }, out _);
            return principal.FindFirstValue("client_id") == inbound.ClientId && principal.FindFirstValue("link_id") == linkId &&
                principal.FindFirstValue("grant_hash") == GrantHash && principal.FindFirstValue("tenant_id") == TenantId &&
                principal.FindFirstValue("scope")?.Split(' ').Contains(scope, StringComparer.Ordinal) == true;
        }
        catch (SecurityTokenException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose(); await app.DisposeAsync(); rsa.Dispose();
    }
}
