using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Shared.ServiceLink;
using Helpdesk.Infrastructure.ServiceLink;
using Xunit;

namespace Helpdesk.Tests.Shared;

public sealed class ServiceLinkPayloadNormalizationTests
{
    [Theory]
    [InlineData("link_revision")]
    [InlineData("credential_revision")]
    [InlineData("expected_link_revision")]
    [InlineData("expected_current_credential_revision")]
    [InlineData("successor_credential_revision")]
    [InlineData("requested_policy_revision")]
    [InlineData("caller_switch_revision")]
    public void Every_lifecycle_revision_requires_a_json_number(string field)
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(new ServiceLinkLifecycleRequest
        {
            OperationId = "operation", LinkId = "link", GrantHash = "hash", LinkRevision = 1,
            CredentialRevision = 1, ExpectedLinkRevision = 1, ExpectedCurrentCredentialRevision = 1,
            SuccessorCredentialRevision = 2, RequestedPolicyRevision = 1, CallerSwitchRevision = 1
        }, ServiceLinkCanonicalJson.Json))!.AsObject();
        Assert.NotNull(ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(payload.ToJsonString()));
        payload[field] = payload[field]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(payload.ToJsonString()));
    }

    [Theory]
    [InlineData("credential_revision")]
    [InlineData("expected_link_revision")]
    [InlineData("expected_current_credential_revision")]
    [InlineData("successor_credential_revision")]
    [InlineData("requested_policy_revision")]
    [InlineData("caller_switch_revision")]
    public void Nullable_lifecycle_revisions_accept_explicit_null_without_coercing_strings(string field)
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(new ServiceLinkLifecycleRequest
        { OperationId = "operation", LinkId = "link", GrantHash = "hash", LinkRevision = 1 }, ServiceLinkCanonicalJson.Json))!.AsObject();
        Assert.Null(payload[field]);
        Assert.NotNull(ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(payload.ToJsonString()));
        payload[field] = "1";
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(payload.ToJsonString()));
    }

    [Fact]
    public void Credential_summary_and_exchange_response_revisions_require_json_numbers()
    {
        AssertQuotedNumberRejected(Credential(), "credential_revision");
        AssertQuotedNumberRejected(new ServiceLinkGrantSummary(), "proposed_link_revision");
        AssertQuotedNumberRejected(new ServiceLinkExchangeResponse(ServiceLinkContract.Version, "attempt", "link", 1,
            "hash", "prepared", Credential()), "link_revision");
        AssertQuotedNumberRejected(new ServiceLinkExchangeRequest(ServiceLinkContract.Version, "attempt", "pairing-code",
            "verifier", "descriptor", "hash", "consent", Credential()), "credential_for_responder", "credential_revision");
        AssertQuotedNumberRejected(new ServiceLinkExchangeResponse(ServiceLinkContract.Version, "attempt", "link", 1,
            "hash", "prepared", Credential()), "credential_for_initiator", "credential_revision");
    }

    [Theory]
    [InlineData("expected_current_credential_revision")]
    [InlineData("successor_credential_revision")]
    [InlineData("caller_switch_revision")]
    public void Rotation_summary_revisions_require_json_numbers(string field) =>
        AssertQuotedNumberRejected(new ServiceLinkRotationSummary("rotation", "direction", "prepared", 1, 2,
            "2026-10-05T12:00:00Z", null, null, 3, null), field);

    [Theory]
    [InlineData("contract")]
    [InlineData("operation_id")]
    [InlineData("link_id")]
    [InlineData("grant_hash")]
    public void Required_lifecycle_strings_reject_explicit_null(string field)
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(new ServiceLinkLifecycleRequest
        { OperationId = "operation", LinkId = "link", GrantHash = "hash", LinkRevision = 1 }, ServiceLinkCanonicalJson.Json))!.AsObject();
        payload[field] = null;
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(payload.ToJsonString()));
    }

    [Fact]
    public void Required_review_and_metadata_strings_reject_null_and_null_scope_elements_have_a_bounded_error()
    {
        var review = JsonNode.Parse(JsonSerializer.Serialize(new ServiceLinkReviewRequest(ServiceLinkContract.Version,
            "attempt", "pairing-code", "verifier", "descriptor"), ServiceLinkCanonicalJson.Json))!.AsObject();
        review["pairing_code"] = null;
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<ServiceLinkReviewRequest>(review.ToJsonString()));
        var metadata = JsonNode.Parse(JsonSerializer.Serialize(new ServiceLinkMetadata(), ServiceLinkCanonicalJson.Json))!.AsObject();
        metadata["instance_id"] = null;
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<ServiceLinkMetadata>(metadata.ToJsonString()));
        var error = Assert.Throws<ServiceLinkProtocolException>(() => ServiceLinkValidation.Set([null!]));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid-set", error.Code);
    }

    [Theory]
    [InlineData("supported_contracts")]
    [InlineData("token_endpoint_auth_methods_supported")]
    [InlineData("permission_profiles")]
    public void Required_metadata_arrays_reject_explicit_null(string field)
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(new ServiceLinkMetadata(), ServiceLinkCanonicalJson.Json))!.AsObject();
        payload[field] = null;
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<ServiceLinkMetadata>(payload.ToJsonString()));
    }

    [Theory]
    [InlineData("oauth_metadata_url")]
    [InlineData("jwks_uri")]
    public void Nullable_discovery_fields_parse_as_null_but_require_upgrade_before_guided_linking(string field)
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "contracts", "bostec-service-link.v1.fixtures.json")));
        var payload = JsonNode.Parse(fixtures.RootElement.GetProperty("roles")[0].GetProperty("metadata").GetProperty("initiator").GetRawText())!.AsObject();
        payload[field] = null;
        var metadata = ServiceLinkCanonicalJson.Deserialize<ServiceLinkMetadata>(payload.ToJsonString());
        var error = Assert.Throws<ServiceLinkProtocolException>(() => ServiceLinkValidation.Metadata(metadata, false));
        Assert.Equal(422, error.StatusCode);
        Assert.Equal("upgrade-required", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Final_reciprocal_grants_reject_a_different_caller_tenant_in_either_direction(int roleIndex)
    {
        var root = FindRepositoryRoot();
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts", "bostec-service-link.v1.fixtures.json")));
        var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(fixtures.RootElement.GetProperty("roles")[roleIndex].GetProperty("grant_summary").GetRawText());
        Assert.Equal(2, ServiceLinkValidation.Grants(summary.Grants, summary.InitiatorEndpointSnapshot, summary.ResponderEndpointSnapshot).Length);
        foreach (var direction in new[] { ServiceLinkContract.InitiatorToResponder, ServiceLinkContract.ResponderToInitiator })
        {
            var changed = summary.Grants.Select(g => g.DirectionId == direction ? g with { CallerTenantId = "different-selected-tenant" } : g).ToArray();
            var error = Assert.Throws<ServiceLinkProtocolException>(() => ServiceLinkValidation.Grants(changed, summary.InitiatorEndpointSnapshot, summary.ResponderEndpointSnapshot));
            Assert.Equal(400, error.StatusCode);
            Assert.Equal("tenant-pair-mismatch", error.Code);
        }
    }

    [Fact]
    public void Directional_credential_scope_set_is_normalized_without_changing_generic_ordered_arrays()
    {
        var original = Credential();
        var reversed = original with { Scopes = original.Scopes.Reverse().ToArray() };
        var normalized = ServiceLinkPayloadNormalization.Credential(original);
        Assert.Equal(original.Scopes.Order(StringComparer.Ordinal), normalized.Scopes);
        Assert.Equal(new[] { "z.business", ServiceLinkContract.VerifyScope, ServiceLinkContract.ControlScope }, original.Scopes);
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(normalized),
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(reversed)));
        Assert.NotEqual(ServiceLinkCanonicalJson.HashObject(original), ServiceLinkCanonicalJson.HashObject(reversed));
        Assert.NotEqual(ServiceLinkCanonicalJson.Hash("{\"ordered\":[\"a\",\"b\"]}"),
            ServiceLinkCanonicalJson.Hash("{\"ordered\":[\"b\",\"a\"]}"));
        Assert.Throws<JsonException>(() => ServiceLinkPayloadNormalization.Credential(original with { Scopes = ["z", "a", "a"] }));
        Assert.Throws<JsonException>(() => ServiceLinkPayloadNormalization.Credential(original with { Scopes = [null!] }));
        Assert.Throws<JsonException>(() => ServiceLinkPayloadNormalization.Credential(original with { Scopes = null! }));
    }

    [Fact]
    public void Descriptor_and_summary_declared_sets_normalize_without_changing_ordered_operations_or_legacy_hash_acceptance()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "contracts", "bostec-service-link.v1.fixtures.json")));
        var role = fixtures.RootElement.GetProperty("roles")[0];
        var descriptor = ServiceLinkCanonicalJson.Deserialize<ServiceLinkRequestDescriptor>(role.GetProperty("descriptor").GetRawText());
        var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(role.GetProperty("grant_summary").GetRawText());
        var originalDescriptorHash = ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash");
        var originalSummaryHash = ServiceLinkCanonicalJson.HashObject(summary);
        var reorderedDescriptor = descriptor with
        {
            InitiatorEndpointSnapshot = ReorderScopeSets(descriptor.InitiatorEndpointSnapshot),
            ResponderEndpointSnapshot = ReorderScopeSets(descriptor.ResponderEndpointSnapshot),
            RequestedGrants = ReorderGrantSets(descriptor.RequestedGrants)
        };
        var reorderedSummary = summary with
        {
            InitiatorEndpointSnapshot = ReorderScopeSets(summary.InitiatorEndpointSnapshot),
            ResponderEndpointSnapshot = ReorderScopeSets(summary.ResponderEndpointSnapshot),
            Grants = ReorderGrantSets(summary.Grants)
        };
        Assert.NotEqual(ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash"), ServiceLinkCanonicalJson.HashObject(reorderedDescriptor, "descriptor_hash"));
        Assert.NotEqual(ServiceLinkCanonicalJson.HashObject(summary), ServiceLinkCanonicalJson.HashObject(reorderedSummary));
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Descriptor(descriptor), "descriptor_hash"),
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Descriptor(reorderedDescriptor), "descriptor_hash"));
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Summary(summary)),
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Summary(reorderedSummary)));
        Assert.True(ServiceLinkPayloadNormalization.DescriptorHashMatches(reorderedDescriptor, ServiceLinkCanonicalJson.HashObject(reorderedDescriptor, "descriptor_hash")));
        Assert.True(ServiceLinkPayloadNormalization.DescriptorHashMatches(reorderedDescriptor,
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Descriptor(descriptor), "descriptor_hash")));
        Assert.True(ServiceLinkPayloadNormalization.SummaryHashMatches(reorderedSummary, ServiceLinkCanonicalJson.HashObject(reorderedSummary)));
        Assert.True(ServiceLinkPayloadNormalization.SummaryHashMatches(reorderedSummary,
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Summary(summary))));
        var metadata = summary.InitiatorEndpointSnapshot;
        var profile = metadata.PermissionProfiles.First(p => p.Operations.Length > 1);
        var changedOperations = metadata with { PermissionProfiles = metadata.PermissionProfiles.Select(p =>
            ReferenceEquals(p, profile) ? p with { Operations = p.Operations.Reverse().ToArray() } : p).ToArray() };
        Assert.NotEqual(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(metadata)),
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(changedOperations)));
        Assert.Equal(originalDescriptorHash, ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash"));
        Assert.Equal(originalSummaryHash, ServiceLinkCanonicalJson.HashObject(summary));
    }

    [Fact]
    public void Exchange_and_rotation_retry_hashes_ignore_scope_order_but_bind_every_other_credential_field()
    {
        var credential = Credential();
        var exchange = new ServiceLinkExchangeRequest(ServiceLinkContract.Version, "attempt", "pairing-code", "verifier",
            "descriptor", "hash", "consent", credential);
        var reorderedExchange = exchange with { CredentialForResponder = credential with { Scopes = credential.Scopes.Reverse().ToArray() } };
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Exchange(exchange)),
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Exchange(reorderedExchange)));
        Assert.NotEqual(ServiceLinkCanonicalJson.HashObject(exchange), ServiceLinkCanonicalJson.HashObject(reorderedExchange));
        foreach (var changed in ChangedCredentials(credential))
            Assert.NotEqual(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Exchange(exchange)),
                ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Exchange(exchange with { CredentialForResponder = changed })));
        Assert.NotEqual(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Exchange(exchange)),
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Exchange(exchange with { InitiatorConsentId = "different-consent" })));

        var offer = new ServiceLinkLifecycleRequest
        {
            OperationId = "operation", LinkId = "link", GrantHash = "hash", LinkRevision = 1,
            RotationId = "rotation", RotationPhase = "offer", DirectionId = ServiceLinkContract.InitiatorToResponder,
            ExpectedCurrentCredentialRevision = 1, SuccessorCredentialRevision = 2,
            OfferExpiresAt = "2026-10-05T12:00:00Z", CredentialForCaller = credential
        };
        var reorderedOffer = offer with { CredentialForCaller = credential with { Scopes = credential.Scopes.Reverse().ToArray() } };
        Assert.Equal(ServiceLinkLifecycleProjection.Hash("rotate", offer), ServiceLinkLifecycleProjection.Hash("rotate", reorderedOffer));
        Assert.NotEqual(ServiceLinkLifecycleProjection.Hash("rotate", offer, false), ServiceLinkLifecycleProjection.Hash("rotate", reorderedOffer, false));
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(ServiceLinkLifecycleProjection.Build("rotate", offer, false)),
            ServiceLinkLifecycleProjection.Hash("rotate", offer, false));
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Lifecycle(offer).CredentialForCaller),
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Lifecycle(reorderedOffer).CredentialForCaller));
        foreach (var changed in ChangedCredentials(credential))
            Assert.NotEqual(ServiceLinkLifecycleProjection.Hash("rotate", offer),
                ServiceLinkLifecycleProjection.Hash("rotate", offer with { CredentialForCaller = changed }));
        Assert.NotEqual(ServiceLinkLifecycleProjection.Hash("rotate", offer),
            ServiceLinkLifecycleProjection.Hash("rotate", offer with { OfferExpiresAt = "2026-10-05T12:01:00Z" }));
    }

    private static void AssertQuotedNumberRejected<T>(T value, params string[] path)
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(value, ServiceLinkCanonicalJson.Json))!.AsObject();
        Assert.NotNull(ServiceLinkCanonicalJson.Deserialize<T>(payload.ToJsonString()));
        JsonObject parent = payload;
        foreach (var part in path[..^1]) parent = parent[part]!.AsObject();
        parent[path[^1]] = parent[path[^1]]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<T>(payload.ToJsonString()));
    }

    private static IEnumerable<ServiceDirectionalCredential> ChangedCredentials(ServiceDirectionalCredential value)
    {
        yield return value with { ClientSecret = "changed-synthetic-secret" };
        yield return value with { Audience = "changed-audience" };
        yield return value with { CredentialRevision = value.CredentialRevision + 1 };
        yield return value with { CallerTenantId = "changed-tenant" };
        yield return value with { Scopes = value.Scopes.Append("unapproved.scope").ToArray() };
    }

    private static ServiceLinkMetadata ReorderScopeSets(ServiceLinkMetadata metadata) => metadata with
    { PermissionProfiles = metadata.PermissionProfiles.Select(p => p with { Scopes = p.Scopes.Reverse().ToArray() }).ToArray() };
    private static ServiceLinkGrant[] ReorderGrantSets(ServiceLinkGrant[] grants) => grants.Select(g =>
        g with { Scopes = g.Scopes.Reverse().ToArray(), Capabilities = g.Capabilities.Reverse().ToArray() }).Reverse().ToArray();

    private static ServiceDirectionalCredential Credential() => new()
    {
        ClientId = "synthetic-client", ClientSecret = "synthetic-secret", CredentialRevision = 2,
        Issuer = "https://issuer.example/", TokenEndpoint = "https://issuer.example/connect/token", Audience = "synthetic-audience",
        CallerInstanceId = "caller", CallerTenantId = "caller-tenant", TargetInstanceId = "target", TargetTenantId = "target-tenant",
        Scopes = ["z.business", ServiceLinkContract.VerifyScope, ServiceLinkContract.ControlScope]
    };

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Helpdesk.sln"))) return directory.FullName;
        throw new InvalidOperationException("The canonical regression requires the owning repository fixtures.");
    }
}
