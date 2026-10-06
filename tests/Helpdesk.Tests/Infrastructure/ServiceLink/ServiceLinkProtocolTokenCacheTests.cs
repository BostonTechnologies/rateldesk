using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.ServiceLink;

public sealed class ServiceLinkProtocolTokenCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_same_authority_requests_share_one_issuance_even_across_cache_instances(bool separateInstances)
    {
        using var harness = new Harness();
        var started = Signal();
        var release = Signal();
        harness.Handler.Reply = async (number, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return TokenResponse(number, 900);
        };
        var other = separateInstances ? harness.NewCache() : harness.Cache;
        var first = harness.GetAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var others = Enumerable.Range(0, 12)
                .Select(_ => other.GetAsync(harness.ResolveAsync, ServiceLinkContract.ControlScope, CancellationToken.None))
                .ToArray();

            Assert.Equal(1, harness.Handler.Requests);
            release.TrySetResult();
            var tokens = await Task.WhenAll(others.Prepend(first));

            Assert.All(tokens, token => Assert.Equal("issued-1", token));
            Assert.Equal(1, harness.Handler.Requests);
            Assert.True(harness.ResolverCalls >= tokens.Length + 1);
            AssertTokenRequest(Assert.Single(harness.Handler.Observed), harness.Context.Credential, ServiceLinkContract.ControlScope);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task A_new_cache_instance_reuses_a_shared_entry_from_an_equivalent_context()
    {
        using var harness = new Harness();
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Context = harness.Context with
        {
            Local = harness.Context.Local with { },
            Peer = harness.Context.Peer with { },
            Credential = harness.Context.Credential with { Scopes = [.. harness.Context.Credential.Scopes] }
        };

        var token = await harness.NewCache().GetAsync(harness.ResolveAsync, ServiceLinkContract.ControlScope, CancellationToken.None);

        Assert.Equal("issued-1", token);
        Assert.Equal(1, harness.Handler.Requests);
    }

    [Fact]
    public async Task Reordering_the_approved_credential_scopes_reuses_the_same_entry()
    {
        using var harness = new Harness();
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Context = harness.Context with
        {
            Credential = harness.Context.Credential with { Scopes = harness.Context.Credential.Scopes.Reverse().ToArray() }
        };

        Assert.Equal("issued-1", await harness.GetAsync());
        Assert.Equal(1, harness.Handler.Requests);
    }

    [Fact]
    public async Task Control_and_verification_scopes_have_separate_tokens_and_unchanged_client_secret_post_requests()
    {
        using var harness = new Harness();

        Assert.Equal("issued-1", await harness.GetAsync(ServiceLinkContract.ControlScope));
        Assert.Equal("issued-2", await harness.GetAsync(ServiceLinkContract.VerifyScope));
        Assert.Equal("issued-1", await harness.GetAsync(ServiceLinkContract.ControlScope));
        Assert.Equal("issued-2", await harness.GetAsync(ServiceLinkContract.VerifyScope));

        Assert.Equal(2, harness.Handler.Requests);
        var requests = harness.Handler.Observed.ToArray();
        AssertTokenRequest(requests[0], harness.Context.Credential, ServiceLinkContract.ControlScope);
        AssertTokenRequest(requests[1], harness.Context.Credential, ServiceLinkContract.VerifyScope);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("link")]
    [InlineData("link-revision")]
    [InlineData("grant")]
    [InlineData("descriptor")]
    [InlineData("direction")]
    [InlineData("mode")]
    [InlineData("profile-revision")]
    [InlineData("rotation")]
    [InlineData("authority-deadline")]
    [InlineData("issuer")]
    [InlineData("token-endpoint")]
    [InlineData("audience")]
    [InlineData("client-id")]
    [InlineData("client-secret")]
    [InlineData("credential-revision")]
    [InlineData("credential-scopes")]
    [InlineData("caller-instance")]
    [InlineData("caller-tenant")]
    [InlineData("target-instance")]
    [InlineData("target-tenant")]
    [InlineData("local-instance")]
    [InlineData("local-source-instance")]
    [InlineData("local-issuer")]
    [InlineData("local-jwks")]
    [InlineData("peer-instance")]
    [InlineData("peer-source-instance")]
    [InlineData("peer-issuer")]
    [InlineData("peer-jwks")]
    public async Task Changed_authority_or_credential_descriptor_cannot_reuse_another_contexts_token(string change)
    {
        using var harness = new Harness();
        var original = harness.Context;
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Context = Changed(original, change);

        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        harness.Context = original;
        Assert.Equal("issued-1", await harness.GetAsync());

        Assert.Equal(2, harness.Handler.Requests);
        var requests = harness.Handler.Observed.ToArray();
        AssertTokenRequest(requests[0], original.Credential, ServiceLinkContract.ControlScope);
        AssertTokenRequest(requests[1], Changed(original, change).Credential, ServiceLinkContract.ControlScope);
    }

    [Theory]
    [InlineData(":")]
    [InlineData("|")]
    public async Task Adjacent_authority_identifiers_cannot_ambiguously_share_tokens(string separator)
    {
        using var harness = new Harness();
        var first = harness.Context with { AttemptId = $"attempt{separator}part", LinkId = "link" };
        var second = harness.Context with { AttemptId = "attempt", LinkId = $"part{separator}link" };
        harness.Context = first;
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Context = second;

        Assert.Equal("issued-2", await harness.GetAsync());
        harness.Context = first;
        Assert.Equal("issued-1", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Theory]
    [InlineData("business.read")]
    [InlineData("")]
    [InlineData("bostec.service-link.control bostec.service-link.verify")]
    public async Task Non_protocol_or_combined_scopes_are_rejected_before_HTTP(string scope)
    {
        using var harness = new Harness();

        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync(scope));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("protocol-scope-required", error.Code);
        Assert.Equal(0, harness.Handler.Requests);
        Assert.Empty(harness.Handler.Observed);
    }

    [Fact]
    public async Task A_current_authority_denial_is_propagated_before_reusing_a_warm_entry()
    {
        using var harness = new Harness();
        Assert.Equal("issued-1", await harness.GetAsync());
        var before = harness.ResolverCalls;
        harness.DenyAuthority = true;

        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync());

        Assert.Equal("test-authority-denied", error.Code);
        Assert.True(harness.ResolverCalls > before);
        Assert.Equal(1, harness.Handler.Requests);
    }

    [Fact]
    public async Task Removing_the_current_private_endpoint_opt_in_prevents_a_warm_cache_hit()
    {
        using var harness = new Harness(allowPrivate: true);
        harness.Context = harness.Context with
        {
            Credential = harness.Context.Credential with { TokenEndpoint = "https://10.1.2.3/connect/token" }
        };
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.CurrentOptions.Value = new ServiceLinkOptions { AllowPrivateHttp = false };

        await Assert.ThrowsAsync<ArgumentException>(() => harness.GetAsync());

        Assert.Equal(1, harness.Handler.Requests);
    }

    [Theory]
    [InlineData(12, 7)]
    [InlineData(900, 45)]
    public async Task Reuse_ends_at_the_expiry_margin_or_45_second_cap(int expiresIn, int reuseSeconds)
    {
        using var harness = new Harness();
        harness.Handler.Reply = (number, _) => Task.FromResult(TokenResponse(number, expiresIn));
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Clock.Advance(TimeSpan.FromSeconds(reuseSeconds - 1));
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Theory]
    [InlineData(20, 10, 15)]
    [InlineData(900, 30, 45)]
    public async Task Slow_HTTP_consumes_the_reuse_lifetime_from_request_start(int expiresIn, int delaySeconds, int reuseSeconds)
    {
        using var harness = new Harness();
        harness.Handler.Reply = (number, _) =>
        {
            if (number == 1) harness.Clock.Advance(TimeSpan.FromSeconds(delaySeconds));
            return Task.FromResult(TokenResponse(number, expiresIn));
        };
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Clock.Advance(TimeSpan.FromSeconds(reuseSeconds - delaySeconds - 1));
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Still_valid_tokens_with_no_safe_reuse_lifetime_are_returned_without_caching(int expiresIn)
    {
        using var harness = new Harness();
        harness.Handler.Reply = (number, _) => Task.FromResult(TokenResponse(number, expiresIn));

        Assert.Equal("issued-1", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Fact]
    public async Task A_still_valid_reply_that_consumed_the_safety_margin_is_returned_without_reuse()
    {
        using var harness = new Harness();
        harness.Handler.Reply = (number, _) =>
        {
            if (number == 1) harness.Clock.Advance(TimeSpan.FromSeconds(6));
            return Task.FromResult(TokenResponse(number, 10));
        };

        Assert.Equal("issued-1", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Fact]
    public async Task An_authority_deadline_prevents_reuse_even_when_the_token_and_45_second_cap_remain_valid()
    {
        using var harness = new Harness();
        harness.Context = harness.Context with { AuthorityExpiresAtUnixSeconds = harness.Clock.GetUtcNow().ToUnixTimeSeconds() + 10 };
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("issued-1", await harness.GetAsync());
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync());

        Assert.Equal(403, error.StatusCode);
        Assert.Equal(1, harness.Handler.Requests);
    }

    [Fact]
    public async Task Authority_expiring_during_HTTP_rejects_the_reply_and_does_not_seed_a_later_authority()
    {
        using var harness = new Harness();
        harness.Context = harness.Context with { AuthorityExpiresAtUnixSeconds = harness.Clock.GetUtcNow().ToUnixTimeSeconds() + 10 };
        harness.Handler.Reply = (number, _) =>
        {
            if (number == 1) harness.Clock.Advance(TimeSpan.FromSeconds(10));
            return Task.FromResult(TokenResponse(number, 900));
        };

        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync());

        Assert.Equal(403, error.StatusCode);
        harness.Context = harness.Context with { AuthorityExpiresAtUnixSeconds = harness.Clock.GetUtcNow().ToUnixTimeSeconds() + 600 };
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Fact]
    public async Task A_reply_that_has_already_expired_is_rejected_and_never_reused()
    {
        using var harness = new Harness();
        harness.Handler.Reply = (number, _) =>
        {
            if (number == 1)
            {
                harness.Clock.Advance(TimeSpan.FromSeconds(10));
                return Task.FromResult(TokenResponse(number, 10));
            }
            return Task.FromResult(TokenResponse(number, 900));
        };

        await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("901")]
    [InlineData("1.5")]
    [InlineData("\"60\"")]
    [InlineData("null")]
    public async Task Invalid_expiry_responses_are_rejected_and_do_not_poison_the_cache(string expiresInJson)
    {
        using var harness = new Harness();
        harness.Handler.Reply = (number, _) => Task.FromResult(number == 1
            ? JsonResponse($"{{\"access_token\":\"issued-1\",\"token_type\":\"Bearer\",\"expires_in\":{expiresInJson}}}")
            : TokenResponse(number, 900));

        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync());

        Assert.Equal("invalid-token-response", error.Code);
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Theory]
    [InlineData("{\"access_token\":\"issued-1\",\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"issued-1\",\"token_type\":\"Bearer\",\"expires_in\":60,\"expires_in\":60}")]
    [InlineData("{\"access_token\":\"issued-1\",\"token_type\":\"Basic\",\"expires_in\":60}")]
    [InlineData("{\"access_token\":\"\",\"token_type\":\"Bearer\",\"expires_in\":60}")]
    [InlineData("not-json")]
    public async Task Invalid_token_response_bodies_are_not_cached(string body)
    {
        using var harness = new Harness();
        harness.Handler.Reply = (number, _) => Task.FromResult(number == 1 ? JsonResponse(body) : TokenResponse(number, 900));

        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync());

        Assert.Equal("invalid-token-response", error.Code);
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(302)]
    public async Task HTTP_failure_or_redirect_is_not_cached(int statusCode)
    {
        using var harness = new Harness();
        harness.Handler.Reply = (number, _) => Task.FromResult(number == 1
            ? new HttpResponseMessage((HttpStatusCode)statusCode) { Content = new StringContent("{}") }
            : TokenResponse(number, 900));

        await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authority_changed_or_denied_during_HTTP_rejects_the_issued_token_without_caching_it(bool deny)
    {
        using var harness = new Harness();
        var original = harness.Context;
        harness.Handler.Reply = (number, _) =>
        {
            if (number == 1)
            {
                if (deny) harness.DenyAuthority = true;
                else harness.Context = Changed(original, "credential-revision");
            }
            return Task.FromResult(TokenResponse(number, 900));
        };

        await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => harness.GetAsync());
        harness.Context = original;
        harness.DenyAuthority = false;
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal("issued-2", await harness.GetAsync());
        Assert.Equal(2, harness.Handler.Requests);
    }

    [Fact]
    public async Task A_cancelled_waiter_does_not_cancel_the_issuer_or_prevent_subsequent_reuse()
    {
        using var harness = new Harness();
        var started = Signal();
        var release = Signal();
        harness.Handler.Reply = async (number, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return TokenResponse(number, 900);
        };
        var issuer = harness.GetAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelled = new CancellationTokenSource();
            var waiter = harness.GetAsync(ct: cancelled.Token);
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            Assert.Equal(1, harness.Handler.Requests);
            release.TrySetResult();
            Assert.Equal("issued-1", await issuer);
            Assert.Equal("issued-1", await harness.GetAsync());
            Assert.Equal(1, harness.Handler.Requests);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Authority_changed_while_waiting_is_rechecked_before_reusing_the_issuers_entry()
    {
        using var harness = new Harness();
        var started = Signal();
        var release = Signal();
        harness.Handler.Reply = async (number, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return TokenResponse(number, 900);
        };
        var issuer = harness.GetAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiterContext = harness.Context;
            var waiter = harness.NewCache().GetAsync(_ => Task.FromResult(waiterContext),
                ServiceLinkContract.ControlScope, CancellationToken.None);
            waiterContext = Changed(waiterContext, "credential-revision");
            release.TrySetResult();

            Assert.Equal("issued-1", await issuer);
            var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => waiter);

            Assert.Equal(409, error.StatusCode);
            Assert.Equal("protocol-profile-conflict", error.Code);
            Assert.Equal(1, harness.Handler.Requests);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Cancelled_issuance_releases_the_gate_and_does_not_cache_a_result()
    {
        using var harness = new Harness();
        var started = Signal();
        var release = Signal();
        harness.Handler.Reply = async (number, ct) =>
        {
            if (number == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return TokenResponse(number, 900);
        };
        using var cancelled = new CancellationTokenSource();
        var pending = harness.GetAsync(ct: cancelled.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal("issued-2", await harness.GetAsync());
            Assert.Equal("issued-2", await harness.GetAsync());
            Assert.Equal(2, harness.Handler.Requests);
        }
        finally
        {
            cancelled.Cancel();
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task An_already_cancelled_call_cannot_reuse_a_warm_entry()
    {
        using var harness = new Harness();
        Assert.Equal("issued-1", await harness.GetAsync());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.GetAsync(ct: cancelled.Token));

        Assert.Equal(1, harness.Handler.Requests);
    }

    private static ServiceLinkProtocolTokenContext Changed(ServiceLinkProtocolTokenContext context, string change) => change switch
    {
        "attempt" => context with { AttemptId = "other-attempt" },
        "link" => context with { LinkId = "other-link" },
        "link-revision" => context with { LinkRevision = context.LinkRevision + 1 },
        "grant" => context with { GrantHash = "other-grant" },
        "descriptor" => context with { DescriptorHash = "other-descriptor" },
        "direction" => context with { DirectionId = ServiceLinkContract.ResponderToInitiator },
        "mode" => context with { Mode = "rotation" },
        "profile-revision" => context with { OutboundProfileRevision = context.OutboundProfileRevision + 1 },
        "rotation" => context with { RotationId = "other-rotation" },
        "authority-deadline" => context with { AuthorityExpiresAtUnixSeconds = context.AuthorityExpiresAtUnixSeconds + 1 },
        "issuer" => context with { Credential = context.Credential with { Issuer = "https://other-peer.example.test" } },
        "token-endpoint" => context with { Credential = context.Credential with { TokenEndpoint = "https://peer.example.test/other-token" } },
        "audience" => context with { Credential = context.Credential with { Audience = "other-audience" } },
        "client-id" => context with { Credential = context.Credential with { ClientId = "other-client" } },
        "client-secret" => context with { Credential = context.Credential with { ClientSecret = "other-secret +&=" } },
        "credential-revision" => context with { Credential = context.Credential with { CredentialRevision = context.Credential.CredentialRevision + 1 } },
        "credential-scopes" => context with { Credential = context.Credential with { Scopes = [ServiceLinkContract.ControlScope] } },
        "caller-instance" => context with { Credential = context.Credential with { CallerInstanceId = "other-caller" } },
        "caller-tenant" => context with { Credential = context.Credential with { CallerTenantId = "other-caller-tenant" } },
        "target-instance" => context with { Credential = context.Credential with { TargetInstanceId = "other-target" } },
        "target-tenant" => context with { Credential = context.Credential with { TargetTenantId = "other-target-tenant" } },
        "local-instance" => context with { Local = context.Local with { InstanceId = "other-local" } },
        "local-source-instance" => context with { Local = context.Local with { SourceInstanceId = "other-local-source" } },
        "local-issuer" => context with { Local = context.Local with { OauthIssuer = "https://other-local.example.test" } },
        "local-jwks" => context with { Local = context.Local with { JwksUri = "https://local.example.test/other-jwks" } },
        "peer-instance" => context with { Peer = context.Peer with { InstanceId = "other-peer" } },
        "peer-source-instance" => context with { Peer = context.Peer with { SourceInstanceId = "other-peer-source" } },
        "peer-issuer" => context with { Peer = context.Peer with { OauthIssuer = "https://other-peer.example.test" } },
        "peer-jwks" => context with { Peer = context.Peer with { JwksUri = "https://peer.example.test/other-jwks" } },
        _ => throw new ArgumentOutOfRangeException(nameof(change))
    };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage TokenResponse(int number, int expiresIn) => JsonResponse(JsonSerializer.Serialize(new
    {
        access_token = $"issued-{number}", token_type = "Bearer", expires_in = expiresIn
    }));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.Created)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void AssertTokenRequest(ObservedRequest request, ServiceDirectionalCredential credential, string scope)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(credential.TokenEndpoint, request.Endpoint);
        Assert.Null(request.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        var form = request.Body.Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Decode(pair[0]), pair => Decode(pair[1]), StringComparer.Ordinal);
        Assert.Equal(4, form.Count);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal(credential.ClientId, form["client_id"]);
        Assert.Equal(credential.ClientSecret, form["client_secret"]);
        Assert.Equal(scope, form["scope"]);
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient client;
        private readonly MemoryCache memory = new(new MemoryCacheOptions());
        private int resolverCalls;

        public Harness(bool allowPrivate = false)
        {
            CurrentOptions = new MutableOptionsMonitor { Value = new ServiceLinkOptions { AllowPrivateHttp = allowPrivate } };
            Handler = new TokenHandler();
            client = new HttpClient(Handler);
            Transport = new ServiceLinkTransport(client, Options.Create(CurrentOptions.Value), CurrentOptions);
            Cache = NewCache();
            var local = Metadata("local", "rateldesk");
            var peer = Metadata("peer", "netratel");
            Context = new ServiceLinkProtocolTokenContext("attempt", "link", 1, "grant", "descriptor",
                ServiceLinkContract.InitiatorToResponder, "active", 1, local, peer,
                new ServiceDirectionalCredential
                {
                    ClientId = "protocol-client", ClientSecret = "protocol-secret +&=",
                    Issuer = peer.OauthIssuer, TokenEndpoint = peer.TokenEndpoint, Audience = peer.Audience,
                    Scopes = [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope], CredentialRevision = 1,
                    CallerInstanceId = local.InstanceId, CallerTenantId = "caller-tenant",
                    TargetInstanceId = peer.InstanceId, TargetTenantId = "target-tenant"
                }, "rotation", Clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds());
        }

        public ManualTimeProvider Clock { get; } = new();
        public TokenHandler Handler { get; }
        public MutableOptionsMonitor CurrentOptions { get; }
        public ServiceLinkTransport Transport { get; }
        public ServiceLinkProtocolTokenCache Cache { get; }
        public ServiceLinkProtocolTokenContext Context { get; set; }
        public bool DenyAuthority { get; set; }
        public int ResolverCalls => Volatile.Read(ref resolverCalls);
        public ServiceLinkProtocolTokenCache NewCache() => new(Transport, memory, Clock);
        public Task<string> GetAsync(string scope = ServiceLinkContract.ControlScope, CancellationToken ct = default) =>
            Cache.GetAsync(ResolveAsync, scope, ct);

        public Task<ServiceLinkProtocolTokenContext> ResolveAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref resolverCalls);
            ct.ThrowIfCancellationRequested();
            if (DenyAuthority) throw new ServiceLinkProtocolException(409, "test-authority-denied", "Current authority was withdrawn.");
            return Task.FromResult(Context);
        }

        public void Dispose()
        {
            client.Dispose();
            memory.Dispose();
        }

        private static ServiceLinkMetadata Metadata(string instance, string product) => new()
        {
            Product = product, ProductVersion = "1.0", InstanceId = instance, SourceInstanceId = $"{instance}-source",
            ApiBaseUrl = $"https://{instance}.example.test", WebBaseUrl = $"https://{instance}.example.test/web",
            OauthIssuer = $"https://{instance}.example.test", OauthMetadataUrl = $"https://{instance}.example.test/.well-known/oauth-authorization-server",
            TokenEndpoint = $"https://{instance}.example.test/connect/token", JwksUri = $"https://{instance}.example.test/jwks",
            Audience = $"{instance}-audience", ServiceLinkEndpoint = $"https://{instance}.example.test{ServiceLinkContract.EndpointPath}",
            ApprovalEndpoint = $"https://{instance}.example.test/approve", CallbackEndpoint = $"https://{instance}.example.test/callback"
        };
    }

    private sealed record ObservedRequest(HttpMethod Method, string Endpoint, string? Authorization, string? ContentType, string Body);

    private sealed class TokenHandler : HttpMessageHandler
    {
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        public ConcurrentQueue<ObservedRequest> Observed { get; } = new();
        public Func<int, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } =
            (number, _) => Task.FromResult(TokenResponse(number, 900));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var number = Interlocked.Increment(ref requests);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Observed.Enqueue(new ObservedRequest(request.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));
            return await Reply(number, ct);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        // Physical eviction stays separate from the injected logical clock used to enforce reuse.
        private DateTimeOffset utcNow = DateTimeOffset.UtcNow.AddHours(1);
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan elapsed) => utcNow += elapsed;
    }

    private sealed class MutableOptionsMonitor : IOptionsMonitor<ServiceLinkOptions>
    {
        public ServiceLinkOptions Value { get; set; } = new();
        public ServiceLinkOptions CurrentValue => Value;
        public ServiceLinkOptions Get(string? name) => Value;
        public IDisposable OnChange(Action<ServiceLinkOptions, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
