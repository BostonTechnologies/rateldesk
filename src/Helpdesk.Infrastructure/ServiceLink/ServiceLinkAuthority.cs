using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.ServiceLink;

/// <summary>Effective business authority always comes from current durable grants, not lifecycle flags alone.</summary>
public static class ServiceLinkAuthority
{
    /// <summary>A live issuer configuration cannot silently replace an identity approved in an immutable grant.</summary>
    public static bool LocalIdentityMatches(ServiceLinkGrantSummary summary, string role, ServiceIdentityOptions currentOptions, ServiceLinkOptions currentLinking)
    {
        if (summary is null || currentOptions is null || currentLinking is null || !currentOptions.Enabled || !currentLinking.Enabled || role is not ("initiator" or "responder") ||
            new[] { currentOptions.Issuer, currentOptions.Audience, currentOptions.InstanceId, currentOptions.WebBaseUrl, currentOptions.ApiBaseUrl,
                currentLinking.WebBaseUrl, currentLinking.ApiBaseUrl }.Any(string.IsNullOrWhiteSpace)) return false;
        var local = role == "initiator" ? summary.InitiatorEndpointSnapshot : summary.ResponderEndpointSnapshot;
        var localInstance = role == "initiator" ? summary.InitiatorInstanceId : summary.ResponderInstanceId;
        if (local is null || local.Contract != ServiceLinkContract.Version || local.Product != "rateldesk" ||
            local.InstanceId != localInstance || local.InstanceId != currentOptions.InstanceId ||
            local.OauthIssuer != currentOptions.Issuer || local.Audience != currentOptions.Audience ||
            local.WebBaseUrl != currentOptions.WebBaseUrl.TrimEnd('/') || local.ApiBaseUrl != currentOptions.ApiBaseUrl.TrimEnd('/') ||
            local.WebBaseUrl != currentLinking.WebBaseUrl.TrimEnd('/') || local.ApiBaseUrl != currentLinking.ApiBaseUrl.TrimEnd('/') ||
            local.GatewayBaseUrl != currentLinking.GatewayBaseUrl ||
            local.TokenEndpoint != ServiceLinkValidation.Endpoint(currentOptions.ApiBaseUrl, "/connect/token") ||
            local.OauthMetadataUrl != ServiceLinkValidation.Endpoint(currentOptions.ApiBaseUrl, "/.well-known/oauth-authorization-server") ||
            local.JwksUri != ServiceLinkValidation.Endpoint(currentOptions.ApiBaseUrl, "/.well-known/jwks.json") ||
            local.ServiceLinkEndpoint != ServiceLinkValidation.Endpoint(currentOptions.ApiBaseUrl, ServiceLinkContract.EndpointPath) ||
            local.ApprovalEndpoint != ServiceLinkValidation.Endpoint(currentOptions.WebBaseUrl, "/account/integration-credentials/link/approve") ||
            local.CallbackEndpoint != ServiceLinkValidation.Endpoint(currentOptions.WebBaseUrl, "/account/integration-credentials/link/callback") ||
            local.TokenEndpointAuthMethodsSupported is not ["client_secret_post"] ||
            local.SupportedContracts?.Contains(ServiceLinkContract.Version, StringComparer.Ordinal) != true) return false;
        return true;
    }

    public static async Task<bool> InboundUsableAsync(HelpdeskDbContext db, ServiceLinkAttempt attempt,
        TimeProvider clock, ServiceIdentityOptions currentIdentity, ServiceLinkOptions currentLinking, CancellationToken ct)
    {
        if (!currentIdentity.Enabled || !currentLinking.Enabled) return false;
        var current = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.AttemptId == attempt.AttemptId && x.LinkId == attempt.LinkId && x.LinkRevision == attempt.LinkRevision, ct);
        if (current is null || current.Decision != "commit" || !current.LocalInboundActive ||
            current.LifecycleState is not ("commit_decided" or "active") || current.InboundPrincipalId is null ||
            current.GrantSummaryJson is null || current.Role is not ("initiator" or "responder")) return false;

