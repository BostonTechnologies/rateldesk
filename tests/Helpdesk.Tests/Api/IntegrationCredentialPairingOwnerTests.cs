using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Api;

public sealed partial class IntegrationCredentialPairingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_pairing_requires_explicit_scope_consent_and_replays_the_same_persisted_grant(bool postgres)
    {
        await using var fixture = await Fixture.CreateProviderAsync(postgres);
        await using var session = fixture.Session();
        var started = await session.Service.StartAsync(new("Owner agent", "mcp", "https://desk.example/mcp"), "ip", default);
        var review = await session.Service.ReviewAsync(started.HumanCode, Owner, "ip", default);
        Assert.True(review!.OwnerAdministrationAvailable);
        Assert.Equal("ordinary", review.GrantKind);
        var approval = OwnerApproval(started.HumanCode);
        Assert.NotNull((await Assert.ThrowsAsync<CredentialGrantException>(() => session.Service.ApproveAsync(
            approval with { OwnerAdministrationConsent = false }, Owner, "ip", default))).Errors);
        Assert.Equal("pending", (await session.Identity.IntegrationCredentialPairings.SingleAsync()).State);
        Assert.True(await session.Service.ApproveAsync(approval, Owner, "ip", default));
        var first = await session.Service.PollAsync(started.DeviceCode, default);
        Assert.Equal("issued", first.Status);
        Assert.Equal(IntegrationCredentialGrantKinds.OwnerAdministration, first.Credential!.GrantKind);
        Assert.Equal("mcp", first.Credential.Purpose);
        Assert.Equal([HelpdeskPermissions.HelpdeskAdmin], first.Credential.Permissions);
        Assert.Equal(IntegrationCredentialGrantKinds.OwnerAdministrationPurpose, (await session.Identity.IntegrationCredentials.SingleAsync()).Purpose);
        Assert.Equal(IntegrationCredentialGrantKinds.OwnerAdministrationPurpose, (await session.Identity.IntegrationCredentialPairings.SingleAsync()).Purpose);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await using var restarted = fixture.Session();
        var replay = await restarted.Service.PollAsync(started.DeviceCode, default);
        Assert.True(first.Credential.Secret == replay.Credential!.Secret, "Delivery retry must preserve the same owner credential.");
        Assert.Equal(first.Credential.GrantKind, replay.Credential.GrantKind);
        Assert.True(await restarted.Service.AckAsync(started.DeviceCode, replay.Credential.Id, default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Owner_pairing_revalidates_current_administrator_before_every_delivery(bool postgres, bool issued)
    {
        await using var fixture = await Fixture.CreateProviderAsync(postgres);
        await using var session = fixture.Session();
        var started = await session.Service.StartAsync(new("Owner agent", "mcp", "https://desk.example/mcp")
        { GrantKind = IntegrationCredentialGrantKinds.OwnerAdministration }, "ip", default);
        Assert.True(await session.Service.ApproveAsync(OwnerApproval(started.HumanCode), Owner, "ip", default));
        if (issued)
        {
            Assert.Equal("issued", (await session.Service.PollAsync(started.DeviceCode, default)).Status);
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        (await session.Identity.Users.SingleAsync()).IsInstanceAdministrator = false;
        await session.Identity.SaveChangesAsync();
        var denied = await session.Service.PollAsync(started.DeviceCode, default);
        Assert.Equal("denied", denied.Status);
        Assert.Null(denied.Credential);
        if (issued) Assert.NotNull((await session.Identity.IntegrationCredentials.SingleAsync()).RevokedAtUtc);
        else Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Setup_verification_cannot_be_upgraded_to_owner_administration(bool postgres)
    {
        await using var fixture = await Fixture.CreateProviderAsync(postgres);
        await using var session = fixture.Session();
        var started = await session.Service.StartAsync(new("Temporary verifier", "mcp", "https://desk.example/mcp")
        { GrantKind = "setup-verification" }, "ip", default);
        var review = await session.Service.ReviewAsync(started.HumanCode, Owner, "ip", default);
        Assert.False(review!.OwnerAdministrationAvailable);
        Assert.NotNull((await Assert.ThrowsAsync<CredentialGrantException>(() => session.Service.ApproveAsync(
            OwnerApproval(started.HumanCode), Owner, "ip", default))).Errors);
        Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
        Assert.Equal("pending", (await session.Identity.IntegrationCredentialPairings.SingleAsync()).State);
    }

    private static IntegrationPairingApprovalRequest OwnerApproval(string code) =>
        new(code, "org-a", [HelpdeskPermissions.HelpdeskAdmin], 1, true)
        {
            GrantKind = IntegrationCredentialGrantKinds.OwnerAdministration,
            OwnerAdministrationConsent = true
        };
}
