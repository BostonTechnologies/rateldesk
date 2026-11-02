using System.Security.Claims;

namespace HelpDesk.NewWeb.Services;

// Authentication providers can notify again for the same principal during a
// circuit refresh. Only changed identity or access claims invalidate a draft.
internal sealed class ChatIdentitySnapshot(ClaimsPrincipal principal)
{
    private readonly HashSet<(string? Type, bool Authenticated)> identities =
        principal.Identities.Select(identity => (identity.AuthenticationType, identity.IsAuthenticated)).ToHashSet();
    private readonly HashSet<IdentityClaim> claims = Claims(principal).ToHashSet();

    public bool Matches(ClaimsPrincipal current) =>
        identities.SetEquals(current.Identities.Select(identity => (identity.AuthenticationType, identity.IsAuthenticated))) &&
        claims.SetEquals(Claims(current));

    private static IEnumerable<IdentityClaim> Claims(ClaimsPrincipal current) => current.Identities.SelectMany(identity =>
        identity.Claims.Select(claim => new IdentityClaim(identity.AuthenticationType, identity.IsAuthenticated,
            claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer)));

    private sealed record IdentityClaim(string? AuthenticationType, bool Authenticated, string Type,
        string Value, string ValueType, string Issuer, string OriginalIssuer);
}
