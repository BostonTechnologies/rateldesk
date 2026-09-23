using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Helpdesk.API.DependencyInjection;
using Helpdesk.Application.Events;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.Services.Tenants;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.DTOs.Attachment;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class MixedMailboxCoordinatorTests
{
    [Theory]
    [InlineData("EmailIngestion:PollTimeout", "00:02:00")]
    [InlineData("EmailIngestion:AcknowledgmentTimeout", "00:00:30")]
    [InlineData("EmailIngestion:AcknowledgmentPhaseBudget", "00:00:30")]
    public void Phase_timeout_must_fit_inside_its_distributed_claim(string key, string value)
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => new MailboxIngestionCoordinator(
            Substitute.For<IServiceScopeFactory>(), settings, NullLogger<MailboxIngestionCoordinator>.Instance));
    }

    [Fact]
    public async Task Sync_now_runs_only_selected_mailbox_before_its_next_scheduled_poll()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        var initial = await fixture.Coordinator.ReconcileAsync(default);
        await Task.WhenAll(initial.Values).WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Coordinator.ReconcileAsync(default);
        var before = fixture.Mailboxes.ToDictionary(x => x.Id, x => fixture.FetchCount(x.Id));
        var selected = fixture.Mailboxes[2];
        using (var scope = fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MailboxSyncService>();
            var queued = await service.RequestAsync(selected.Id, default);
            Assert.Equal("Queued", queued.Status);
            Assert.Equal(queued.RequestVersion, (await service.RequestAsync(selected.Id, default)).RequestVersion);
        }

        var commanded = await fixture.Coordinator.ReconcileAsync(default);
        Assert.Single(commanded);
        await Task.WhenAll(commanded.Values).WaitAsync(TimeSpan.FromSeconds(10));
        await using var verify = fixture.Open();
        var state = await verify.Set<MailboxIngestionState>().SingleAsync(x => x.MailboxId == selected.Id);
        Assert.Equal(1, state.SyncRequestedVersion);
        Assert.Equal(state.SyncRequestedVersion, state.SyncCompletedVersion);
        Assert.Null(state.LastSyncCommandErrorCode);
        Assert.NotNull(state.LastSyncCommandUnixMilliseconds);
        Assert.Equal(before[selected.Id] + 1, fixture.FetchCount(selected.Id));
        Assert.All(fixture.Mailboxes.Where(x => x.Id != selected.Id), x =>
            Assert.Equal(before[x.Id], fixture.FetchCount(x.Id)));
    }

    [Fact]
    public async Task Durable_pending_receipt_processes_when_fresh_enumeration_fails()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        await fixture.SeedReceiptAsync(fixture.Global, "durable-pending", InboundReceiptOutcome.Pending,
            "requester@tenant0.example.com");
        fixture.Adapters[InboundMailboxProvider.Graph].FetchFailureMailbox = fixture.Global.Id;

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        Assert.Single(await verify.Incidents.ToListAsync());
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(InboundReceiptOutcome.Succeeded, receipt.Outcome);
        Assert.Equal(1, receipt.Attempts);
        Assert.Equal(nameof(IOException), (await verify.Set<MailboxIngestionState>().SingleAsync(x => x.MailboxId == fixture.Global.Id)).ErrorCode);
    }

    [Fact]
    public async Task Selected_empty_envelope_baseline_receipt_refetches_and_processes_without_replaying_other_receipts()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        await fixture.SeedReceiptAsync(fixture.Global, "selected-old", InboundReceiptOutcome.Ignored, envelope: false);
        await fixture.SeedReceiptAsync(fixture.Global, "other-old", InboundReceiptOutcome.Ignored, envelope: false);
        await fixture.SeedReceiptAsync(fixture.Global, "already-handled", InboundReceiptOutcome.Succeeded, envelope: false);
        fixture.AddMessage(fixture.Global, "selected-old", "requester@tenant0.example.com");
        await using (var setup = fixture.Open())
        {
            var rows = await setup.Set<InboundMessageReceipt>().ToListAsync();
            foreach (var receipt in rows.Where(x => x.Outcome == InboundReceiptOutcome.Ignored))
            {
                receipt.Reason = "InitialBaselineSkipped";
                receipt.Acknowledged = true;
            }
            rows.Single(x => x.TransportKey == "selected-old").HistoricalImportRequestId = Guid.NewGuid();
            await setup.SaveChangesAsync();
        }

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        Assert.Single(await verify.Incidents.ToListAsync());
        var receipts = await verify.Set<InboundMessageReceipt>().ToListAsync();
        Assert.Equal(InboundReceiptOutcome.Succeeded, receipts.Single(x => x.TransportKey == "selected-old").Outcome);
        Assert.NotNull(receipts.Single(x => x.TransportKey == "selected-old").HistoricalImportCompletedUnixMilliseconds);
        Assert.Equal(InboundReceiptOutcome.Ignored, receipts.Single(x => x.TransportKey == "other-old").Outcome);
        Assert.Equal(InboundReceiptOutcome.Succeeded, receipts.Single(x => x.TransportKey == "already-handled").Outcome);
    }

    [Fact]
    public async Task Missing_historical_source_is_held_for_review_without_creating_an_incident()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        await fixture.SeedReceiptAsync(fixture.Global, "gone-old", InboundReceiptOutcome.Ignored, envelope: false);
        await using (var setup = fixture.Open())
        {
            var receipt = await setup.Set<InboundMessageReceipt>().SingleAsync();
            receipt.Reason = "InitialBaselineSkipped";
            receipt.Acknowledged = true;
            receipt.HistoricalImportRequestId = Guid.NewGuid();
            await setup.SaveChangesAsync();
        }

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        Assert.Empty(await verify.Incidents.ToListAsync());
        var held = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(InboundReceiptOutcome.NeedsReview, held.Outcome);
        Assert.Equal("ImportSourceMissing", held.Reason);
    }

    [Fact]
    public async Task Durable_acknowledgment_recovers_when_fresh_enumeration_fails()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        await fixture.SeedReceiptAsync(fixture.Global, "durable-ack", InboundReceiptOutcome.Succeeded, envelope: false);
        var adapter = fixture.Adapters[InboundMailboxProvider.Graph];
        adapter.FetchFailureMailbox = fixture.Global.Id;

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        Assert.True((await verify.Set<InboundMessageReceipt>().SingleAsync()).Acknowledged);
        Assert.Equal(1, adapter.Acknowledgements.GetValueOrDefault(fixture.Global.Id));
        Assert.Equal(nameof(IOException), (await verify.Set<MailboxIngestionState>().SingleAsync(x => x.MailboxId == fixture.Global.Id)).ErrorCode);
    }

    [Fact]
    public async Task Blocked_remote_acknowledgment_does_not_hold_sqlite_write_lock_and_archive_can_complete()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        await fixture.SeedReceiptAsync(fixture.Global, "blocked-ack", InboundReceiptOutcome.Succeeded, envelope: false);
        var adapter = fixture.Adapters[InboundMailboxProvider.Graph];
        adapter.BlockedAcknowledgmentMailbox = fixture.Global.Id;

        var poll = fixture.Coordinator.PollAsync(fixture.Global, default);
        await adapter.AcknowledgmentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var unrelated = fixture.Open())
        {
            unrelated.Organizations.Add(new Organization { Id = "unrelated-write", Name = "Unrelated write" });
            await unrelated.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        await using (var archive = fixture.Open())
            await archive.EmailInboxSettings.Where(x => x.Id == fixture.Global.Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Archived, true).SetProperty(x => x.Version, x => x.Version + 1));
        adapter.ReleaseAcknowledgment();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.True(receipt.Acknowledged);
        Assert.Equal(InboundAcknowledgmentStatus.Succeeded, receipt.AcknowledgmentStatus);
        Assert.Equal(1, receipt.AcknowledgmentAttempts);
    }

    [Fact]
    public async Task Failed_acknowledgment_does_not_starve_later_receipt()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        await fixture.SeedReceiptAsync(fixture.Global, "first-fails", InboundReceiptOutcome.Succeeded, envelope: false);
        await fixture.SeedReceiptAsync(fixture.Global, "second-succeeds", InboundReceiptOutcome.Succeeded, envelope: false);
        var adapter = fixture.Adapters[InboundMailboxProvider.Graph];
        adapter.FailingAcknowledgmentKeys.Add("first-fails");

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        var first = await verify.Set<InboundMessageReceipt>().SingleAsync(x => x.TransportKey == "first-fails");
        var second = await verify.Set<InboundMessageReceipt>().SingleAsync(x => x.TransportKey == "second-succeeds");
        Assert.False(first.Acknowledged);
        Assert.Equal(nameof(IOException), first.AcknowledgmentErrorCode);
        Assert.True(second.Acknowledged);
        Assert.Equal(2, adapter.Acknowledgements.GetValueOrDefault(fixture.Global.Id));
    }

    [Fact]
    public async Task Slow_acknowledgment_backlog_does_not_starve_fresh_capture_or_unrelated_writes()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false,
            ingestionSettings: new Dictionary<string, string?>
            {
                ["EmailIngestion:PollTimeout"] = "00:00:15",
                ["EmailIngestion:AcknowledgmentTimeout"] = "00:00:00.200",
                ["EmailIngestion:AcknowledgmentPhaseBudget"] = "00:00:02"
            });
        var adapter = fixture.Adapters[InboundMailboxProvider.Graph];
        for (var i = 0; i < 8; i++)
        {
            var key = $"slow-ack-{i}";
            await fixture.SeedReceiptAsync(fixture.Global, key, InboundReceiptOutcome.Succeeded, envelope: false);
            adapter.SlowAcknowledgmentKeys.Add(key);
        }
        fixture.AddMessage(fixture.Global, "fresh-during-slow-acks-1", "requester@tenant0.example.com");

        var firstPoll = fixture.Coordinator.PollAsync(fixture.Global, default);
        await adapter.AcknowledgmentEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var first = fixture.Open())
        {
            Assert.Single(await first.Incidents.ToListAsync());
            first.Organizations.Add(new Organization { Id = "write-during-backlog", Name = "Independent write" });
            await first.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        await firstPoll.WaitAsync(TimeSpan.FromSeconds(15));
        await using (var first = fixture.Open())
            Assert.Contains(await first.Set<InboundMessageReceipt>().ToListAsync(), receipt =>
                receipt.AcknowledgmentErrorCode == "AcknowledgmentTimeout");

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        fixture.AddMessage(fixture.Global, "fresh-during-slow-acks-2", "second@tenant0.example.com");
        await fixture.Coordinator.PollAsync(fixture.Global, default).WaitAsync(TimeSpan.FromSeconds(15));

        await using var verify = fixture.Open();
        Assert.Equal(2, await verify.Incidents.CountAsync());
        Assert.Equal(2, await verify.Set<InboundMessageReceipt>().CountAsync(x => x.TicketId != null));
        Assert.True(fixture.FetchCount(fixture.Global.Id) >= 2);
    }

    [Fact]
    public async Task Disposition_change_before_claim_requires_review_without_redirecting_acknowledgment()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        await fixture.SeedReceiptAsync(fixture.Global, "stale-target", InboundReceiptOutcome.Succeeded, envelope: false);
        await using (var setup = fixture.Open())
            await setup.Set<InboundMessageReceipt>().ExecuteUpdateAsync(update => update
                .SetProperty(x => x.AcknowledgmentTargetFingerprint, "captured-for-another-target"));

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.False(receipt.Acknowledged);
        Assert.Equal(InboundAcknowledgmentStatus.NeedsReview, receipt.AcknowledgmentStatus);
        Assert.Equal("AcknowledgmentConfigurationChanged", receipt.AcknowledgmentErrorCode);
        Assert.Equal(0, fixture.Adapters[InboundMailboxProvider.Graph].Acknowledgements.GetValueOrDefault(fixture.Global.Id));
    }

    [Fact]
    public async Task Missing_source_after_uncertain_acknowledgment_is_terminal_and_visible()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, seedMessages: false);
        await fixture.SeedReceiptAsync(fixture.Global, "already-moved", InboundReceiptOutcome.Succeeded, envelope: false);
        fixture.Adapters[InboundMailboxProvider.Graph].MissingAcknowledgmentKeys.Add("already-moved");

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.True(receipt.Acknowledged);
        Assert.Equal(InboundAcknowledgmentStatus.NotRequired, receipt.AcknowledgmentStatus);
        Assert.Equal("SourceMissingDuringAcknowledgment", receipt.AcknowledgmentErrorCode);
        Assert.Equal("SourceMissingDuringAcknowledgment", receipt.Reason);
    }

    [Theory]
    [InlineData(false, false, InboundAcknowledgmentStatus.Pending)]
    [InlineData(true, true, InboundAcknowledgmentStatus.NotRequired)]
    public async Task Graph_move_not_found_is_terminal_only_when_source_probe_is_missing(
        bool sourceMissing, bool acknowledged, InboundAcknowledgmentStatus expectedStatus)
    {
        using var transport = new MoveNotFoundGraphTransport(sourceMissing);
        var adapter = new GraphMailboxAdapter(new MailboxCredentialProtector(new EphemeralDataProtectionProvider()),
            _ => new GraphServiceClient(new HttpClient(transport, disposeHandler: false), new AnonymousAuthenticationProvider()));
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, adapter, seedMessages: false);
        fixture.Global.MarkReadAfterSuccess = true;
        fixture.Global.ProcessedFolder = "unavailable-destination";
        await using (var setup = fixture.Open())
            await setup.EmailInboxSettings.Where(x => x.Id == fixture.Global.Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.MarkReadAfterSuccess, true)
                .SetProperty(x => x.ProcessedFolder, "unavailable-destination"));
        await fixture.SeedReceiptAsync(fixture.Global, "source-id", InboundReceiptOutcome.Succeeded, envelope: false);

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(acknowledged, receipt.Acknowledged);
        Assert.Equal(expectedStatus, receipt.AcknowledgmentStatus);
        Assert.Equal(sourceMissing ? "SourceMissingDuringAcknowledgment" : nameof(Microsoft.Graph.Models.ODataErrors.ODataError),
            receipt.AcknowledgmentErrorCode);
        Assert.True(transport.SourceProbeAttempted);
    }

    [Theory]
    [InlineData(true, "tenant-0", false, InboundReceiptOutcome.NeedsReview)]
    [InlineData(false, "tenant-0", false, InboundReceiptOutcome.Succeeded)]
    [InlineData(true, "tenant-1", false, InboundReceiptOutcome.Succeeded)]
    [InlineData(true, "tenant-0", true, InboundReceiptOutcome.Succeeded)]
    public async Task Forwarded_requester_is_used_only_by_an_applicable_authorized_rule(
        bool enabledRule, string forwarderTenant, bool instanceAdmin, InboundReceiptOutcome expectedOutcome)
    {
        var parser = Substitute.For<IForwardedEmailParser>();
        parser.Parse(Arg.Any<string>(), Arg.Any<string>()).Returns(new ForwardedEmailParseResult(
            ForwardedEmailParseStatus.Parsed, "customer@tenant1.example.com", "Customer B", null,
            DateTimeOffset.UtcNow, "No ticket reference", "<p>Original request</p>", "Original request", 0.99));
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph,
            parser: parser, realRuleExecutor: true, seedMessages: false);
        await using (var setup = fixture.Open())
        {
            setup.Users.Add(new User
            {
                Id = "forwarder", Email = "tech@support.local", Name = "Forwarder",
                Role = instanceAdmin ? "HelpdeskAdmin" : "Technician", OrganizationId = forwarderTenant
            });
            setup.Customers.AddRange(
                new Customer { Id = "forwarder-contact", Email = "forwarder-contact@support.local", OrganizationId = forwarderTenant },
                new Customer { Id = "customer-b", Email = "customer@tenant1.example.com", Name = "Customer B", OrganizationId = "tenant-1" });
            setup.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = "forwarder-contact", DomainUserId = "forwarder",
                OidcIssuer = "https://issuer.example", OidcSubject = "forwarder"
            });
            setup.InboundEmailRules.Add(new InboundEmailRule
            {
                Id = "forward-rule", Name = "Forwarded request", Enabled = enabledRule,
                ScopeType = InboundEmailRuleScopeType.Tenant, TenantId = "tenant-1", StopProcessing = true,
                ConditionsJson = JsonSerializer.Serialize(new[]
                {
                    new InboundEmailRuleConditionConfig(InboundEmailRuleConditionType.SenderIsInternalSupportUser),
                    new InboundEmailRuleConditionConfig(InboundEmailRuleConditionType.IsForwardedEmail),
                    new InboundEmailRuleConditionConfig(InboundEmailRuleConditionType.OriginalForwardedSenderExists)
                }),
                ActionsJson = JsonSerializer.Serialize(new[]
                {
                    new InboundEmailRuleActionConfig(InboundEmailRuleActionType.CreateIncidentForOriginalForwardedSender)
                })
            });
            await setup.SaveChangesAsync();
        }
        fixture.AddMessage(fixture.Global, "forward-without-reference", "tech@support.local");

        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(expectedOutcome, receipt.Outcome);
        var incidents = await verify.Incidents.ToListAsync();
        if (expectedOutcome == InboundReceiptOutcome.NeedsReview)
        {
            Assert.Empty(incidents);
            Assert.Contains("Unauthorized", receipt.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await verify.Set<MailboxOutboxEffect>().ToListAsync());
        }
        else
        {
            var incident = Assert.Single(incidents);
            if (enabledRule)
            {
                Assert.Equal("tenant-1", incident.OrganizationId);
                Assert.Equal("customer@tenant1.example.com", incident.RequesterEmail);
                Assert.Equal(TicketState.New, incident.State);
                Assert.Null(incident.AssignedToId);
                Assert.Empty(await verify.Set<MailboxOutboxEffect>().ToListAsync());
            }
            else
            {
                Assert.Equal("tech@support.local", incident.RequesterEmail);
                Assert.NotEqual("tenant-1", incident.OrganizationId);
            }
        }
    }

    [Theory]
    [InlineData(false, false, false, InboundReceiptOutcome.Succeeded)]
    [InlineData(false, true, false, InboundReceiptOutcome.Succeeded)]
    [InlineData(true, false, false, InboundReceiptOutcome.Succeeded)]
    [InlineData(false, false, true, InboundReceiptOutcome.NeedsReview)]
    public async Task Authorized_forwarding_uses_candidate_route_when_outer_contact_has_an_incompatible_route(
        bool globalWithOuterTenantDedicated, bool instanceAdmin, bool candidateUsesWrongSource,
        InboundReceiptOutcome expectedOutcome)
    {
        var parser = Substitute.For<IForwardedEmailParser>();
        parser.Parse(Arg.Any<string>(), Arg.Any<string>()).Returns(new ForwardedEmailParseResult(
            ForwardedEmailParseStatus.Parsed, "customer@tenant1.example.com", "Customer B", null,
            DateTimeOffset.UtcNow, "No ticket reference", "<p>Original request</p>", "Original request", 0.99));
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph,
            parser: parser, realRuleExecutor: true, seedMessages: false);
        var source = fixture.Global;
        await using (var setup = fixture.Open())
        {
            if (candidateUsesWrongSource)
            {
                setup.EmailInboxSettings.Add(MixedHarness.NewMailbox(InboundMailboxProvider.Imap, "tenant-1"));
            }
            else if (globalWithOuterTenantDedicated)
            {
                setup.EmailInboxSettings.Add(MixedHarness.NewMailbox(InboundMailboxProvider.Imap, "tenant-0"));
            }
            else
            {
                source.OrganizationId = "tenant-1";
                source.Scope = MailboxScope.Organization;
                await setup.EmailInboxSettings.Where(x => x.Id == source.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.OrganizationId, "tenant-1")
                    .SetProperty(x => x.Scope, MailboxScope.Organization));
            }
            setup.Users.Add(new User
            {
                Id = "same-email-forwarder", Email = "tech@support.local", Name = "Forwarder",
                Role = instanceAdmin ? "HelpdeskAdmin" : "Technician",
                OrganizationId = instanceAdmin ? "tenant-0" : "tenant-1"
            });
            setup.Customers.AddRange(
                new Customer { Id = "same-email-contact", Email = "tech@support.local", Name = "Forwarder contact", OrganizationId = "tenant-0" },
                new Customer { Id = "candidate-b", Email = "customer@tenant1.example.com", Name = "Customer B", OrganizationId = "tenant-1" });
            setup.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = "same-email-contact", DomainUserId = "same-email-forwarder",
                OidcIssuer = "https://issuer.example", OidcSubject = "same-email-forwarder"
            });
            if (!instanceAdmin)
                setup.ScopedRoleAssignments.Add(new ScopedRoleAssignment
                {
                    UserId = "same-email-forwarder", OrganizationId = "tenant-1",
                    RoleKey = ScopedRoleCatalog.Technician
                });
            setup.InboundEmailRules.Add(new InboundEmailRule
            {
                Id = $"candidate-route-{globalWithOuterTenantDedicated}-{instanceAdmin}",
                Name = "Forward candidate", Enabled = true, ScopeType = InboundEmailRuleScopeType.Tenant,
                TenantId = "tenant-1", StopProcessing = true,
                ConditionsJson = JsonSerializer.Serialize(new[]
                {
                    new InboundEmailRuleConditionConfig(InboundEmailRuleConditionType.SenderIsInternalSupportUser),
                    new InboundEmailRuleConditionConfig(InboundEmailRuleConditionType.IsForwardedEmail),
                    new InboundEmailRuleConditionConfig(InboundEmailRuleConditionType.OriginalForwardedSenderExists)
                }),
                ActionsJson = JsonSerializer.Serialize(new[]
                {
                    new InboundEmailRuleActionConfig(InboundEmailRuleActionType.CreateIncidentForOriginalForwardedSender)
                })
            });
            await setup.SaveChangesAsync();
        }
        fixture.AddMessage(source, "candidate-route-message", "tech@support.local");

        await fixture.Coordinator.PollAsync(source, default);

        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(expectedOutcome, receipt.Outcome);
        var incidents = await verify.Incidents.ToListAsync();
        if (expectedOutcome == InboundReceiptOutcome.NeedsReview)
        {
            Assert.Empty(incidents);
            Assert.Equal("TenantUsesDedicatedMailbox", receipt.Reason);
            Assert.Empty(await verify.Set<MailboxOutboxEffect>().ToListAsync());
            Assert.Equal("tenant-0", (await verify.Customers.SingleAsync(x => x.Id == "same-email-contact")).OrganizationId);
            Assert.Equal("tenant-1", (await verify.Customers.SingleAsync(x => x.Id == "candidate-b")).OrganizationId);
            return;
        }
        var incident = Assert.Single(incidents);
        Assert.Equal("tenant-1", incident.OrganizationId);
        Assert.Equal("customer@tenant1.example.com", incident.RequesterEmail);
        Assert.Equal(TicketState.New, incident.State);
        Assert.Null(incident.AssignedToId);
        Assert.Empty(await verify.Set<MailboxOutboxEffect>().ToListAsync());
        Assert.Equal("tenant-0", (await verify.Customers.SingleAsync(x => x.Id == "same-email-contact")).OrganizationId);
        Assert.Equal("tenant-1", (await verify.Customers.SingleAsync(x => x.Id == "candidate-b")).OrganizationId);
    }

    [Fact]
    public async Task Real_Graph_MIME_creates_ticket_before_ack_and_failed_ack_recovers_without_duplicate()
    {
        using var transport = new CommitCheckingGraphTransport();
        var secrets = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
        var adapter = new GraphMailboxAdapter(secrets,
            _ => new GraphServiceClient(new HttpClient(transport, disposeHandler: false), new AnonymousAuthenticationProvider()));
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, adapter);
        fixture.Global.InitialImport = InitialMailImport.All;
        await using (var setup = fixture.Open())
            await setup.EmailInboxSettings.Where(x => x.Id == fixture.Global.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.InitialImport, InitialMailImport.All));
        transport.CheckCommitted = async () =>
        {
            await using var db = fixture.Open();
            Assert.Single(await db.Incidents.ToListAsync());
            Assert.Single(await db.Customers.ToListAsync());
            var receipt = await db.Set<InboundMessageReceipt>().SingleAsync();
            Assert.Equal(InboundReceiptOutcome.Succeeded, receipt.Outcome);
            Assert.False(receipt.Acknowledged);
        };
        await fixture.Coordinator.PollAsync(fixture.Global, default);
        string ticketId;
        await using (var verify = fixture.Open())
        {
            var incident = Assert.Single(await verify.Incidents.ToListAsync());
            ticketId = incident.Id;
            Assert.Equal("tenant-0", incident.OrganizationId);
            Assert.Equal("Fixture", incident.Title);
            Assert.Contains("Hello", incident.OriginalEmailText);
            Assert.Equal("requester@tenant0.example.com", Assert.Single(await verify.Customers.ToListAsync()).Email);
            var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
            Assert.Equal("AbC-Case", receipt.TransportKey);
            Assert.Equal(ticketId, receipt.TicketId);
            Assert.False(receipt.Acknowledged);
            Assert.Equal(InboundReceiptOutcome.Succeeded, receipt.Outcome);
        }
        Assert.Equal(1, transport.Acknowledgements);
        Assert.Equal(1, transport.CommittedChecks);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await fixture.Coordinator.PollAsync(fixture.Global, default);
        await using var final = fixture.Open();
        Assert.Equal(ticketId, Assert.Single(await final.Incidents.ToListAsync()).Id);
        Assert.Single(await final.Customers.ToListAsync());
        var recovered = await final.Set<InboundMessageReceipt>().SingleAsync();
        Assert.True(recovered.Acknowledged);
        Assert.Equal(1, recovered.Attempts);
        Assert.Equal(1, transport.MimeFetches);
        Assert.Equal(2, transport.DeltaFetches);
        Assert.Equal(2, transport.Acknowledgements);
        Assert.Equal(2, transport.CommittedChecks);
    }

    [Fact]
    public async Task Graph_missing_item_and_valid_neighbor_commit_tombstone_checkpoint_and_one_ticket()
    {
        using var transport = new MissingNeighborGraphTransport();
        var secrets = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
        var adapter = new GraphMailboxAdapter(secrets,
            _ => new GraphServiceClient(new HttpClient(transport, disposeHandler: false), new AnonymousAuthenticationProvider()));
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph, adapter, seedMessages: false);
        fixture.Global.InitialImport = InitialMailImport.All;
        await using (var setup = fixture.Open())
            await setup.EmailInboxSettings.Where(x => x.Id == fixture.Global.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.InitialImport, InitialMailImport.All));

        await fixture.Coordinator.PollAsync(fixture.Global, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await fixture.Coordinator.PollAsync(fixture.Global, default);

        await using var verify = fixture.Open();
        Assert.Single(await verify.Incidents.ToListAsync());
        var receipts = await verify.Set<InboundMessageReceipt>().OrderBy(x => x.TransportKey).ToListAsync();
        Assert.Equal(2, receipts.Count);
        var missing = Assert.Single(receipts, x => x.TransportKey == "missing-id");
        Assert.Equal(InboundReceiptOutcome.Ignored, missing.Outcome);
        Assert.Equal("SourceMessageMissing", missing.Reason);
        Assert.True(missing.Acknowledged);
        Assert.Equal(InboundReceiptOutcome.Succeeded, Assert.Single(receipts, x => x.TransportKey == "valid-id").Outcome);
        Assert.Equal(1, transport.ValidMimeFetches);
        Assert.Equal("https://graph.microsoft.com/v1.0/delta?cursor=complete",
            (await verify.Set<MailboxIngestionState>().SingleAsync(x => x.MailboxId == fixture.Global.Id)).Cursor);
    }

    [Theory]
    [InlineData(InboundMailboxProvider.Imap)]
    [InlineData(InboundMailboxProvider.Pop3)]
    public async Task Real_TLS_protocol_MIME_persists_tenant_ticket_before_disposition(InboundMailboxProvider provider)
    {
        var server = new ProtocolMailboxAdapterTests.PopFixture(imap: provider == InboundMailboxProvider.Imap);
        var disposed = false;
        try
        {
            var secrets = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["EmailIngestion:AllowedPrivateHosts:0"] = "localhost" }).Build();
            var real = new ProtocolMailboxAdapter(provider, new MailboxDestinationPolicy(config), secrets);
            IInboundMailboxAdapter adapter = provider == InboundMailboxProvider.Imap
                ? new BeforeAcknowledgementAdapter(real, async () =>
                {
                    // The IMAP test server accepts one connection. Shut it down after
                    // fetch so the real disposition connection fails after business commit.
                    await server.Completion;
                    await server.DisposeAsync();
                    disposed = true;
                }) : real;
            await using var fixture = await MixedHarness.CreateAsync(provider, adapter);
            var mailbox = fixture.Global;
            mailbox.Authentication = MailboxAuthentication.Password;
            mailbox.MailHost = "localhost"; mailbox.Port = server.Port; mailbox.Username = "fixture";
            mailbox.MailboxFolder = "Support"; mailbox.InitialImport = InitialMailImport.All;
            mailbox.MarkReadAfterSuccess = provider == InboundMailboxProvider.Imap;
            mailbox.Password = secrets.Protect(mailbox.Id, "synthetic password");
            await using (var setup = fixture.Open())
            {
                setup.Entry(await setup.EmailInboxSettings.SingleAsync(x => x.Id == mailbox.Id)).CurrentValues.SetValues(mailbox);
                (await setup.Organizations.SingleAsync(x => x.Id == "tenant-0")).DnsName = "example.test";
                await setup.SaveChangesAsync();
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await fixture.Coordinator.PollAsync(mailbox, deadline.Token);
            await using var verify = fixture.Open();
            var incident = Assert.Single(await verify.Incidents.ToListAsync());
            Assert.Equal("tenant-0", incident.OrganizationId);
            Assert.Contains("hello", incident.OriginalEmailText);
            Assert.Equal("requester@example.test", Assert.Single(await verify.Customers.ToListAsync()).Email);
            var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
            Assert.Equal(InboundReceiptOutcome.Succeeded, receipt.Outcome);
            Assert.Equal(incident.Id, receipt.TicketId);
            Assert.Equal(provider == InboundMailboxProvider.Imap ? "7:42" : "stable-UIDL", receipt.TransportKey);
            Assert.Equal(provider == InboundMailboxProvider.Pop3, receipt.Acknowledged);
            Assert.DoesNotContain(server.Commands, command => command.StartsWith("DELE", StringComparison.Ordinal));
        }
        finally { if (!disposed) await server.DisposeAsync(); }
    }

    [Fact]
    public async Task Transaction_retry_preserves_distinct_incident_ids_and_one_eml_attachment_file()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Imap);
        var receiptId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), $"ingress-attachment-retry-{Guid.NewGuid():N}");
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(root);
        var fileStore = new TicketAttachmentFileStore(environment, Options.Create(new StorageOptions { RootPath = root }));
        string[]? firstIds = null;
        string? firstPath = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var scope = fixture.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                db.BeginIngressRoutingScope();
                db.RestrictIngressToOrganization("tenant-0");
                await using var transaction = await db.Database.BeginTransactionAsync();
                var effects = scope.ServiceProvider.GetRequiredService<IIngressEffectContext>();
                using var active = effects.Begin(receiptId);
                var sender = scope.ServiceProvider.GetRequiredService<IRequestSender>();
                var ids = new List<string>();
                for (var index = 0; index < 2; index++)
                {
                    var incident = await sender.Send(new CreateIncidentCommand($"Request {index}", "Synthetic", null,
                        null, "tenant-0", null, null, null, null, "requester@tenant0.example.com", [], null));
                    ids.Add(incident.Id);
                }
                Assert.NotEqual(ids[0], ids[1]);
                var eml = Encoding.UTF8.GetBytes("From: nested@example.test\r\nSubject: Nested retained\r\n\r\nNested body and attachment content");
                await new TicketAttachmentService(db, fileStore, effects).SaveAsync(ids[0],
                    [new AttachmentUpload("request.eml", "message/rfc822", eml)], null, default);
                var attachment = await db.Attachments.SingleAsync();
                var filePath = fileStore.GetReadPath(attachment.FilePath)!;
                Assert.Equal(eml, await File.ReadAllBytesAsync(filePath));
                Assert.Equal("request.eml", attachment.FileName);
                Assert.Equal("message/rfc822", attachment.ContentType);
                Assert.Single(Directory.GetFiles(Path.Combine(root, "attachments")));
                if (attempt == 0)
                {
                    firstIds = ids.ToArray(); firstPath = filePath;
                    await transaction.RollbackAsync();
                    await using var verifyRollback = fixture.Open();
                    Assert.Empty(await verifyRollback.Incidents.ToListAsync());
                    Assert.Empty(await verifyRollback.Attachments.ToListAsync());
                }
                else
                {
                    Assert.Equal(firstIds, ids.ToArray());
                    Assert.Equal(firstPath, filePath);
                    await transaction.CommitAsync();
                }
            }
            await using var verify = fixture.Open();
            Assert.Equal(2, await verify.Incidents.CountAsync());
            Assert.Single(await verify.Attachments.ToListAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(InboundMailboxProvider.Graph)]
    [InlineData(InboundMailboxProvider.Imap)]
    [InlineData(InboundMailboxProvider.Pop3)]
    public async Task Thirteen_tenants_use_four_sources_and_paused_dedicated_never_falls_back(InboundMailboxProvider globalProvider)
    {
        await using var fixture = await MixedHarness.CreateAsync(globalProvider);
        var first = await fixture.Coordinator.ReconcileAsync(default);
        Assert.Equal(4, first.Count);
        await Task.WhenAll(first.Values).WaitAsync(TimeSpan.FromSeconds(30));
        await using (var verify = fixture.Open())
        {
            var receipts = await verify.Set<InboundMessageReceipt>().ToListAsync();
            Assert.Equal(13, receipts.Count);
            Assert.All(receipts, receipt => Assert.Equal(InboundReceiptOutcome.Succeeded, receipt.Outcome));
            var incidents = await verify.Incidents.ToListAsync();
            Assert.Equal(13, incidents.Count);
            Assert.Equal(13, incidents.Select(x => x.OrganizationId).Distinct().Count());
            Assert.Equal(13, await verify.Organizations.CountAsync());
            Assert.All(fixture.Mailboxes, mailbox => Assert.Equal(1, fixture.FetchCount(mailbox.Id)));
            Assert.Equal(10, receipts.Count(x => x.MailboxId == fixture.Global.Id));
            Assert.Equal(13, await verify.Customers.CountAsync());
        }

        var paused = fixture.Mailboxes.Single(x => x.OrganizationId == "tenant-11");
        await using (var update = fixture.Open())
            await update.EmailInboxSettings.Where(x => x.Id == paused.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Enabled, false).SetProperty(x => x.Version, x => x.Version + 1));
        fixture.AddMessage(fixture.Global, "wrong-ingress", "requester@external11.example.net");
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        var second = await fixture.Coordinator.ReconcileAsync(default);
        Assert.Equal(3, second.Count);
        await Task.WhenAll(second.Values).WaitAsync(TimeSpan.FromSeconds(30));
        await using var final = fixture.Open();
        Assert.Equal(13, await final.Incidents.CountAsync());
        var held = await final.Set<InboundMessageReceipt>().SingleAsync(x => x.TransportKey == "wrong-ingress");
        Assert.Equal(InboundReceiptOutcome.NeedsReview, held.Outcome);
        Assert.Equal("TenantUsesDedicatedMailbox", held.Reason);
        Assert.Equal("tenant-11", held.OrganizationId);
        Assert.Equal(1, fixture.FetchCount(paused.Id));
        Assert.Equal(2, fixture.FetchCount(fixture.Global.Id));
    }

    [Fact]
    public async Task Blocked_source_does_not_delay_healthy_next_poll_and_version_change_cancels_old_runner()
    {
        await using var fixture = await MixedHarness.CreateAsync(InboundMailboxProvider.Graph);
        var blocked = fixture.Mailboxes.Single(x => x.OrganizationId == "tenant-10");
        var control = fixture.Adapters[blocked.Provider];
        control.BlockedMailbox = blocked.Id;
        var first = await fixture.Coordinator.ReconcileAsync(default);
        await control.BlockedEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(first.Where(x => x.Key != blocked.Id).Select(x => x.Value)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(first[blocked.Id].IsCompleted);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        var second = await fixture.Coordinator.ReconcileAsync(default);
        await Task.WhenAll(second.Where(x => x.Key != blocked.Id).Select(x => x.Value)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, fixture.FetchCount(fixture.Global.Id));
        Assert.Equal(1, fixture.FetchCount(blocked.Id));

        await using (var update = fixture.Open())
            await update.EmailInboxSettings.Where(x => x.Id == blocked.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Enabled, false).SetProperty(x => x.Version, x => x.Version + 1));
        await fixture.Coordinator.ReconcileAsync(default);
        await control.BlockedCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await first[blocked.Id].WaitAsync(TimeSpan.FromSeconds(10));
        await using var verify = fixture.Open();
        Assert.Empty(await verify.Set<InboundMessageReceipt>().Where(x => x.MailboxId == blocked.Id).ToListAsync());
    }

    private sealed class MixedHarness : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"mixed-mailboxes-{Guid.NewGuid():N}.db");
        private ServiceProvider services = null!;
        public ManualClock Clock { get; } = new();
        public List<EmailInboxSettings> Mailboxes { get; } = [];
        public EmailInboxSettings Global => Mailboxes[0];
        public Dictionary<InboundMailboxProvider, ControlledAdapter> Adapters { get; } = [];
        public MailboxIngestionCoordinator Coordinator { get; private set; } = null!;

        public static async Task<MixedHarness> CreateAsync(InboundMailboxProvider globalProvider,
            IInboundMailboxAdapter? overrideAdapter = null, IForwardedEmailParser? parser = null,
            bool realRuleExecutor = false, bool seedMessages = true,
            IReadOnlyDictionary<string, string?>? ingestionSettings = null)
        {
            var fixture = new MixedHarness();
            var registrations = new ServiceCollection();
            registrations.AddScoped<HelpdeskDbContext>(_ => fixture.Open());
            registrations.AddSingleton<TimeProvider>(fixture.Clock);
            registrations.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            registrations.AddSingleton<MailboxCredentialProtector>();
            registrations.AddScoped<MailboxLeaseStore>();
            registrations.AddScoped<MailboxSyncService>();
            registrations.AddScoped<MailboxOutboxStore>();
            registrations.AddScoped<IIngressEffectContext, IngressEffectContext>();
            registrations.AddSingleton(parser ?? Substitute.For<IForwardedEmailParser>());
            registrations.AddScoped(_ => new RatelDeskIdentityDbContext(new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
                .UseInMemoryDatabase("unused-mixed-identity").Options));
            registrations.AddScoped<InboundTenantRouter>();
            registrations.AddScoped<IInboundEmailRuleProcessor, InboundEmailRuleProcessor>();
            if (realRuleExecutor)
                registrations.AddScoped<IInboundEmailActionExecutor, InboundEmailActionExecutor>();
            else
                registrations.AddSingleton(Substitute.For<IInboundEmailActionExecutor>());
            registrations.AddLogging();
            registrations.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            registrations.AddScoped<IRequestSender, RequestSender>();
            registrations.AddTransient<IRequestHandler<CreateIncidentCommand, Incident>, CreateIncidentCommandHandler>();
            var references = Substitute.For<Helpdesk.Application.Services.Tickets.ITicketRefGeneratorService>();
            references.NextReferenceAsync(Arg.Any<string>()).Returns(call => $"{call.Arg<string>()}-{Guid.NewGuid():N}");
            registrations.AddSingleton(references);
            registrations.AddSingleton(Substitute.For<ISupportNotificationService>());
            registrations.AddSingleton<IDomainEventPublisher>(NoopDomainEventPublisher.Instance);
            registrations.AddSingleton<ILogger>(NullLogger.Instance);
            registrations.AddSingleton(Substitute.For<ITenantProvisioningService>());
            registrations.AddSingleton(Substitute.For<ITicketNotificationService>());
            registrations.AddSingleton(Substitute.For<IEmailService>());
            registrations.AddSingleton(Substitute.For<ITicketAttachmentService>());
            var inline = Substitute.For<IInboundInlineImageResolver>();
            inline.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<InboundEmailAttachmentContext>>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call => new InboundInlineImageResult(call.ArgAt<string>(1), new HashSet<int>()));
            registrations.AddSingleton(inline);
            registrations.AddSingleton(Substitute.For<IEmailTemplateRenderer>());
            registrations.AddSingleton(Substitute.For<IEmailLayoutResolver>());
            registrations.AddSingleton(Substitute.For<ITenantBrandingResolver>());
            registrations.AddScoped<InboundTicketProcessor>();
            foreach (var provider in Enum.GetValues<InboundMailboxProvider>())
            {
                var adapter = new ControlledAdapter(provider);
                fixture.Adapters.Add(provider, adapter);
                registrations.AddSingleton<IInboundMailboxAdapter>(overrideAdapter?.Provider == provider ? overrideAdapter : adapter);
            }
            var settings = new Dictionary<string, string?> { ["EmailIngestion:Enabled"] = "true" };
            if (ingestionSettings is not null)
                foreach (var (key, value) in ingestionSettings) settings[key] = value;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            registrations.AddSingleton<IConfiguration>(configuration);
            registrations.AddScoped<MailboxWorkerPolicy>();
            fixture.services = registrations.BuildServiceProvider();
            fixture.Coordinator = new MailboxIngestionCoordinator(fixture.services.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<MailboxIngestionCoordinator>.Instance, fixture.Clock);
            await using var db = fixture.Open();
            await db.Database.EnsureCreatedAsync();
            for (var i = 0; i < 13; i++)
                db.Organizations.Add(new Organization { Id = $"tenant-{i}", Name = $"Synthetic {i}", DnsName = $"tenant{i}.example.com" });
            fixture.Mailboxes.Add(NewMailbox(globalProvider, null));
            fixture.Mailboxes.Add(NewMailbox(InboundMailboxProvider.Graph, "tenant-10"));
            fixture.Mailboxes.Add(NewMailbox(InboundMailboxProvider.Imap, "tenant-11"));
            fixture.Mailboxes.Add(NewMailbox(InboundMailboxProvider.Pop3, "tenant-12"));
            foreach (var mailbox in fixture.Mailboxes)
            {
                db.EmailInboxSettings.Add(mailbox);
                db.Set<MailboxLease>().Add(new MailboxLease { MailboxId = mailbox.Id });
                db.Set<MailboxIngestionState>().Add(new MailboxIngestionState { MailboxId = mailbox.Id, SourceKey = mailbox.SourceKey });
            }
            if (seedMessages)
            {
                for (var i = 0; i < 10; i++)
                    fixture.AddMessage(fixture.Global, $"message-{i}", $"requester@tenant{i}.example.com");
                for (var i = 10; i < 13; i++)
                    fixture.AddMessage(fixture.Mailboxes.Single(x => x.OrganizationId == $"tenant-{i}"), $"message-{i}", $"requester@external{i}.example.net");
            }
            await db.SaveChangesAsync();
            return fixture;
        }

        public void AddMessage(EmailInboxSettings mailbox, string key, string sender)
        {
            var context = new InboundEmailContext("reused-message-id", null, mailbox.Id, null, mailbox.MailboxAddress,
                sender, "Synthetic requester", [], [], "New service request", "<p>Help please</p>", "Help please",
                Clock.GetUtcNow(), new Dictionary<string, string>(), []) { SourceMessageKey = key };
            Adapters[mailbox.Provider].Feeds.GetOrAdd(mailbox.Id, _ => []).Enqueue(new InboundSourceMessage(key, context));
        }

        public async Task SeedReceiptAsync(EmailInboxSettings mailbox, string key, InboundReceiptOutcome outcome,
            string sender = "requester@tenant0.example.com", bool envelope = true)
        {
            var message = new InboundEmailContext(key, null, mailbox.Id, mailbox.OrganizationId, mailbox.MailboxAddress,
                sender, "Synthetic requester", [], [], "Durable request", "<p>Help</p>", "Help",
                Clock.GetUtcNow(), new Dictionary<string, string>(), []);
            await using var db = Open();
            db.Set<InboundMessageReceipt>().Add(new InboundMessageReceipt
            {
                MailboxId = mailbox.Id, SourceKey = mailbox.SourceKey, TransportKey = key,
                ConfigurationVersion = mailbox.Version, InternetMessageId = key, Outcome = outcome,
                ProtectedEnvelope = envelope ? services.GetRequiredService<MailboxCredentialProtector>()
                    .Protect(mailbox.Id, JsonSerializer.Serialize(message)) : string.Empty,
                CreatedUnixMilliseconds = Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                UpdatedUnixMilliseconds = Clock.GetUtcNow().ToUnixTimeMilliseconds()
            });
            await db.SaveChangesAsync();
        }

        public int FetchCount(Guid mailboxId) => Adapters.Values.Sum(x => x.Fetches.GetValueOrDefault(mailboxId));

        public IServiceScope CreateScope() => services.CreateScope();

        public HelpdeskDbContext Open() => new(new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString()).Options,
            new AdminTenantContext(), new HttpContextAccessor());

        internal static EmailInboxSettings NewMailbox(InboundMailboxProvider provider, string? organization) => new()
        {
            Id = Guid.NewGuid(), Provider = provider, OrganizationId = organization,
            Scope = organization is null ? MailboxScope.Global : MailboxScope.Organization,
            MailHost = "mail.example.com", MailboxAddress = $"{organization ?? "global"}@example.com",
            TenantId = string.Empty, ClientId = string.Empty, ClientSecret = string.Empty, CredentialVersion = 1,
            SourceKey = Guid.NewGuid().ToString("N"), Enabled = true, BackgroundSyncEnabled = true, PollIntervalSeconds = 30
        };

        public async ValueTask DisposeAsync()
        {
            await Coordinator.StopRunnersAsync();
            Coordinator.Dispose();
            await services.DisposeAsync();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    private sealed class BeforeAcknowledgementAdapter(IInboundMailboxAdapter inner, Func<Task> beforeAck) : IInboundMailboxAdapter
    {
        public InboundMailboxProvider Provider => inner.Provider;
        public Task<MailboxConnectionTest> TestAsync(EmailInboxSettings mailbox, CancellationToken ct) => inner.TestAsync(mailbox, ct);
        public Task<InboundSourceBatch> FetchAsync(EmailInboxSettings mailbox, MailboxIngestionState state,
            IReadOnlySet<string> knownKeys, CancellationToken ct) => inner.FetchAsync(mailbox, state, knownKeys, ct);
        public async Task AcknowledgeAsync(EmailInboxSettings mailbox, string key, CancellationToken ct)
        {
            await beforeAck();
            await inner.AcknowledgeAsync(mailbox, key, ct);
        }
    }

    private sealed class CommitCheckingGraphTransport : HttpMessageHandler
    {
        public Func<Task> CheckCommitted { get; set; } = null!;
        public int MimeFetches { get; private set; }
        public int DeltaFetches { get; private set; }
        public int Acknowledgements { get; private set; }
        public int CommittedChecks { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Contains("IdType=\"ImmutableId\"", string.Join(",", request.Headers.GetValues("Prefer")));
            if (request.Method == HttpMethod.Patch)
            {
                await CheckCommitted();
                CommittedChecks++;
                Acknowledgements++;
                return Acknowledgements == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"code\":\"SyntheticAckFailure\"}}", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.RequestUri!.ToString().Contains("$value"))
            {
                MimeFetches++;
                var mime = (await File.ReadAllTextAsync(Path.Combine(TestEnvironment.RepositoryRoot, "tests", "Fixtures", "inbound", "multipart-request.eml"), ct))
                    .Replace("requester@example.test", "requester@tenant0.example.com", StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(mime, Encoding.UTF8, "message/rfc822") };
            }
            DeltaFetches++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"value":[{"id":"AbC-Case","isRead":false}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta?cursor=stable"}""",
                Encoding.UTF8, "application/json") };
        }
    }

    private sealed class MissingNeighborGraphTransport : HttpMessageHandler
    {
        public int ValidMimeFetches { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Patch) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path.Contains("missing-id") && path.Contains("$value"))
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"ErrorItemNotFound\"}}", Encoding.UTF8, "application/json")
                };
            if (path.Contains("valid-id") && path.Contains("$value"))
            {
                ValidMimeFetches++;
                var mime = (await File.ReadAllTextAsync(Path.Combine(TestEnvironment.RepositoryRoot, "tests", "Fixtures", "inbound", "multipart-request.eml"), ct))
                    .Replace("requester@example.test", "requester@tenant0.example.com", StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(mime, Encoding.UTF8, "message/rfc822") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"value":[{"id":"missing-id","isRead":false},{"id":"valid-id","isRead":false}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta?cursor=complete"}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class MoveNotFoundGraphTransport(bool sourceMissing) : HttpMessageHandler
    {
        public bool SourceProbeAttempted { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Patch)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (path.EndsWith("/move", StringComparison.Ordinal))
                return Task.FromResult(Error());
            if (request.Method == HttpMethod.Get && path.EndsWith("/messages/source-id", StringComparison.Ordinal))
            {
                SourceProbeAttempted = true;
                return Task.FromResult(sourceMissing
                    ? Error()
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"source-id\"}", Encoding.UTF8, "application/json")
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"value\":[],\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/delta?cursor=empty\"}",
                    Encoding.UTF8, "application/json")
            });
        }

        private static HttpResponseMessage Error() => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":{\"code\":\"ErrorItemNotFound\"}}", Encoding.UTF8, "application/json")
        };
    }

    private sealed class ControlledAdapter(InboundMailboxProvider provider) : IInboundMailboxAdapter, IHistoricalMailboxAdapter
    {
        public InboundMailboxProvider Provider => provider;
        public ConcurrentDictionary<Guid, ConcurrentQueue<InboundSourceMessage>> Feeds { get; } = new();
        public ConcurrentDictionary<Guid, int> Fetches { get; } = new();
        public ConcurrentDictionary<Guid, int> Acknowledgements { get; } = new();
        public Guid? FetchFailureMailbox { get; set; }
        public Guid? BlockedAcknowledgmentMailbox { get; set; }
        public HashSet<string> FailingAcknowledgmentKeys { get; } = [];
        public HashSet<string> MissingAcknowledgmentKeys { get; } = [];
        public HashSet<string> SlowAcknowledgmentKeys { get; } = [];
        public TaskCompletionSource AcknowledgmentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource acknowledgmentRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? BlockedMailbox { get; set; }
        public TaskCompletionSource BlockedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MailboxConnectionTest> TestAsync(EmailInboxSettings settings, CancellationToken ct) =>
            Task.FromResult(new MailboxConnectionTest(true, "Synthetic protocol fixture"));

        public Task<IReadOnlyList<HistoricalSourcePreview>> PreviewAsync(EmailInboxSettings settings,
            IReadOnlyList<string> keys, CancellationToken ct) => Task.FromResult<IReadOnlyList<HistoricalSourcePreview>>(
            keys.Select(key => new HistoricalSourcePreview(key, null, null, null,
                Feeds.GetValueOrDefault(settings.Id)?.Any(x => x.Key == key) == true, null)).ToArray());

        public Task<InboundSourceMessage> FetchHistoricalAsync(EmailInboxSettings settings, string key, CancellationToken ct)
        {
            var message = Feeds.GetValueOrDefault(settings.Id)?.FirstOrDefault(x => x.Key == key);
            if (message is null) throw new InboundSourceMissingException("Synthetic source is absent.");
            return Task.FromResult(message);
        }

        public async Task<InboundSourceBatch> FetchAsync(EmailInboxSettings settings, MailboxIngestionState state,
            IReadOnlySet<string> knownKeys, CancellationToken ct)
        {
            Fetches.AddOrUpdate(settings.Id, 1, (_, count) => count + 1);
            if (settings.Id == FetchFailureMailbox) throw new IOException("Synthetic enumeration failure");
            if (settings.Id == BlockedMailbox)
            {
                BlockedEntered.TrySetResult();
                try { await release.Task.WaitAsync(ct); }
                catch (OperationCanceledException)
                {
                    BlockedCancelled.TrySetResult();
                    throw;
                }
            }
            var messages = Feeds.GetOrAdd(settings.Id, _ => []).Where(x => !knownKeys.Contains(x.Key)).ToArray();
            return new InboundSourceBatch(messages, null, true);
        }

        public async Task AcknowledgeAsync(EmailInboxSettings settings, string key, CancellationToken ct)
        {
            Acknowledgements.AddOrUpdate(settings.Id, 1, (_, count) => count + 1);
            if (settings.Id == BlockedAcknowledgmentMailbox)
            {
                AcknowledgmentEntered.TrySetResult();
                await acknowledgmentRelease.Task.WaitAsync(ct);
            }
            if (MissingAcknowledgmentKeys.Contains(key))
                throw new InboundSourceMissingException("Synthetic source already absent");
            if (SlowAcknowledgmentKeys.Contains(key))
            {
                AcknowledgmentEntered.TrySetResult();
                await acknowledgmentRelease.Task.WaitAsync(ct);
            }
            if (FailingAcknowledgmentKeys.Contains(key)) throw new IOException("Synthetic acknowledgment failure");
        }

        public void ReleaseAcknowledgment() => acknowledgmentRelease.TrySetResult();
    }

    private sealed class AdminTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref milliseconds));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref milliseconds, (long)duration.TotalMilliseconds);
    }
}
