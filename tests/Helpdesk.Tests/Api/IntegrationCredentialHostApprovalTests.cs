using Helpdesk.API.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Api;

public sealed partial class IntegrationCredentialPairingTests
{
    private static HostDeploymentProposalRequest HostProposal => new("HTTP MCP deployment", new('A', 64), new('B', 64),
        "https://desk.example", "https://desk.example/mcp", "sandbox-47", "gateway", "http://api:8222/", "http://mcp-http:8223/", ["Enable reviewed Web MCP route"]);

    private static HostDeploymentApprovalService Hosts(Fixture fixture, Fixture.ScopeSession session)
    {
        var owners = new IntegrationCredentialOwnerResolver(session.Domain, session.Identity);
        return new(session.Identity, session.Service, owners, new CurrentUserAccessService(session.Domain, session.Identity), fixture.Protection(), fixture.Clock);
    }

    [Fact]
    public async Task Host_review_approves_exact_generation_without_issuing_or_rotating_a_credential()
    {
        await using var fixture = await Fixture.CreateAsync();
        IntegrationPairingStarted start;
        await using (var session = fixture.Session())
        {
            var hosts = Hosts(fixture, session);
            start = await hosts.StartAsync(HostProposal, "ip", default);
            Assert.EndsWith("?kind=host", start.VerificationUri);
            Assert.Null(await session.Service.ReviewAsync(start.HumanCode, Owner, "ip", default));
            Assert.Equal("expired", (await session.Service.PollAsync(start.DeviceCode, default)).Status);
            var review = await hosts.ReviewAsync(start.HumanCode, Owner, "ip", default);
            Assert.Equal(HostProposal.ProposalHash, review!.Proposal.ProposalHash);
            Assert.Equal(HostProposal.SourceHash, review.Proposal.SourceHash);
            Assert.Equal(HostProposal.Changes.ToArray(), review.Proposal.Changes.ToArray());
            Assert.Equal("pending", (await hosts.PollAsync(start.DeviceCode, default)).Status);
            await Assert.ThrowsAsync<CredentialGrantException>(() => hosts.ApproveAsync(new(start.HumanCode, HostProposal.ProposalHash, HostProposal.SourceHash, false), Owner, default));
            await Assert.ThrowsAsync<CredentialGrantException>(() => hosts.ApproveAsync(new(start.HumanCode, new('C', 64), HostProposal.SourceHash, true), Owner, default));
            Assert.True(await hosts.ApproveAsync(new(start.HumanCode, HostProposal.ProposalHash, HostProposal.SourceHash, true), Owner, default));
            Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
            var row = await session.Identity.IntegrationCredentialPairings.SingleAsync();
            Assert.Equal("http-host", row.Purpose); Assert.Null(row.CredentialId); Assert.Null(row.ProtectedDelivery);
            Assert.DoesNotContain(HostProposal.PublicOrigin, row.ProtectedApproval!);
        }
        await using var resumed = fixture.Session();
        var approved = await Hosts(fixture, resumed).PollAsync(start.DeviceCode, default);
        Assert.Equal("approved", approved.Status); Assert.Equal("owner", approved.ApprovedBy);
        Assert.Equal(HostProposal.ProposalHash, approved.ProposalHash); Assert.Equal(HostProposal.SourceHash, approved.SourceHash);
        Assert.Empty(await resumed.Identity.IntegrationCredentials.ToListAsync());
    }

    [Fact]
    public async Task Host_consent_rechecks_current_administrator_and_enabled_owner_authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = fixture.Session();
        var hosts = Hosts(fixture, session);
        var started = await hosts.StartAsync(HostProposal, "ip", default);
        var user = await session.Identity.Users.SingleAsync(); user.IsInstanceAdministrator = false;
        await session.Identity.SaveChangesAsync();
        await Assert.ThrowsAsync<CredentialGrantException>(() => hosts.ReviewAsync(started.HumanCode, Owner, "ip", default));
        await Assert.ThrowsAsync<CredentialGrantException>(() => hosts.ApproveAsync(new(started.HumanCode, HostProposal.ProposalHash, HostProposal.SourceHash, true), Owner, default));
        user.IsInstanceAdministrator = true; await session.Identity.SaveChangesAsync();
        Assert.True(await hosts.ApproveAsync(new(started.HumanCode, HostProposal.ProposalHash, HostProposal.SourceHash, true), Owner, default));
        user.IsEnabled = false; await session.Identity.SaveChangesAsync();
        Assert.Equal("denied", (await hosts.PollAsync(started.DeviceCode, default)).Status);
        Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
    }

    [Fact]
    public async Task Host_codes_are_kind_bound_and_cancellation_expiry_are_bounded()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = fixture.Session();
        var hosts = Hosts(fixture, session);
        var client = await session.Service.StartAsync(new("CLI", "api"), "ip", default);
        Assert.Null(await hosts.ReviewAsync(client.HumanCode, Owner, "ip", default));
        var host = await hosts.StartAsync(HostProposal, "ip", default);
        await hosts.CancelAsync(host.DeviceCode, default); await hosts.CancelAsync(host.DeviceCode, default);
        Assert.Equal("cancelled", (await hosts.PollAsync(host.DeviceCode, default)).Status);
        Assert.False(await hosts.ApproveAsync(new(host.HumanCode, HostProposal.ProposalHash, HostProposal.SourceHash, true), Owner, default));
        var expiring = await hosts.StartAsync(HostProposal, "ip", default);
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("expired", (await hosts.PollAsync(expiring.DeviceCode, default)).Status);
        Assert.Null(await hosts.ReviewAsync(expiring.HumanCode, Owner, "ip", default));
    }

    [Theory]
    [InlineData("https://DESK.example:443", "https://DESK.example:443/mcp")]
    [InlineData("https://desk.example", "https://desk.example/mcp")]
    public async Task Host_consent_preserves_equivalent_explicit_authority_and_resumes_with_current_authority(string origin, string resource)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = fixture.Session();
        var hosts = Hosts(fixture, session);
        var proposal = HostProposal with { PublicOrigin = origin, ResourceUri = resource };
        var started = await hosts.StartAsync(proposal, "ip", default);
        Assert.True(await hosts.ApproveAsync(new(started.HumanCode, proposal.ProposalHash, proposal.SourceHash, true), Owner, default));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("approved", (await hosts.PollAsync(started.DeviceCode, default)).Status);
        (await session.Identity.Users.SingleAsync()).IsInstanceAdministrator = false;
        await session.Identity.SaveChangesAsync();
        Assert.Equal("denied", (await hosts.PollAsync(started.DeviceCode, default)).Status);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("resource")]
    [InlineData("secret-backend")]
    [InlineData("hash")]
    public async Task Host_proposal_rejects_wrong_target_or_unbounded_review(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = fixture.Session();
        var proposal = kind switch
        {
            "origin" => HostProposal with { PublicOrigin = "https://elsewhere.example" },
            "resource" => HostProposal with { ResourceUri = "https://desk.example/other" },
            "secret-backend" => HostProposal with { ApiBaseUrl = "http://user:password@api:8222/" },
            _ => HostProposal with { ProposalHash = "invalid" }
        };
        await Assert.ThrowsAsync<CredentialGrantException>(() => Hosts(fixture, session).StartAsync(proposal, "ip", default));
        Assert.Empty(await session.Identity.IntegrationCredentialPairings.ToListAsync());
    }
}
