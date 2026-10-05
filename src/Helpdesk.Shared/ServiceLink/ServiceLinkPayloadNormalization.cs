using System.Text.Json;

namespace Helpdesk.Shared.ServiceLink;

/// <summary>Sorts only explicitly declared protocol sets; ordered arrays remain unchanged.</summary>
public static class ServiceLinkPayloadNormalization
{
    public static ServiceDirectionalCredential Credential(ServiceDirectionalCredential credential) =>
        credential with { Scopes = ScopeSet(credential.Scopes) };

    public static ServiceLinkExchangeRequest Exchange(ServiceLinkExchangeRequest request) =>
        request with { CredentialForResponder = Credential(request.CredentialForResponder) };

    public static ServiceLinkLifecycleRequest Lifecycle(ServiceLinkLifecycleRequest request) =>
        request.CredentialForCaller is null ? request : request with { CredentialForCaller = Credential(request.CredentialForCaller) };

    public static ServiceLinkMetadata Metadata(ServiceLinkMetadata metadata)
    {
        if (metadata is null || metadata.PermissionProfiles is null)
            throw new JsonException("Metadata and its profile array cannot be null.");
        return metadata with
        {
            PermissionProfiles = metadata.PermissionProfiles.Select(profile => profile is null ||
                profile.Operations is null || profile.Operations.Any(operation => operation is null)
                ? throw new JsonException("Permission profiles and operations cannot contain null.")
                : profile with { Scopes = ScopeSet(profile.Scopes) }).ToArray()
        };
    }

    public static ServiceLinkGrant Grant(ServiceLinkGrant grant) =>
        grant with { Scopes = ScopeSet(grant.Scopes), Capabilities = ScopeSet(grant.Capabilities) };

    public static ServiceLinkGrant[] Grants(ServiceLinkGrant[] grants)
    {
        if (grants is null) throw new JsonException("The grant array cannot be null.");
        return grants.Select(grant => grant is null ? throw new JsonException("A grant cannot be null.") : Grant(grant))
            .OrderBy(grant => grant.DirectionId, StringComparer.Ordinal).ToArray();
    }

    public static ServiceLinkRequestDescriptor Descriptor(ServiceLinkRequestDescriptor descriptor) => descriptor with
    {
        InitiatorEndpointSnapshot = Metadata(descriptor.InitiatorEndpointSnapshot),
        ResponderEndpointSnapshot = Metadata(descriptor.ResponderEndpointSnapshot),
        RequestedGrants = Grants(descriptor.RequestedGrants)
    };

    public static ServiceLinkGrantSummary Summary(ServiceLinkGrantSummary summary) => summary with
    {
        InitiatorEndpointSnapshot = Metadata(summary.InitiatorEndpointSnapshot),
        ResponderEndpointSnapshot = Metadata(summary.ResponderEndpointSnapshot),
        Grants = Grants(summary.Grants)
    };

    public static bool DescriptorHashMatches(ServiceLinkRequestDescriptor descriptor, string hash) =>
        ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash") == hash ||
        ServiceLinkCanonicalJson.HashObject(Descriptor(descriptor), "descriptor_hash") == hash;

    public static bool SummaryHashMatches(ServiceLinkGrantSummary summary, string hash) =>
        ServiceLinkCanonicalJson.HashObject(summary) == hash ||
        ServiceLinkCanonicalJson.HashObject(Summary(summary)) == hash;

    private static string[] ScopeSet(string[] scopes)
    {
        if (scopes is null || scopes.Length > 256 || scopes.Any(scope => scope is not { Length: > 0 and <= 256 }) ||
            scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length)
            throw new JsonException("A protocol set contains null, duplicate, empty or oversized values.");
        return scopes.Order(StringComparer.Ordinal).ToArray();
    }
}
