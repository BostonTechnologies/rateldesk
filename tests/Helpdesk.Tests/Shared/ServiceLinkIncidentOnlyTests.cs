using System.Text.Json;
using Helpdesk.Shared.ServiceLink;
using Helpdesk.Infrastructure.ServiceLink;
using Xunit;

namespace Helpdesk.Tests.Shared;

public sealed class ServiceLinkIncidentOnlyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Negotiated_incident_only_grants_have_no_reverse_business_authority_and_preserve_v1_hashes(int role)
    {
        var summary = Fixture("bostec-service-link.incident-only.v1.fixtures.json", role);
        var grants = ServiceLinkValidation.Grants(summary.Grants, summary.InitiatorEndpointSnapshot, summary.ResponderEndpointSnapshot);
        var reverse = grants.Single(g => g.TargetProduct == "netratel");
        Assert.True(ServiceLinkValidation.IncidentOnlyGrant(reverse));
        Assert.Empty(reverse.ResourceConstraints.ResourceIds);
        Assert.Empty(reverse.ResourceConstraints.RequestDefinitionIds);
        Assert.DoesNotContain(reverse.Scopes, s => s.StartsWith("netratel.", StringComparison.Ordinal));
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(summary.Grants), ServiceLinkCanonicalJson.HashObject(grants));
        var legacy = Fixture("bostec-service-link.v1.fixtures.json", role);
        Assert.Equal(2, ServiceLinkValidation.Grants(legacy.Grants, legacy.InitiatorEndpointSnapshot, legacy.ResponderEndpointSnapshot).Length);
        Assert.False(ServiceLinkValidation.IncidentOnlyGrant(legacy.Grants.Single(g => g.TargetProduct == "netratel")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Missing_peer_negotiation_requires_upgrade_before_a_new_incident_only_grant(int role)
    {
        var summary = Fixture("bostec-service-link.incident-only.v1.fixtures.json", role);
        var peer = summary.ResponderEndpointSnapshot with { PermissionProfiles = summary.ResponderEndpointSnapshot.PermissionProfiles
            .Where(p => p.Capability != ServiceLinkContract.IncidentOnlyCapability).ToArray() };
        var error = Assert.Throws<ServiceLinkProtocolException>(() => ServiceLinkValidation.Grants(summary.Grants, summary.InitiatorEndpointSnapshot, peer));
        Assert.Equal("upgrade-required", error.Code);
        Assert.Equal(422, error.StatusCode);
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("business-scope")]
    [InlineData("legacy-control-capability")]
    public void Control_only_profile_cannot_be_used_to_smuggle_business_authority(string mutation)
    {
        var summary = Fixture("bostec-service-link.incident-only.v1.fixtures.json", 0);
        var changed = summary.Grants.Select(g => g.TargetProduct != "netratel" ? g : mutation switch
        {
            "resource" => g with { ResourceConstraints = g.ResourceConstraints with { ResourceIds = [Guid.NewGuid().ToString("D")] } },
            "business-scope" => g with { Scopes = [.. g.Scopes, "netratel.orchestration.read"] },
            _ => g with { Capabilities = [ServiceLinkContract.Version] }
        }).ToArray();
        Assert.Throws<ServiceLinkProtocolException>(() => ServiceLinkValidation.Grants(changed, summary.InitiatorEndpointSnapshot, summary.ResponderEndpointSnapshot));
    }

    [Fact]
    public void Incident_only_profile_cannot_enable_reverse_callbacks_or_omit_incident_permissions()
    {
        var summary = Fixture("bostec-service-link.incident-only.v1.fixtures.json", 0);
        foreach (var scopes in new[]
        {
            new[] { "rateldesk.incidents.create", "rateldesk.incident-receipts.read" },
            new[] { "rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.orchestration.callback" }
        })
        {
            var grants = summary.Grants.Select(g => g.TargetProduct == "rateldesk" ? g with { Scopes = scopes } : g).ToArray();
            Assert.Throws<ServiceLinkProtocolException>(() => ServiceLinkValidation.Grants(grants, summary.InitiatorEndpointSnapshot, summary.ResponderEndpointSnapshot));
        }
    }

    private static ServiceLinkGrantSummary Fixture(string filename, int role)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "contracts", filename);
            if (!File.Exists(path)) continue;
            using var fixtures = JsonDocument.Parse(File.ReadAllText(path));
            var item = fixtures.RootElement.GetProperty("roles")[role];
            var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(item.GetProperty("grant_summary").GetRawText());
            if (item.TryGetProperty("sha256", out var hash)) Assert.Equal(hash.GetString(), ServiceLinkCanonicalJson.HashObject(summary));
            return summary;
        }
        throw new InvalidOperationException("The owning contract fixture was not found.");
    }
}
