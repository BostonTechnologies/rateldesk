using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Tests.Application.Services;

public sealed class SupportNotificationRecipientResolverTests
{
    [Fact]
    public async Task ResolveOrganizationEventRecipients_ReturnsSelfSupportedGroupMember()
    {
        var fixture = new ResolverFixture();
        await fixture.SeedRouteAsync("org-1", "org-1", "group-1", "user-1");

        var recipients = await fixture.Resolver.ResolveOrganizationEventRecipientsAsync(
            "org-1",
            SupportNotificationEventType.TicketCreatedUnassigned,
            SupportNotificationChannel.Email);

        Assert.Single(recipients);
        Assert.Equal("user-1", recipients[0].UserId);
    }

    [Fact]
    public async Task ResolveOrganizationEventRecipients_ReturnsMspSupportedGroupMember()
    {
        var fixture = new ResolverFixture();
        await fixture.SeedRouteAsync("customer-1", "provider-1", "group-1", "agent-1");

        var recipients = await fixture.Resolver.ResolveOrganizationEventRecipientsAsync(
            "customer-1",
            SupportNotificationEventType.TicketCreatedUnassigned,
            SupportNotificationChannel.Email);

        Assert.Single(recipients);
        Assert.Equal("agent-1", recipients[0].UserId);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("member")]
    [InlineData("subscription")]
    [InlineData("preference")]
    public async Task ResolveOrganizationEventRecipients_FiltersDisabledRows(string disabled)
    {
        var fixture = new ResolverFixture();
        await fixture.SeedRouteAsync("org-1", "org-1", "group-1", "user-1");
        if (disabled == "group")
        {
            (await fixture.SupportGroups.GetAsync("group-1"))!.IsEnabled = false;
        }
        if (disabled == "member")
        {
            (await fixture.SupportGroupMembers.GetAsync("member-user-1"))!.IsEnabled = false;
        }
        if (disabled == "subscription")
        {
            (await fixture.Subscriptions.GetAsync("sub-group-1"))!.IsEnabled = false;
        }
        if (disabled == "preference")
        {
            await fixture.Preferences.CreateAsync(new UserSupportNotificationPreference
            {
                Id = "pref-user-1",
                UserId = "user-1",
                EventType = SupportNotificationEventType.TicketCreatedUnassigned,
                Channel = SupportNotificationChannel.Email,
                IsEnabled = false
            });
        }

        var recipients = await fixture.Resolver.ResolveOrganizationEventRecipientsAsync(
            "org-1",
            SupportNotificationEventType.TicketCreatedUnassigned,
            SupportNotificationChannel.Email);

        Assert.Empty(recipients);
    }

    [Fact]
    public async Task ResolveOrganizationEventRecipients_NoCoverage_ReturnsEmpty()
    {
        var fixture = new ResolverFixture();
        await fixture.Users.CreateAsync(new User { Id = "user-1", Name = "Agent", Email = "agent@example.test" });

        var recipients = await fixture.Resolver.ResolveOrganizationEventRecipientsAsync(
            "org-1",
            SupportNotificationEventType.TicketCreatedUnassigned,
            SupportNotificationChannel.Email);

        Assert.Empty(recipients);
    }

    [Fact]
    public async Task ResolveOrganizationEventRecipients_InvalidSubscriptionRecipient_ReturnsEmpty()
    {
        var fixture = new ResolverFixture();
        await fixture.SeedRouteAsync("org-1", "org-1", "group-1", "user-1");
        (await fixture.Subscriptions.GetAsync("sub-group-1"))!.RecipientId = "missing-group";

        var recipients = await fixture.Resolver.ResolveOrganizationEventRecipientsAsync(
            "org-1",
            SupportNotificationEventType.TicketCreatedUnassigned,
            SupportNotificationChannel.Email);

        Assert.Empty(recipients);
    }

    [Fact]
    public async Task ResolveOrganizationEventRecipients_DeduplicatesByUserId()
    {
        var fixture = new ResolverFixture();
        await fixture.SeedRouteAsync("org-1", "org-1", "group-1", "user-1");
        await fixture.Subscriptions.CreateAsync(new SupportNotificationSubscription
        {
            Id = "sub-user-1",
            CustomerOrganizationId = "org-1",
            EventType = SupportNotificationEventType.TicketCreatedUnassigned,
            RecipientType = SupportNotificationRecipientType.User,
            RecipientId = "user-1",
            Channel = SupportNotificationChannel.Email
        });

        var recipients = await fixture.Resolver.ResolveOrganizationEventRecipientsAsync(
            "org-1",
            SupportNotificationEventType.TicketCreatedUnassigned,
            SupportNotificationChannel.Email);

        Assert.Single(recipients);
    }

