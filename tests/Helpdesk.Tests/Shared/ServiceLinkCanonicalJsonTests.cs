using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Helpdesk.Shared.ServiceLink;
using Helpdesk.Infrastructure.ServiceLink;

namespace Helpdesk.Tests.Shared;

public sealed class ServiceLinkCanonicalJsonTests
{
    [Theory]
    [InlineData("{\"z\":null,\"n\":1,\"a\":[true,\"x\"]}", "{\"a\":[true,\"x\"],\"n\":1,\"z\":null}", "bd92bf6940da187209c3f1092f1934110a77a5308ad639fc23cb110fd621c4ee")]
    [InlineData("{\"expires_at\":\"2026-10-04T15:00:00Z\",\"contract\":\"bostec.service-link.v1\",\"attempt_id\":\"test-attempt\"}", "{\"attempt_id\":\"test-attempt\",\"contract\":\"bostec.service-link.v1\",\"expires_at\":\"2026-10-04T15:00:00Z\"}", "85c791bf3a6272a64c8a86e9108cb8f92878db92296f789682466524155f595b")]
    public void Normative_hash_vectors_match(string json, string canonical, string hash)
    {
        Assert.Equal(canonical, ServiceLinkCanonicalJson.Canonicalize(json));
        Assert.Equal(hash, ServiceLinkCanonicalJson.Hash(json));
    }

    [Fact]
    public void Jcs_uses_minimal_escaping_and_preserves_unicode()
    {
        const string json = "{\"s\":\"\\u20ac\\u000f\\nA'B\\\"\\\\\\/\\ud83d\\ude00\"}";
        Assert.Equal("{\"s\":\"€\\u000f\\nA'B\\\"\\\\/😀\"}", ServiceLinkCanonicalJson.Canonicalize(json));
        Assert.NotEqual(ServiceLinkCanonicalJson.Hash("{\"s\":\"é\"}"),
            ServiceLinkCanonicalJson.Hash("{\"s\":\"e\\u0301\"}"));
    }

