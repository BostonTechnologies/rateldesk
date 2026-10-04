using System.Text.Json.Serialization;

namespace Helpdesk.Shared.ServiceLink;

public static class ServiceLinkContract
{
    public const string Version = "bostec.service-link.v1";
    public const string VerifyScope = "bostec.service-link.verify";
    public const string ControlScope = "bostec.service-link.control";
    public const string InitiatorToResponder = "initiator_to_responder";
    public const string ResponderToInitiator = "responder_to_initiator";
    public const string EndpointPath = "/api/integrations/service-link/v1";
    public const string MetadataPath = "/api/integrations/service-link/metadata";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceLinkMetadata
{
    [JsonRequired, JsonPropertyName("contract")] public string Contract { get; init; } = ServiceLinkContract.Version;
    [JsonRequired, JsonPropertyName("product")] public string Product { get; init; } = "rateldesk";
    [JsonRequired, JsonPropertyName("product_version")] public string ProductVersion { get; init; } = "";
    [JsonRequired, JsonPropertyName("instance_id")] public string InstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("source_instance_id")] public string? SourceInstanceId { get; init; }
    [JsonRequired, JsonPropertyName("web_base_url")] public string WebBaseUrl { get; init; } = "";
    [JsonRequired, JsonPropertyName("api_base_url")] public string ApiBaseUrl { get; init; } = "";
    [JsonRequired, JsonPropertyName("gateway_base_url")] public string? GatewayBaseUrl { get; init; }
    [JsonRequired, JsonPropertyName("oauth_issuer")] public string OauthIssuer { get; init; } = "";
    [JsonRequired, JsonPropertyName("oauth_metadata_url")] public string OauthMetadataUrl { get; init; } = "";
    [JsonRequired, JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; init; } = "";
    [JsonRequired, JsonPropertyName("jwks_uri")] public string JwksUri { get; init; } = "";
    [JsonRequired, JsonPropertyName("audience")] public string Audience { get; init; } = "";
    [JsonRequired, JsonPropertyName("token_endpoint_auth_methods_supported")] public string[] TokenEndpointAuthMethodsSupported { get; init; } = ["client_secret_post"];
    [JsonRequired, JsonPropertyName("service_link_endpoint")] public string ServiceLinkEndpoint { get; init; } = "";
    [JsonRequired, JsonPropertyName("approval_endpoint")] public string ApprovalEndpoint { get; init; } = "";
    [JsonRequired, JsonPropertyName("callback_endpoint")] public string CallbackEndpoint { get; init; } = "";
    [JsonRequired, JsonPropertyName("permission_profiles")] public ServiceLinkPermissionProfile[] PermissionProfiles { get; init; } = [];
    [JsonRequired, JsonPropertyName("supported_contracts")] public string[] SupportedContracts { get; init; } = [ServiceLinkContract.Version];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceLinkPermissionProfile(
    [property: JsonRequired, JsonPropertyName("capability")] string Capability,
    [property: JsonRequired, JsonPropertyName("scopes")] string[] Scopes,
    [property: JsonRequired, JsonPropertyName("operations")] ServiceLinkResourceOperation[] Operations);
public sealed record ServiceLinkResourceOperation(
    [property: JsonRequired, JsonPropertyName("method")] string Method,
    [property: JsonRequired, JsonPropertyName("path")] string Path,
    [property: JsonRequired, JsonPropertyName("scope")] string Scope);

/// <summary>Explicit resource tuples. Empty sets confer no authority; they never mean every resource.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceLinkResourceConstraints
{
    [JsonRequired, JsonPropertyName("organization_id")] public string? OrganizationId { get; init; }
    [JsonRequired, JsonPropertyName("customer_ids")] public string[] CustomerIds { get; init; } = [];
    [JsonRequired, JsonPropertyName("request_ids")] public string[] RequestIds { get; init; } = [];
    [JsonRequired, JsonPropertyName("task_ids")] public string[] TaskIds { get; init; } = [];
    [JsonRequired, JsonPropertyName("tenant_id")] public string? TenantId { get; init; }
    [JsonRequired, JsonPropertyName("resource_ids")] public string[] ResourceIds { get; init; } = [];
    [JsonRequired, JsonPropertyName("request_definition_ids")] public string[] RequestDefinitionIds { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceLinkGrant
{
    [JsonRequired, JsonPropertyName("direction_id")] public string DirectionId { get; init; } = "";
    [JsonRequired, JsonPropertyName("caller_snapshot")] public string CallerSnapshot { get; init; } = "";
    [JsonRequired, JsonPropertyName("target_snapshot")] public string TargetSnapshot { get; init; } = "";
    [JsonRequired, JsonPropertyName("caller_product")] public string CallerProduct { get; init; } = "";
    [JsonRequired, JsonPropertyName("caller_instance_id")] public string CallerInstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("caller_tenant_id")] public string CallerTenantId { get; init; } = "";
    [JsonRequired, JsonPropertyName("target_product")] public string TargetProduct { get; init; } = "";
    [JsonRequired, JsonPropertyName("target_instance_id")] public string TargetInstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("target_tenant_id")] public string TargetTenantId { get; init; } = "";
    [JsonRequired, JsonPropertyName("issuer")] public string Issuer { get; init; } = "";
    [JsonRequired, JsonPropertyName("audience")] public string Audience { get; init; } = "";
    [JsonRequired, JsonPropertyName("capabilities")] public string[] Capabilities { get; init; } = [];
    [JsonRequired, JsonPropertyName("scopes")] public string[] Scopes { get; init; } = [];
    [JsonRequired, JsonPropertyName("resource_constraints")] public ServiceLinkResourceConstraints ResourceConstraints { get; init; } = new();
    [JsonRequired, JsonPropertyName("source_instance_id")] public string? SourceInstanceId { get; init; }
    [JsonRequired, JsonPropertyName("source_namespace_id")] public string? SourceNamespaceId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceLinkRequestDescriptor
{
    [JsonRequired, JsonPropertyName("contract")] public string Contract { get; init; } = ServiceLinkContract.Version;
    [JsonRequired, JsonPropertyName("attempt_id")] public string AttemptId { get; init; } = "";
    [JsonRequired, JsonPropertyName("expires_at")] public string ExpiresAt { get; init; } = "";
    [JsonRequired, JsonPropertyName("initiator_instance_id")] public string InitiatorInstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("initiator_tenant_id")] public string InitiatorTenantId { get; init; } = "";
    [JsonRequired, JsonPropertyName("expected_responder_instance_id")] public string ExpectedResponderInstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("requested_responder_tenant_id")] public string? RequestedResponderTenantId { get; init; }
    [JsonRequired, JsonPropertyName("initiator_endpoint_snapshot")] public ServiceLinkMetadata InitiatorEndpointSnapshot { get; init; } = new();
    [JsonRequired, JsonPropertyName("responder_endpoint_snapshot")] public ServiceLinkMetadata ResponderEndpointSnapshot { get; init; } = new();
    [JsonRequired, JsonPropertyName("descriptor_hash")] public string DescriptorHash { get; init; } = "";
    [JsonRequired, JsonPropertyName("code_challenge")] public string CodeChallenge { get; init; } = "";
    [JsonRequired, JsonPropertyName("code_challenge_method")] public string CodeChallengeMethod { get; init; } = "S256";
    [JsonRequired, JsonPropertyName("requested_grants")] public ServiceLinkGrant[] RequestedGrants { get; init; } = [];
    [JsonRequired, JsonPropertyName("initiator_callback_endpoint")] public string InitiatorCallbackEndpoint { get; init; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceLinkGrantSummary
{
    [JsonRequired, JsonPropertyName("contract")] public string Contract { get; init; } = ServiceLinkContract.Version;
    [JsonRequired, JsonPropertyName("attempt_id")] public string AttemptId { get; init; } = "";
    [JsonRequired, JsonPropertyName("link_id")] public string LinkId { get; init; } = "";
    [JsonRequired, JsonPropertyName("proposed_link_revision")] public long ProposedLinkRevision { get; init; } = 1;
    [JsonRequired, JsonPropertyName("descriptor_hash")] public string DescriptorHash { get; init; } = "";
    [JsonRequired, JsonPropertyName("expires_at")] public string ExpiresAt { get; init; } = "";
    [JsonRequired, JsonPropertyName("initiator_instance_id")] public string InitiatorInstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("responder_instance_id")] public string ResponderInstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("initiator_endpoint_snapshot")] public ServiceLinkMetadata InitiatorEndpointSnapshot { get; init; } = new();
    [JsonRequired, JsonPropertyName("responder_endpoint_snapshot")] public ServiceLinkMetadata ResponderEndpointSnapshot { get; init; } = new();
    [JsonRequired, JsonPropertyName("grants")] public ServiceLinkGrant[] Grants { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceDirectionalCredential
{
    [JsonRequired, JsonPropertyName("client_id")] public string ClientId { get; init; } = "";
    [JsonRequired, JsonPropertyName("client_secret")] public string ClientSecret { get; init; } = "";
    [JsonRequired, JsonPropertyName("token_endpoint_auth_method")] public string TokenEndpointAuthMethod { get; init; } = "client_secret_post";
    [JsonRequired, JsonPropertyName("issuer")] public string Issuer { get; init; } = "";
    [JsonRequired, JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; init; } = "";
    [JsonRequired, JsonPropertyName("audience")] public string Audience { get; init; } = "";
    [JsonRequired, JsonPropertyName("scopes")] public string[] Scopes { get; init; } = [];
    [JsonRequired, JsonPropertyName("credential_revision")] public long CredentialRevision { get; init; } = 1;
    [JsonRequired, JsonPropertyName("caller_instance_id")] public string CallerInstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("caller_tenant_id")] public string CallerTenantId { get; init; } = "";
    [JsonRequired, JsonPropertyName("target_instance_id")] public string TargetInstanceId { get; init; } = "";
    [JsonRequired, JsonPropertyName("target_tenant_id")] public string TargetTenantId { get; init; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record ServiceLinkReviewRequest(
    [property: JsonRequired, JsonPropertyName("contract")] string Contract,
    [property: JsonRequired, JsonPropertyName("attempt_id")] string AttemptId,
    [property: JsonRequired, JsonPropertyName("pairing_code")] string PairingCode,
    [property: JsonRequired, JsonPropertyName("code_verifier")] string CodeVerifier,
    [property: JsonRequired, JsonPropertyName("descriptor_hash")] string DescriptorHash);
public sealed record ServiceLinkReviewResponse(
    [property: JsonRequired, JsonPropertyName("grant_summary")] ServiceLinkGrantSummary GrantSummary,
    [property: JsonRequired, JsonPropertyName("grant_hash")] string GrantHash,
    [property: JsonRequired, JsonPropertyName("lifecycle_state")] string LifecycleState);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceLinkExchangeRequest : ServiceLinkReviewRequest
{
    public ServiceLinkExchangeRequest(string contract, string attemptId, string pairingCode, string codeVerifier, string descriptorHash,
        string grantHash, string initiatorConsentId, ServiceDirectionalCredential credentialForResponder)
        : base(contract, attemptId, pairingCode, codeVerifier, descriptorHash)
        => (GrantHash, InitiatorConsentId, CredentialForResponder) = (grantHash, initiatorConsentId, credentialForResponder);
    [JsonRequired, JsonPropertyName("grant_hash")] public string GrantHash { get; init; }
    [JsonRequired, JsonPropertyName("initiator_consent_id")] public string InitiatorConsentId { get; init; }
    [JsonRequired, JsonPropertyName("credential_for_responder")] public ServiceDirectionalCredential CredentialForResponder { get; init; }
}
public sealed record ServiceLinkExchangeResponse(
    [property: JsonRequired, JsonPropertyName("contract")] string Contract,
    [property: JsonRequired, JsonPropertyName("attempt_id")] string AttemptId,
    [property: JsonRequired, JsonPropertyName("link_id")] string LinkId,
    [property: JsonRequired, JsonPropertyName("link_revision")] long LinkRevision,
    [property: JsonRequired, JsonPropertyName("grant_hash")] string GrantHash,
    [property: JsonRequired, JsonPropertyName("lifecycle_state")] string LifecycleState,
    [property: JsonRequired, JsonPropertyName("credential_for_initiator")] ServiceDirectionalCredential CredentialForInitiator);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceLinkLifecycleRequest
{
    [JsonIgnore] public IReadOnlySet<string>? WirePropertyNames { get; init; }
    [JsonRequired, JsonPropertyName("contract")] public string Contract { get; init; } = ServiceLinkContract.Version;
    [JsonRequired, JsonPropertyName("operation_id")] public string OperationId { get; init; } = "";
    [JsonPropertyName("attempt_id")] public string? AttemptId { get; init; }
    [JsonRequired, JsonPropertyName("link_id")] public string LinkId { get; init; } = "";
    [JsonRequired, JsonPropertyName("link_revision")] public long LinkRevision { get; init; }
    [JsonRequired, JsonPropertyName("grant_hash")] public string GrantHash { get; init; } = "";
    [JsonPropertyName("direction_id")] public string? DirectionId { get; init; }
    [JsonPropertyName("credential_revision")] public long? CredentialRevision { get; init; }
    [JsonPropertyName("rotation_id")] public string? RotationId { get; init; }
    [JsonPropertyName("commit_id")] public string? CommitId { get; init; }
    [JsonPropertyName("descriptor_hash")] public string? DescriptorHash { get; init; }
    [JsonPropertyName("initiator_verification_receipt_id")] public string? InitiatorVerificationReceiptId { get; init; }
    [JsonPropertyName("responder_verification_receipt_id")] public string? ResponderVerificationReceiptId { get; init; }
    [JsonPropertyName("ack_phase")] public string? AckPhase { get; init; }
    [JsonPropertyName("peer_operation_id")] public string? PeerOperationId { get; init; }
    [JsonPropertyName("exchange_response_hash")] public string? ExchangeResponseHash { get; init; }
    [JsonPropertyName("revocation_id")] public string? RevocationId { get; init; }
    [JsonPropertyName("expected_link_revision")] public long? ExpectedLinkRevision { get; init; }
    [JsonPropertyName("abort_phase")] public string? AbortPhase { get; init; }
    [JsonPropertyName("abort_id")] public string? AbortId { get; init; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; init; }
    [JsonPropertyName("rotation_phase")] public string? RotationPhase { get; init; }
    [JsonPropertyName("expected_current_credential_revision")] public long? ExpectedCurrentCredentialRevision { get; init; }
    [JsonPropertyName("successor_credential_revision")] public long? SuccessorCredentialRevision { get; init; }
    [JsonPropertyName("requested_by_instance_id")] public string? RequestedByInstanceId { get; init; }
    [JsonPropertyName("requested_policy_revision")] public long? RequestedPolicyRevision { get; init; }
    [JsonPropertyName("offer_expires_at")] public string? OfferExpiresAt { get; init; }
    [JsonPropertyName("credential_for_caller")] public ServiceDirectionalCredential? CredentialForCaller { get; init; }
    [JsonPropertyName("successor_verification_receipt_id")] public string? SuccessorVerificationReceiptId { get; init; }
    [JsonPropertyName("activate_decision_id")] public string? ActivateDecisionId { get; init; }
    [JsonPropertyName("caller_switch_revision")] public long? CallerSwitchRevision { get; init; }
}

// Local administrator actions are ordinary authenticated application commands, not peer protocol grants.
public sealed record ServiceLinkStartRequest(string PeerWebBaseUrl, string LocalTenantId, string? RequestedResponderTenantId,
    ServiceLinkGrant[] RequestedGrants, string SessionBinding)
{
    public string[] LocalCustomerIds { get; init; } = [];
    public string[] InboundScopes { get; init; } = ["rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.orchestration.callback"];
    public string[] OutboundScopes { get; init; } = ["netratel.orchestration.read", "netratel.orchestration.invoke"];
    public string[] OutboundResourceIds { get; init; } = [];
    public string[] OutboundRequestDefinitionIds { get; init; } = [];
}
public sealed record ServiceLinkRemoteReviewRequest(string InitiatorWebBaseUrl, string AttemptId, string BrowserState);
public sealed record ServiceLinkRemoteApproveRequest(string AttemptId, string LocalTenantId, ServiceLinkGrant[] Grants, string BrowserState);
public sealed record ServiceLinkCallbackRequest(string AttemptId, string PairingCode, string BrowserState,
    string ResponderInstanceId, string OauthIssuer, string SessionBinding);
public sealed record ServiceLinkLocalApproveRequest(string GrantHash, string SessionBinding);
public sealed record ServiceLinkNavigation(string AttemptId, string NavigationUrl, string LifecycleState);
public sealed record ServiceLinkAdminAction(string? ReasonCode = null, string? DirectionId = null);
public sealed record ServiceLinkAdminStatus(string AttemptId, string? LinkId, long LinkRevision, string LifecycleState,
    string LocalTenantId, string PeerInstanceId, string? PeerTenantId, string Decision, string? CommitId,
    string? GrantHash, ServiceLinkRequestDescriptor Descriptor, ServiceLinkGrantSummary? GrantSummary,
    bool LocalInboundReady, bool LocalOutboundPersisted, bool LocalInboundActive, bool LocalBusinessSenderEnabled,
    bool PeerActiveAcknowledged, string? LastErrorCode, bool DeploymentManaged, IReadOnlyList<ServiceLinkRotationSummary> Rotations)
{
    public bool AutomaticRotationEnabled { get; init; }
    public int RotationAgeDays { get; init; }
    public int RotationOverlapSeconds { get; init; }
}
public sealed record ServiceLinkRotationSummary(
    [property: JsonRequired, JsonPropertyName("rotation_id")] string RotationId,
    [property: JsonRequired, JsonPropertyName("direction_id")] string DirectionId,
    [property: JsonRequired, JsonPropertyName("rotation_state")] string RotationState,
    [property: JsonRequired, JsonPropertyName("expected_current_credential_revision")] long ExpectedCurrentCredentialRevision,
    [property: JsonRequired, JsonPropertyName("successor_credential_revision")] long? SuccessorCredentialRevision,
    [property: JsonRequired, JsonPropertyName("offer_expires_at")] string? OfferExpiresAt,
    [property: JsonRequired, JsonPropertyName("successor_verification_receipt_id")] string? SuccessorVerificationReceiptId,
    [property: JsonRequired, JsonPropertyName("activate_decision_id")] string? ActivateDecisionId,
    [property: JsonRequired, JsonPropertyName("caller_switch_revision")] long? CallerSwitchRevision,
    [property: JsonRequired, JsonPropertyName("predecessor_retire_at")] string? PredecessorRetireAt);