    [Fact]
    public async Task ResolveAssignmentRecipients_ReturnsAssignedUserUnlessOptedOut()
    {
        var fixture = new ResolverFixture();
        await fixture.Users.CreateAsync(new User { Id = "user-1", Name = "Agent", Email = "agent@example.test" });

        var recipients = await fixture.Resolver.ResolveAssignmentRecipientsAsync(
            new Incident { Id = "ticket-1", OrganizationId = "org-1" },
            "user-1",
            SupportNotificationChannel.Email);

        Assert.Single(recipients);

        await fixture.Preferences.CreateAsync(new UserSupportNotificationPreference
        {
            Id = "pref-user-1",
            UserId = "user-1",
            EventType = SupportNotificationEventType.TicketAssigned,
            Channel = SupportNotificationChannel.Email,
            IsEnabled = false
        });

        recipients = await fixture.Resolver.ResolveAssignmentRecipientsAsync(
            new Incident { Id = "ticket-1", OrganizationId = "org-1" },
            "user-1",
            SupportNotificationChannel.Email);

        Assert.Empty(recipients);
    }

    [Fact]
    public async Task SupportAccess_RequiresCoveringGroupMembership()
    {
        await using var harness = await Helpdesk.Tests.Api.AssigneeSearchEndpointsTests.Harness.Create();
        await harness.WithDb(async db =>
        {
            var access = new Helpdesk.Infrastructure.Persistence.TicketSupportAccessService(db);
            Assert.True(await access.CanUserSupportOrganizationAsync("external", "org"));
            var membership = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.SupportGroupMembers);
            membership.IsEnabled = false;
            await db.SaveChangesAsync();
            Assert.False(await access.CanUserSupportOrganizationAsync("external", "org"));
        });
    }

    private sealed class ResolverFixture
    {
        public InMemoryRepository<User> Users { get; } = new();
        public InMemoryRepository<SupportGroup> SupportGroups { get; } = new();
        public InMemoryRepository<SupportGroupMember> SupportGroupMembers { get; } = new();
        public InMemoryRepository<OrganizationSupportCoverage> Coverages { get; } = new();
        public InMemoryRepository<SupportNotificationSubscription> Subscriptions { get; } = new();
        public InMemoryRepository<UserSupportNotificationPreference> Preferences { get; } = new();

        public SupportNotificationRecipientResolver Resolver => new(
            Users,
            SupportGroups,
            SupportGroupMembers,
            Coverages,
            Subscriptions,
            Preferences,
            NullLogger<SupportNotificationRecipientResolver>.Instance);

        public async Task SeedRouteAsync(string customerOrgId, string providerOrgId, string groupId, string userId)
        {
            if (await Users.GetAsync(userId) is null)
            {
                await Users.CreateAsync(new User { Id = userId, Name = userId, Email = $"{userId}@example.test", OrganizationId = providerOrgId });
            }

            await SupportGroups.CreateAsync(new SupportGroup
            {
                Id = groupId,
                OwningOrganizationId = providerOrgId,
                Name = "Support"
            });
            await SupportGroupMembers.CreateAsync(new SupportGroupMember
            {
                Id = $"member-{userId}",
                SupportGroupId = groupId,
                UserId = userId
            });
            await Coverages.CreateAsync(new OrganizationSupportCoverage
            {
                Id = $"coverage-{customerOrgId}",
                CustomerOrganizationId = customerOrgId,
                ProviderOrganizationId = providerOrgId,
                SupportGroupId = groupId
            });
            await Subscriptions.CreateAsync(new SupportNotificationSubscription
            {
                Id = $"sub-{groupId}",
                CustomerOrganizationId = customerOrgId,
                EventType = SupportNotificationEventType.TicketCreatedUnassigned,
                RecipientType = SupportNotificationRecipientType.SupportGroup,
                RecipientId = groupId,
                Channel = SupportNotificationChannel.Email
            });
        }
    }
}