        ServiceLinkGrant grant;
        string[] scopes;
        string[] customers;
        ServiceLinkResourceConstraints constraints;
        var registration = await db.Set<ServicePrincipalRegistration>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == current.InboundPrincipalId, ct);
        if (registration is null || registration.Status != "active" || registration.LinkId != current.LinkId ||
            registration.AttemptId != current.AttemptId || registration.LinkRevision != current.LinkRevision ||
            registration.GrantHash != current.GrantHash || registration.DescriptorHash != current.DescriptorHash ||
            registration.OrganizationId != current.LocalTenantId || registration.PeerInstanceId != current.PeerInstanceId ||
            registration.PeerTenantId != current.PeerTenantId) return false;
        try
        {
            var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(current.GrantSummaryJson);
            if (summary.Contract != ServiceLinkContract.Version || summary.AttemptId != current.AttemptId ||
                summary.LinkId != current.LinkId || summary.ProposedLinkRevision != current.LinkRevision ||
                summary.DescriptorHash != current.DescriptorHash ||
                !ServiceLinkPayloadNormalization.SummaryHashMatches(summary, current.GrantHash!) || summary.Grants is not { Length: 2 } ||
                !LocalIdentityMatches(summary, current.Role, currentIdentity, currentLinking)) return false;
            var direction = current.Role == "initiator" ? ServiceLinkContract.ResponderToInitiator : ServiceLinkContract.InitiatorToResponder;
            var matches = summary.Grants.Where(x => x is not null && x.DirectionId == direction).ToArray();
            if (matches.Length != 1) return false;
            grant = matches[0];
            var localInstance = current.Role == "initiator" ? summary.InitiatorInstanceId : summary.ResponderInstanceId;
            if (registration.DirectionId != direction || grant.TargetProduct != "rateldesk" ||
                grant.TargetInstanceId != localInstance || grant.TargetTenantId != current.LocalTenantId ||
                grant.CallerInstanceId != current.PeerInstanceId || grant.CallerTenantId != current.PeerTenantId ||
                grant.ResourceConstraints is null) return false;
            scopes = ServiceLinkCanonicalJson.Deserialize<string[]>(registration.AllowedScopesJson);
            customers = ServiceLinkCanonicalJson.Deserialize<string[]>(registration.CustomerIdsJson);
            constraints = ServiceLinkCanonicalJson.Deserialize<ServiceLinkResourceConstraints>(registration.ResourceConstraintsJson);
            if (!SameSet(scopes, grant.Scopes) || scopes.Length == 0 || !SameSet(customers, grant.ResourceConstraints.CustomerIds) ||
                customers.Length != 1 || !SameConstraints(constraints, grant.ResourceConstraints) ||
                constraints.OrganizationId != current.LocalTenantId || constraints.TenantId is not null ||
                constraints.ResourceIds.Length != 0 || constraints.RequestDefinitionIds.Length != 0 ||
                !Guid.TryParseExact(grant.SourceInstanceId, "D", out var source) || source.ToString("D") != grant.SourceInstanceId ||
                !Guid.TryParseExact(grant.SourceNamespaceId, "D", out var sourceNamespace) || sourceNamespace.ToString("D") != grant.SourceNamespaceId ||
                registration.SourceInstanceId != source || registration.SourceNamespaceId != sourceNamespace) return false;
        }
        catch (JsonException)
        {
            return false;
        }

        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ServicePrincipalId == registration.Id && x.CredentialRevision == registration.CurrentCredentialRevision, ct);
        var now = clock.GetUtcNow();
        if (credential is null || credential.Status is not ("active" or "retiring") || credential.ExpiresAtUtc <= now ||
            credential.RetireAtUtc is { } retirement && retirement <= now) return false;
        if (!await db.Organizations.AsNoTracking().AnyAsync(x => x.Id == current.LocalTenantId &&
                x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct) ||
            await db.Customers.AsNoTracking().CountAsync(x => customers.Contains(x.Id) && x.OrganizationId == current.LocalTenantId &&
                x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct) != customers.Length) return false;
        return await db.IncidentReceiverSources.AsNoTracking().AnyAsync(x => x.SourceNamespaceId == registration.SourceNamespaceId &&
                   x.SourceInstanceId == registration.SourceInstanceId && x.OrganizationId == current.LocalTenantId &&
                   x.CustomerId == customers[0] && x.IsEnabled, ct) &&
               await db.IncidentReceiverPrincipalBindings.AsNoTracking().AnyAsync(x => x.SourceNamespaceId == registration.SourceNamespaceId &&
                   x.PrincipalKind == "service_principal" && x.PrincipalId == registration.Id.ToString("N") && x.IsEnabled, ct);
    }

    private static bool SameSet(string[]? left, string[]? right) => ValidSet(left) && ValidSet(right) &&
        left!.Order(StringComparer.Ordinal).SequenceEqual(right!.Order(StringComparer.Ordinal));

    private static bool ValidSet(string[]? values) => values is { Length: <= 256 } &&
        values.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 256) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;

    private static bool SameConstraints(ServiceLinkResourceConstraints left, ServiceLinkResourceConstraints right) =>
        left.OrganizationId == right.OrganizationId && left.TenantId == right.TenantId &&
        SameSet(left.CustomerIds, right.CustomerIds) && SameSet(left.RequestIds, right.RequestIds) &&
        SameSet(left.TaskIds, right.TaskIds) && SameSet(left.ResourceIds, right.ResourceIds) &&
        SameSet(left.RequestDefinitionIds, right.RequestDefinitionIds);
}
