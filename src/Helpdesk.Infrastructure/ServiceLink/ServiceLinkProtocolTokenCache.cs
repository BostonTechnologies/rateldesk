using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Helpdesk.Shared.ServiceLink;

namespace Helpdesk.Infrastructure.ServiceLink;

/// <summary>Server-only immutable control-token binding; never serialize its credential into a response or log.</summary>
public sealed record ServiceLinkProtocolTokenContext(string AttemptId, string LinkId, long LinkRevision,
    string GrantHash, string DescriptorHash, string DirectionId, string Mode, int? OutboundProfileRevision,
    ServiceLinkMetadata Local, ServiceLinkMetadata Peer, ServiceDirectionalCredential Credential,
    string? RotationId, long? AuthorityExpiresAtUnixSeconds);

/// <summary>A bounded control/verification cache. The caller must resolve current durable authority on every use.</summary>
public sealed class ServiceLinkProtocolTokenCache(ServiceLinkTransport transport, IMemoryCache cache, TimeProvider clock)
{
    // Fixed stripes share acquisition across transient worker scopes without retaining a lock per link.
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private sealed record CachedToken(string Token, DateTimeOffset ReuseUntilUtc);

    public async Task<string> GetAsync(Func<CancellationToken, Task<ServiceLinkProtocolTokenContext>> resolveCurrent,
        string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (scope is not (ServiceLinkContract.ControlScope or ServiceLinkContract.VerifyScope))
            throw new ServiceLinkProtocolException(403, "protocol-scope-required", "Only an exact control or verification scope may use this cache.");
        var captured = await resolveCurrent(ct);
        Validate(captured, ct);
        var key = Key(captured, scope);
        var stripe = Stripes[(key.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % Stripes.Length];
        await stripe.WaitAsync(ct);
        try
        {
            // Waiting for another scope's token cannot preserve stale authority.
            var current = await resolveCurrent(ct);
            Validate(current, ct);
            RequireSame(key, current, scope);
            if (cache.TryGetValue<CachedToken>(key, out var cached) && cached is not null)
            {
                if (cached.ReuseUntilUtc > clock.GetUtcNow()) return cached.Token;
                cache.Remove(key);
            }
            var requestStartedAt = clock.GetUtcNow();
            var issued = await transport.AcquireTokenAsync(current.Credential, scope, ct);
            var after = await resolveCurrent(ct);
            Validate(after, ct);
            RequireSame(key, after, scope);
            var now = clock.GetUtcNow();
            if (requestStartedAt.AddSeconds(issued.ExpiresIn) <= now)
                throw new ServiceLinkProtocolException(422, "expired-token-response", "The service token lifetime elapsed before its response could be used.");
            var reuseUntil = requestStartedAt.AddSeconds(Math.Min(45, issued.ExpiresIn - 5));
            if (after.AuthorityExpiresAtUnixSeconds is { } deadline)
                reuseUntil = DateTimeOffset.FromUnixTimeSeconds(deadline) < reuseUntil
                    ? DateTimeOffset.FromUnixTimeSeconds(deadline) : reuseUntil;
            var remaining = reuseUntil - now;
            if (remaining > TimeSpan.Zero) cache.Set(key, new CachedToken(issued.AccessToken, reuseUntil), remaining);
            return issued.AccessToken;
        }
        finally { stripe.Release(); }
    }

    private void Validate(ServiceLinkProtocolTokenContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.AuthorityExpiresAtUnixSeconds is { } expires && expires <= clock.GetUtcNow().ToUnixTimeSeconds())
            throw new ServiceLinkProtocolException(403, "protocol-authority-expired", "The retained control authority has expired.");
        // A cache hit still observes an opt-in removed from current typed options.
        transport.ValidateTokenEndpoint(context.Credential);
    }

    private static void RequireSame(string capturedKey, ServiceLinkProtocolTokenContext current, string scope)
    {
        if (Key(current, scope) != capturedKey)
            throw new ServiceLinkProtocolException(409, "protocol-profile-conflict", "The current protocol credential or approved link changed during acquisition.");
    }

    private static string Key(ServiceLinkProtocolTokenContext context, string scope) => "rateldesk-protocol-token/" +
        ServiceLinkCanonicalJson.HashObject(new
        {
            context.AttemptId, context.LinkId, context.LinkRevision, context.GrantHash, context.DescriptorHash,
            context.DirectionId, context.Mode, context.OutboundProfileRevision, context.Local, context.Peer,
            context.RotationId, context.AuthorityExpiresAtUnixSeconds, RequestedScope = scope,
            Credential = context.Credential with
            {
                ClientSecret = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context.Credential.ClientSecret))),
                Scopes = ServiceLinkValidation.Set(context.Credential.Scopes)
            }
        });
}
