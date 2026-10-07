using System.Security.Cryptography;
using System.Text;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.ServiceLink;

namespace Helpdesk.Infrastructure.ServiceLink;

public static class ServiceLinkValidation
{
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public static string Proof() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static bool Same(string? left, string? right) => left is not null && right is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    public static string Timestamp(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    public static string Endpoint(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    public static void Require(bool condition, string code, string message, int status = 400)
    { if (!condition) throw new ServiceLinkProtocolException(status, code, message); }
    public static void Id(string value) => Require(value is { Length: >= 16 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "invalid-identifier", "The protocol identifier is invalid.");

    public static ServiceLinkMetadata Metadata(ServiceLinkMetadata metadata, bool privateHttp, string? enteredWebBase = null)
    {
        Require(metadata.Contract == ServiceLinkContract.Version && metadata.SupportedContracts.Contains(ServiceLinkContract.Version, StringComparer.Ordinal), "upgrade-required", "The peer does not advertise the required service link contract.", 422);
        Require(metadata.Product is "netratel" or "rateldesk" && metadata.InstanceId.Length is > 0 and <= 256 && !string.IsNullOrWhiteSpace(metadata.Audience), "invalid-metadata", "The peer metadata is incomplete.");
        Require(metadata.SourceInstanceId is null || Guid.TryParseExact(metadata.SourceInstanceId, "D", out var producer) && producer.ToString("D") == metadata.SourceInstanceId,
            "invalid-metadata", "The producer identity must use canonical GUID format.");
        if (metadata.OauthMetadataUrl is null || metadata.JwksUri is null)
            throw new ServiceLinkProtocolException(422, "upgrade-required", "The peer issuer must expose the OAuth metadata and signing keys required for managed service authentication.");
        foreach (var address in new[] { metadata.WebBaseUrl, metadata.ApiBaseUrl, metadata.OauthIssuer, metadata.OauthMetadataUrl, metadata.TokenEndpoint, metadata.JwksUri, metadata.ServiceLinkEndpoint, metadata.ApprovalEndpoint, metadata.CallbackEndpoint })
        {
            Require(Uri.TryCreate(address, UriKind.Absolute, out _), "invalid-metadata", "The peer advertises an invalid endpoint.");
            IntegrationEndpointPolicy.ValidateServiceLink(new Uri(address), "Advertised service endpoint", privateHttp);
        }
        if (metadata.GatewayBaseUrl is not null) IntegrationEndpointPolicy.ValidateServiceLink(new Uri(metadata.GatewayBaseUrl), "Gateway", privateHttp);
        Require(metadata.ServiceLinkEndpoint == Endpoint(metadata.ApiBaseUrl, ServiceLinkContract.EndpointPath) &&
            metadata.ApprovalEndpoint == Endpoint(metadata.WebBaseUrl, "/account/integration-credentials/link/approve") &&
            metadata.CallbackEndpoint == Endpoint(metadata.WebBaseUrl, "/account/integration-credentials/link/callback"), "invalid-metadata", "The peer lifecycle and browser endpoints are not its fixed configured endpoints.");
        Require(SameOrigin(metadata.TokenEndpoint, metadata.ApiBaseUrl) || SameOrigin(metadata.TokenEndpoint, metadata.OauthIssuer), "invalid-metadata", "The token endpoint lies outside the approved API/issuer identity.");
        Require((SameOrigin(metadata.OauthMetadataUrl, metadata.OauthIssuer) || SameOrigin(metadata.OauthMetadataUrl, metadata.ApiBaseUrl)) && (SameOrigin(metadata.JwksUri, metadata.OauthIssuer) || SameOrigin(metadata.JwksUri, metadata.ApiBaseUrl)), "invalid-metadata", "OAuth metadata/JWKS is outside its approved API/issuer origins.");
        Require(metadata.TokenEndpointAuthMethodsSupported.Contains("client_secret_post", StringComparer.Ordinal), "upgrade-required", "The peer lacks the required client_secret_post authentication method.", 422);
        if (enteredWebBase is not null) Require(metadata.WebBaseUrl.TrimEnd('/') == enteredWebBase.TrimEnd('/'), "origin-mismatch", "The descriptor does not belong to the entered Web origin.");
        foreach (var profile in metadata.PermissionProfiles)
        {
            if (profile is null) throw new ServiceLinkProtocolException(400, "invalid-profile", "The peer permission profile cannot be null.");
            Require(profile.Capability.Length > 0 && profile.Scopes.Length > 0 && profile.Operations.Length > 0, "invalid-profile", "The peer permission profile is incomplete.");
            Set(profile.Scopes);
            Require(profile.Operations.All(x => x is not null && x.Method is "GET" or "POST" && x.Path.StartsWith('/') && !x.Path.Contains('?') && profile.Scopes.Contains(x.Scope, StringComparer.Ordinal)), "invalid-profile", "The permission profile contains unsupported resource operations.");
        }
        return ServiceLinkPayloadNormalization.Metadata(metadata);
    }

    public static bool ControlOnlyScopes(string[] scopes) => scopes is { Length: 2 } &&
        scopes.Contains(ServiceLinkContract.ControlScope, StringComparer.Ordinal) &&
        scopes.Contains(ServiceLinkContract.VerifyScope, StringComparer.Ordinal);

    public static bool IncidentOnlyGrant(ServiceLinkGrant grant) => grant.TargetProduct == "netratel" &&
        grant.Capabilities is [ServiceLinkContract.IncidentOnlyCapability] && ControlOnlyScopes(grant.Scopes) &&
        grant.ResourceConstraints.ResourceIds.Length == 0 && grant.ResourceConstraints.RequestDefinitionIds.Length == 0;

    public static void RequireIncidentOnlySupport(ServiceLinkMetadata local, ServiceLinkMetadata peer) =>
        Require(new[] { local, peer }.All(m => m.PermissionProfiles.Any(p => p.Capability == ServiceLinkContract.IncidentOnlyCapability && ControlOnlyScopes(p.Scopes))),
            "upgrade-required", "Upgrade both products to support incident-only connections before approving this relationship.", 422);

    public static ServiceLinkGrant[] Grants(ServiceLinkGrant[] grants, ServiceLinkMetadata initiator, ServiceLinkMetadata responder, bool proposal = false)
    {
        if (grants is null) throw new ServiceLinkProtocolException(400, "invalid-grant", "Exactly two independently approved directions are required.");
        Require(grants.Length == 2 && grants.All(x => x is not null) && grants.Select(x => x.DirectionId).Distinct(StringComparer.Ordinal).Count() == 2, "invalid-grant", "Exactly two independently approved directions are required.");
        var normalized = grants.OrderBy(x => x.DirectionId, StringComparer.Ordinal).Select(g =>
        {
            var forward = g.DirectionId == ServiceLinkContract.InitiatorToResponder;
            Require(forward || g.DirectionId == ServiceLinkContract.ResponderToInitiator, "invalid-grant", "The grant direction is invalid.");
            var caller = forward ? initiator : responder; var target = forward ? responder : initiator;
            Require(g.CallerSnapshot == (forward ? "initiator" : "responder") && g.TargetSnapshot == (forward ? "responder" : "initiator") &&
                g.CallerProduct == caller.Product && g.CallerInstanceId == caller.InstanceId && g.TargetProduct == target.Product &&
                g.TargetInstanceId == target.InstanceId && g.Issuer == target.OauthIssuer && g.Audience == target.Audience,
                "grant-binding-mismatch", "The grant differs from its approved endpoint identity.");
            Require((proposal || !string.IsNullOrWhiteSpace(g.CallerTenantId) && !string.IsNullOrWhiteSpace(g.TargetTenantId)) && g.Scopes.Length > 0 && g.Capabilities.Length > 0, "invalid-grant", "The grant requires explicit tenant and capability boundaries.");
            var scopes = Set(g.Scopes); var capabilities = Set(g.Capabilities);
            if (capabilities.Contains(ServiceLinkContract.IncidentOnlyCapability, StringComparer.Ordinal)) RequireIncidentOnlySupport(initiator, responder);
            Require(capabilities.All(c => target.PermissionProfiles.Any(p => p.Capability == c)) && scopes.All(s => target.PermissionProfiles.Any(p => capabilities.Contains(p.Capability, StringComparer.Ordinal) && p.Scopes.Contains(s, StringComparer.Ordinal))), "unsupported-grant", "A requested scope or capability is not supported by the approved peer profile.");
            var constraints = g.ResourceConstraints;
            foreach (var set in new[] { constraints.CustomerIds, constraints.RequestIds, constraints.TaskIds, constraints.ResourceIds, constraints.RequestDefinitionIds }) Set(set);
            if (target.Product == "rateldesk")
            {
                Require((constraints.OrganizationId == g.TargetTenantId || proposal && string.IsNullOrEmpty(g.TargetTenantId) && constraints.OrganizationId is null) && constraints.TenantId is null && constraints.ResourceIds.Length == 0 && constraints.RequestDefinitionIds.Length == 0,
                    "invalid-resource-constraints", "RatelDesk requires its exact local organization boundary.");
                Require((proposal && g.TargetSnapshot == "responder" || constraints.CustomerIds.Length == 1) &&
                    Guid.TryParseExact(g.SourceInstanceId, "D", out var source) && source.ToString("D") == g.SourceInstanceId &&
                    (proposal && g.SourceNamespaceId is null || Guid.TryParseExact(g.SourceNamespaceId, "D", out var sourceNamespace) && sourceNamespace.ToString("D") == g.SourceNamespaceId),
                    "invalid-source-grant", "The receiver grant requires one approved customer and an exact producer/source namespace.");
                Require(g.SourceInstanceId == caller.SourceInstanceId, "source-identity-mismatch", "The grant does not match the producer's persisted source identity.");
            }
            else
            {
                Require(constraints.OrganizationId is null && constraints.CustomerIds.Length == 0 && constraints.TaskIds.Length == 0 && constraints.RequestIds.Length == 0 && constraints.TenantId == (string.IsNullOrEmpty(g.TargetTenantId) ? null : g.TargetTenantId), "invalid-resource-constraints", "NetRatel requires its exact tenant/resource boundary.");
                if (capabilities.Contains(ServiceLinkContract.IncidentOnlyCapability, StringComparer.Ordinal))
                {
                    RequireIncidentOnlySupport(initiator, responder);
                    Require(IncidentOnlyGrant(g), "invalid-resource-constraints", "An incident-only reverse direction permits only connection verification/control and no business resources.");
                }
                else
                    Require(proposal || constraints.ResourceIds.Length > 0 || constraints.RequestDefinitionIds.Length > 0, "invalid-resource-constraints", "The final NetRatel grant must select explicit authorized resources or request definitions.");
                Require(g.SourceInstanceId is null && g.SourceNamespaceId is null, "invalid-source-grant", "This outbound grant does not own the incident producer namespace.");
            }
            return g with { Scopes = scopes, Capabilities = capabilities };
        }).ToArray();
        if (normalized.Any(g => g.Capabilities.Contains(ServiceLinkContract.IncidentOnlyCapability, StringComparer.Ordinal)))
        {
            var control = normalized.SingleOrDefault(g => g.TargetProduct == "netratel");
            var incidents = normalized.SingleOrDefault(g => g.TargetProduct == "rateldesk");
            Require(control is not null && IncidentOnlyGrant(control) && incidents is not null &&
                !incidents.Capabilities.Contains(ServiceLinkContract.IncidentOnlyCapability, StringComparer.Ordinal) &&
                incidents.Scopes.Length == 3 && incidents.Scopes.Contains("rateldesk.incidents.create", StringComparer.Ordinal) &&
                incidents.Scopes.Contains("rateldesk.incident-receipts.read", StringComparer.Ordinal) &&
                incidents.Scopes.Contains("rateldesk.incident-targets.read", StringComparer.Ordinal),
                "invalid-incident-only-grant", "Incident-only connections authorize incident creation and exact connection control. Approve task access separately.");
        }
        var forwardGrant = normalized.Single(g => g.DirectionId == ServiceLinkContract.InitiatorToResponder);
        var reverseGrant = normalized.Single(g => g.DirectionId == ServiceLinkContract.ResponderToInitiator);
        Require((proposal && (string.IsNullOrEmpty(forwardGrant.CallerTenantId) || string.IsNullOrEmpty(reverseGrant.TargetTenantId)) || forwardGrant.CallerTenantId == reverseGrant.TargetTenantId) &&
            (proposal && (string.IsNullOrEmpty(forwardGrant.TargetTenantId) || string.IsNullOrEmpty(reverseGrant.CallerTenantId)) || forwardGrant.TargetTenantId == reverseGrant.CallerTenantId),
            "tenant-pair-mismatch", "Both reciprocal directions must bind the same selected initiator and responder tenants.");
        if (!proposal && normalized.Any(g => g.TargetProduct == "netratel" && g.Scopes.Contains("netratel.orchestration.invoke", StringComparer.Ordinal)))
            Require(normalized.Any(g => g.TargetProduct == "rateldesk" && g.Scopes.Contains("rateldesk.orchestration.callback", StringComparer.Ordinal)), "callback-required", "Orchestration invocation requires the separately approved RatelDesk task callback direction before the link can activate.");
        return normalized;
    }

    public static void SelectedTenants(ServiceLinkGrant[] grants, string initiatorTenant, string responderTenant)
    {
        var forward = grants.Single(grant => grant.DirectionId == ServiceLinkContract.InitiatorToResponder);
        var reverse = grants.Single(grant => grant.DirectionId == ServiceLinkContract.ResponderToInitiator);
        Require(forward.CallerTenantId == initiatorTenant && reverse.TargetTenantId == initiatorTenant &&
            forward.TargetTenantId == responderTenant && reverse.CallerTenantId == responderTenant,
            "tenant-pair-mismatch", "The final reciprocal tenant pair differs from the retained initiator and selected responder consent.");
    }

    public static void Narrowed(ServiceLinkGrant ceiling, ServiceLinkGrant selected)
    {
        Require(ceiling.DirectionId == selected.DirectionId && ceiling.CallerInstanceId == selected.CallerInstanceId && ceiling.TargetInstanceId == selected.TargetInstanceId && ceiling.Issuer == selected.Issuer && ceiling.Audience == selected.Audience &&
            (string.IsNullOrEmpty(ceiling.CallerTenantId) || ceiling.CallerTenantId == selected.CallerTenantId) && (string.IsNullOrEmpty(ceiling.TargetTenantId) || ceiling.TargetTenantId == selected.TargetTenantId) &&
            selected.Scopes.All(x => ceiling.Scopes.Contains(x, StringComparer.Ordinal)) && selected.Capabilities.All(x => ceiling.Capabilities.Contains(x, StringComparer.Ordinal)), "grant-expansion-rejected", "The peer selected a grant outside the proposed permission ceiling.");
        var a = ceiling.ResourceConstraints; var b = selected.ResourceConstraints;
        Require((a.OrganizationId is null || a.OrganizationId == b.OrganizationId) && (a.TenantId is null || a.TenantId == b.TenantId) &&
            (a.CustomerIds.Length == 0 || b.CustomerIds.All(x => a.CustomerIds.Contains(x, StringComparer.Ordinal))) && b.RequestIds.All(x => a.RequestIds.Contains(x, StringComparer.Ordinal)) && b.TaskIds.All(x => a.TaskIds.Contains(x, StringComparer.Ordinal)) &&
            (a.ResourceIds.Length == 0 || b.ResourceIds.All(x => a.ResourceIds.Contains(x, StringComparer.Ordinal))) && (a.RequestDefinitionIds.Length == 0 || b.RequestDefinitionIds.All(x => a.RequestDefinitionIds.Contains(x, StringComparer.Ordinal))) &&
            ceiling.SourceInstanceId == selected.SourceInstanceId && (ceiling.SourceNamespaceId is null || ceiling.SourceNamespaceId == selected.SourceNamespaceId), "grant-expansion-rejected", "The selected resources or source identity exceed the immutable proposal.");
    }

    public static void Credential(ServiceDirectionalCredential credential, ServiceLinkGrant grant, ServiceLinkMetadata target)
    {
        Require(credential.ClientId.Length is > 0 and <= 256 && credential.ClientSecret.Length is >= 32 and <= 1024 && credential.CredentialRevision is >= 1 and <= 9007199254740991 &&
            credential.TokenEndpointAuthMethod == "client_secret_post" && credential.Issuer == grant.Issuer && credential.TokenEndpoint == target.TokenEndpoint && credential.Audience == grant.Audience &&
            credential.CallerInstanceId == grant.CallerInstanceId && credential.CallerTenantId == grant.CallerTenantId && credential.TargetInstanceId == grant.TargetInstanceId && credential.TargetTenantId == grant.TargetTenantId &&
            Set(credential.Scopes).SequenceEqual(grant.Scopes.Order(StringComparer.Ordinal)), "credential-binding-mismatch", "The directional credential differs from its exact approved grant.");
    }
    public static string[] Set(string[] values)
    {
        if (values is null) throw new ServiceLinkProtocolException(400, "invalid-set", "A protocol set cannot be null.");
        Require(values.Length <= 256 && values.All(x => x is { Length: > 0 and <= 256 }) && values.Distinct(StringComparer.Ordinal).Count() == values.Length, "invalid-set", "A protocol set contains duplicate, null, empty or oversized values.");
        return values.Order(StringComparer.Ordinal).ToArray();
    }
    private static bool SameOrigin(string a, string b) => new Uri(a).GetLeftPart(UriPartial.Authority) == new Uri(b).GetLeftPart(UriPartial.Authority);
}