    [Fact]
    public void Object_order_is_utf16_ordinal_including_supplementary_characters()
    {
        const string json = "{\"\\ufb33\":\"last\",\"\\ud83d\\ude00\":\"emoji\",\"\\u20ac\":\"euro\",\"\\u00f6\":\"latin\",\"\\u0080\":\"control\",\"1\":\"digit\",\"\\r\":\"first\"}";
        Assert.Equal("{\"\\r\":\"first\",\"1\":\"digit\",\"\u0080\":\"control\",\"ö\":\"latin\",\"€\":\"euro\",\"😀\":\"emoji\",\"דּ\":\"last\"}",
            ServiceLinkCanonicalJson.Canonicalize(json));
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("{\"nested\":{\"a\":1,\"a\":2}}")]
    [InlineData("{\"s\":\"\\ud800\"}")]
    [InlineData("{\"s\":\"\\udc00\"}")]
    [InlineData("{\"\\ud800\":1}")]
    [InlineData("{\"n\":9007199254740992}")]
    [InlineData("{\"n\":-1}")]
    [InlineData("{\"n\":-0}")]
    [InlineData("{\"n\":1.0}")]
    [InlineData("{\"n\":1e0}")]
    [InlineData("{\"n\":NaN}")]
    [InlineData("{\"n\":1,}")]
    public void Invalid_unicode_duplicates_and_unsupported_number_forms_fail(string json) =>
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Canonicalize(json));

    [Fact]
    public void Unpaired_raw_surrogates_are_rejected_before_utf8_replacement() =>
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Canonicalize("{\"s\":\"" + '\ud800' + "\"}"));

    [Fact]
    public void Typed_hashing_rejects_invalid_unicode_before_serializer_replacement()
    {
        var invalid = new string('\ud800', 1);
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.HashObject(new Dictionary<string, string> { ["value"] = invalid }));
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.HashObject(new Dictionary<string, string> { [invalid] = "value" }));
    }

    [Fact]
    public void Maximum_safe_integer_and_array_order_remain_exact()
    {
        Assert.Equal("{\"n\":9007199254740991}", ServiceLinkCanonicalJson.Canonicalize("{\"n\":9007199254740991}"));
        Assert.NotEqual(ServiceLinkCanonicalJson.Hash("[\"a\",\"b\"]"), ServiceLinkCanonicalJson.Hash("[\"b\",\"a\"]"));
    }

    [Fact]
    public void Hash_projection_excludes_only_its_own_top_level_field()
    {
        var first = new Dictionary<string, object?>
        {
            ["attempt_id"] = "test-attempt",
            ["contract"] = "bostec.service-link.v1",
            ["expires_at"] = "2026-10-04T15:00:00Z",
            ["descriptor_hash"] = "self-field"
        };
        Assert.Equal("85c791bf3a6272a64c8a86e9108cb8f92878db92296f789682466524155f595b",
            ServiceLinkCanonicalJson.HashObject(first, "descriptor_hash"));
        first["descriptor_hash"] = "replacement-self-field";
        var unchanged = ServiceLinkCanonicalJson.HashObject(first, "descriptor_hash");
        first["expires_at"] = "2026-10-04T15:00:01Z";
        Assert.NotEqual(unchanged, ServiceLinkCanonicalJson.HashObject(first, "descriptor_hash"));

        var grant = new Dictionary<string, object?> { ["grant_hash"] = "self", ["descriptor_hash"] = "nested-binding" };
        var before = ServiceLinkCanonicalJson.HashObject(grant, "grant_hash");
        grant["descriptor_hash"] = "changed-binding";
        Assert.NotEqual(before, ServiceLinkCanonicalJson.HashObject(grant, "grant_hash"));
    }

    [Fact]
    public void Strict_deserialization_rejects_unknown_fields_and_property_case()
    {
        Assert.Equal(1, ServiceLinkCanonicalJson.Deserialize<KnownPayload>("{\"revision\":1}").Revision);
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<KnownPayload>("{\"revision\":1,\"unapproved\":true}"));
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<KnownPayload>("{\"Revision\":1}"));
        Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<KnownPayload>("{\"revision\":1,\"revision\":2}"));
    }

    [Theory]
    [InlineData("2026-10-04T15:00:00Z", true)]
    [InlineData("2026-10-04T15:00:00.000Z", false)]
    [InlineData("2026-10-04T17:00:00+02:00", false)]
    [InlineData("2026-02-30T15:00:00Z", false)]
    [InlineData("2026-10-04t15:00:00z", false)]
    public void Timestamp_wire_values_are_not_renormalized(string value, bool valid) =>
        Assert.Equal(valid, ServiceLinkCanonicalJson.IsWholeSecondUtcTimestamp(value));

    public static IEnumerable<object[]> PublishedVectors()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot,
            "docs", "contracts", "bostec-service-link.v1.fixtures.json")));
        foreach (var vector in fixtures.RootElement.GetProperty("canonical_vectors").EnumerateArray())
            yield return [vector.GetProperty("name").GetString()!, vector.GetProperty("input").GetString()!,
                vector.GetProperty("canonical").GetString()!, vector.GetProperty("sha256").GetString()!];
    }

    [Theory]
    [MemberData(nameof(PublishedVectors))]
    public void Shared_published_canonical_vectors_match(string name, string input, string canonical, string hash)
    {
        Assert.NotEmpty(name);
        Assert.Equal(canonical, ServiceLinkCanonicalJson.Canonicalize(input));
        Assert.Equal(hash, ServiceLinkCanonicalJson.Hash(input));
    }

    [Fact]
    public void Published_grants_and_endpoint_snapshots_satisfy_the_runtime_profile_in_both_roles()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot,
            "docs", "contracts", "bostec-service-link.v1.fixtures.json")));
        foreach (var role in fixtures.RootElement.GetProperty("roles").EnumerateArray())
        {
            var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(role.GetProperty("grant_summary").GetRawText());
            ServiceLinkValidation.Metadata(summary.InitiatorEndpointSnapshot, false);
            ServiceLinkValidation.Metadata(summary.ResponderEndpointSnapshot, false);
            var validated = ServiceLinkValidation.Grants(summary.Grants, summary.InitiatorEndpointSnapshot, summary.ResponderEndpointSnapshot);
            Assert.Equal(2, validated.Length);
            Assert.Equal(ServiceLinkCanonicalJson.HashObject(summary.Grants), ServiceLinkCanonicalJson.HashObject(validated));
        }
    }

    [Fact]
    public void Complete_typed_payloads_match_shared_hashes_in_both_product_roles()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot,
            "docs", "contracts", "bostec-service-link.v1.fixtures.json")));
        foreach (var role in fixtures.RootElement.GetProperty("roles").EnumerateArray())
        {
            var descriptor = ServiceLinkCanonicalJson.Deserialize<ServiceLinkRequestDescriptor>(role.GetProperty("descriptor").GetRawText());
            Assert.Equal(role.GetProperty("descriptor_hash").GetString(), ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash"));
            var grants = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(role.GetProperty("grant_summary").GetRawText());
            Assert.Equal(role.GetProperty("grant_hash").GetString(), ServiceLinkCanonicalJson.HashObject(grants));
            var review = ServiceLinkCanonicalJson.Deserialize<ServiceLinkReviewResponse>(role.GetProperty("review_response").GetRawText());
            Assert.Equal("approved", review.LifecycleState);
            Assert.Equal(ServiceLinkCanonicalJson.HashObject(grants), review.GrantHash);
            var exchange = ServiceLinkCanonicalJson.Deserialize<ServiceLinkExchangeRequest>(role.GetProperty("exchange_request").GetRawText());
            Assert.Equal(role.GetProperty("exchange_request_fingerprint").GetString(), ServiceLinkCanonicalJson.HashObject(exchange));
            var response = ServiceLinkCanonicalJson.Deserialize<ServiceLinkExchangeResponse>(role.GetProperty("exchange_response").GetRawText());
            Assert.Equal(role.GetProperty("exchange_response_hash").GetString(), ServiceLinkCanonicalJson.HashObject(response));
            Assert.NotEqual(exchange.CredentialForResponder.ClientId, response.CredentialForInitiator.ClientId);
            Assert.NotEqual(exchange.CredentialForResponder.ClientSecret, response.CredentialForInitiator.ClientSecret);
            var commit = ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(role.GetProperty("commit_request").GetRawText());
            Assert.Equal(role.GetProperty("commit_request_fingerprint").GetString(), ServiceLinkLifecycleProjection.Hash("commit", commit));
            foreach (var (property, kind) in new[] { ("verify_request", "verify"), ("prepared_ack_request", "ack"), ("rotation_request", "rotate") })
            {
                var wire = role.GetProperty(property).GetRawText();
                var request = ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(wire);
                Assert.Equal(ServiceLinkCanonicalJson.Hash(wire), ServiceLinkLifecycleProjection.Hash(kind, request));
            }
        }
    }

    [Fact]
    public void Frozen_metadata_requires_explicit_presence_of_nullable_snapshot_fields()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot,
            "docs", "contracts", "bostec-service-link.v1.fixtures.json")));
        var original = fixtures.RootElement.GetProperty("roles")[0].GetProperty("metadata").GetProperty("initiator").GetRawText();
        Assert.Null(ServiceLinkCanonicalJson.Deserialize<ServiceLinkMetadata>(original).GatewayBaseUrl);
        foreach (var field in new[] { "gateway_base_url", "source_instance_id", "permission_profiles", "callback_endpoint" })
        {
            var changed = JsonNode.Parse(original)!.AsObject();
            Assert.True(changed.Remove(field));
            Assert.Throws<JsonException>(() => ServiceLinkCanonicalJson.Deserialize<ServiceLinkMetadata>(changed.ToJsonString()));
        }
    }

    [Fact]
    public void Every_approved_endpoint_identity_and_constraint_is_bound_by_the_grant_hash()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot,
            "docs", "contracts", "bostec-service-link.v1.fixtures.json")));
        var original = fixtures.RootElement.GetProperty("roles")[0].GetProperty("grant_summary").GetRawText();
        var digest = ServiceLinkCanonicalJson.Hash(original);
        Action<JsonNode>[] mutations =
        [
            node => node["descriptor_hash"] = new string('a', 64),
            node => node["proposed_link_revision"] = 2,
            node => node["initiator_endpoint_snapshot"]!["oauth_issuer"] = "https://changed.example",
            node => node["responder_endpoint_snapshot"]!["token_endpoint"] = "https://changed.example/connect/token",
            node => node["responder_endpoint_snapshot"]!["api_base_url"] = "https://changed.example",
            node => node["responder_endpoint_snapshot"]!["service_link_endpoint"] = "https://changed.example/link",
            node => node["responder_endpoint_snapshot"]!["instance_id"] = "changed-instance",
            node => node["grants"]![0]!["target_tenant_id"] = "43",
            node => node["grants"]![0]!["audience"] = "changed-audience",
            node => node["grants"]![0]!["scopes"]!.AsArray().Add("unapproved.scope"),
            node => node["grants"]![0]!["resource_constraints"]!["resource_ids"]!.AsArray().Add("foreign-resource"),
            node => node["grants"]![1]!["source_instance_id"] = "changed-source",
            node => node["grants"]![1]!["source_namespace_id"] = "changed-namespace",
            node => node["grants"]![1]!["resource_constraints"]!["customer_ids"]!.AsArray().Add("foreign-customer")
        ];
        foreach (var mutation in mutations)
        {
            var changed = JsonNode.Parse(original)!;
            mutation(changed);
            Assert.NotEqual(digest, ServiceLinkCanonicalJson.Hash(changed.ToJsonString()));
        }
    }

    [Fact]
    public void Normative_prose_and_published_artifacts_have_their_pinned_digests()
    {
        var directory = Path.Combine(TestEnvironment.RepositoryRoot, "docs", "contracts");
        Assert.Equal("ab3a33e61a33cf86744a9cb1b58c24513ca5bb193bbc1566a2e16362866c7f77",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "bostec-service-link.v1.md")))));
        foreach (var line in File.ReadAllLines(Path.Combine(directory, "bostec-service-link.v1.SHA256SUMS")))
        {
            var fields = line.Split("  ", StringSplitOptions.None);
            Assert.Equal(2, fields.Length);
            Assert.Equal(Path.GetFileName(fields[1]), fields[1]);
            Assert.Equal(fields[0], Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, fields[1])))));
        }
    }

    [Fact]
    public void Lifecycle_projection_preserves_required_nulls_and_excludes_unrelated_union_fields()
    {
        var request = new ServiceLinkLifecycleRequest
        {
            OperationId = "operation", AttemptId = "attempt", LinkId = "link", LinkRevision = 1,
            GrantHash = new string('a', 64), DirectionId = "initiator_to_responder", CredentialRevision = 1
        };
        var verify = ServiceLinkLifecycleProjection.Build("verify", request);
        Assert.True(verify.ContainsKey("rotation_id"));
        Assert.Null(verify["rotation_id"]);
        Assert.DoesNotContain("commit_id", verify.Keys);
        Assert.DoesNotContain("credential_for_caller", verify.Keys);
        var commit = ServiceLinkLifecycleProjection.Build("commit", request with
        {
            DirectionId = null, CredentialRevision = null,
            CommitId = "decision", DescriptorHash = new string('b', 64),
            InitiatorVerificationReceiptId = "first", ResponderVerificationReceiptId = "second"
        });
        Assert.DoesNotContain("rotation_id", commit.Keys);
        Assert.DoesNotContain("credential_revision", commit.Keys);
    }

    [Fact]
    public void Rotation_projection_binds_offer_secret_and_only_the_selected_phase_fields()
    {
        var offer = new ServiceLinkLifecycleRequest
        {
            OperationId = "offer-operation", LinkId = "link", LinkRevision = 1, GrantHash = new string('a', 64),
            RotationId = "rotation", RotationPhase = "offer", DirectionId = "initiator_to_responder",
            ExpectedCurrentCredentialRevision = 1, SuccessorCredentialRevision = 2,
            OfferExpiresAt = "2026-10-04T15:00:00Z", CredentialForCaller = new() { ClientSecret = "first-synthetic-secret" }
        };
        var payload = ServiceLinkLifecycleProjection.Build("rotate", offer);
        Assert.DoesNotContain("requested_by_instance_id", payload.Keys);
        Assert.DoesNotContain("activate_decision_id", payload.Keys);
        Assert.NotEqual(ServiceLinkLifecycleProjection.Hash("rotate", offer), ServiceLinkLifecycleProjection.Hash("rotate",
            offer with { CredentialForCaller = offer.CredentialForCaller! with { ClientSecret = "changed-synthetic-secret" } }));
        var switched = offer with { RotationPhase = "switched", CredentialForCaller = null, OfferExpiresAt = null,
            ActivateDecisionId = "activation", SuccessorVerificationReceiptId = "receipt", CallerSwitchRevision = 2 };
        var switchedPayload = ServiceLinkLifecycleProjection.Build("rotate", switched);
        Assert.DoesNotContain("credential_for_caller", switchedPayload.Keys);
        Assert.DoesNotContain("offer_expires_at", switchedPayload.Keys);
        Assert.Equal("activation", switchedPayload["activate_decision_id"]);
        Assert.Throws<JsonException>(() => ServiceLinkLifecycleProjection.Build("rotate", switched with { CredentialForCaller = offer.CredentialForCaller }));
    }

    private sealed record KnownPayload([property: JsonPropertyName("revision")] int Revision);
}
