using System.Text.Json;
using Helpdesk.API.Endpoints.Email;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class MailboxCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_marker_adopts_existing_ticket_without_replaying_rules(bool timeline)
    {
        await using var fixture = await Harness.CreateAsync((_, _, _) => throw new InvalidOperationException("Legacy replay must not execute."));
        fixture.Mailbox.LegacySource = true;
        await using (var db = fixture.Open())
        {
            await db.EmailInboxSettings.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LegacySource, true));
            db.Incidents.Add(new Incident { Id = "historical", OrganizationId = "tenant-a", TrackingId = "INC-HISTORICAL" });
            if (timeline) db.TicketTimelineEvents.Add(new TicketTimelineEvent { TicketId = "historical", EventType = TimelineEventType.CustomerReply, CreatedByUserId = "<reused-rfc-id>" });
            else db.InboundEmailProcessingLogs.Add(new InboundEmailProcessingLog { MessageId = "reused-rfc-id", MailboxKey = "default", Status = InboundEmailProcessingStatus.Succeeded, TicketId = "historical" });
            await db.SaveChangesAsync();
        }
        await fixture.Coordinator.ProcessAsync(fixture.Mailbox, fixture.Lease, fixture.ReceiptId, default);
        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(InboundReceiptOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("LegacyReplayAdopted", receipt.Reason);
        Assert.Equal("historical", receipt.TicketId);
        Assert.Empty(await verify.Customers.ToListAsync());
        Assert.Empty(await verify.Set<MailboxOutboxEffect>().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_marker_cannot_suppress_foreign_tenant_or_second_native_delivery(bool alreadyAdopted)
    {
        var called = false;
        await using var fixture = await Harness.CreateAsync(async (services, _, ct) =>
        {
            called = true;
            var incident = new Incident { Id = "new-delivery", OrganizationId = "tenant-a", TrackingId = "INC-NEW" };
            var db = services.GetRequiredService<HelpdeskDbContext>();
            db.Incidents.Add(incident);
            await db.SaveChangesAsync(ct);
            return new InboundEmailRuleProcessingResult(true, false, incident);
        });
        fixture.Mailbox.LegacySource = true;
        await using (var db = fixture.Open())
        {
            await db.EmailInboxSettings.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LegacySource, true));
            db.Organizations.Add(new Organization { Id = "tenant-b", Name = "Other" });
            db.Incidents.Add(new Incident { Id = "historical", OrganizationId = alreadyAdopted ? "tenant-a" : "tenant-b", TrackingId = "INC-HISTORICAL" });
            db.TicketTimelineEvents.Add(new TicketTimelineEvent { TicketId = "historical", EventType = TimelineEventType.CustomerReply, CreatedByUserId = "reused-rfc-id" });
            if (alreadyAdopted) db.Set<InboundMessageReceipt>().Add(new InboundMessageReceipt
            {
                MailboxId = fixture.Mailbox.Id, SourceKey = fixture.Mailbox.SourceKey, TransportKey = "older-native-id",
                InternetMessageId = "reused-rfc-id", OrganizationId = "tenant-a", TicketId = "historical",
                Outcome = InboundReceiptOutcome.Succeeded, Reason = "LegacyReplayAdopted"
            });
            await db.SaveChangesAsync();
        }
        await fixture.Coordinator.ProcessAsync(fixture.Mailbox, fixture.Lease, fixture.ReceiptId, default);
        Assert.True(called);
        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync(x => x.Id == fixture.ReceiptId);
        Assert.Equal(InboundReceiptOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("new-delivery", receipt.TicketId);
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, "CrossTenantReference")]
    [InlineData(false, true, "AmbiguousTicketReference")]
    public async Task Thread_headers_resolve_only_one_authorized_tenant_ticket(bool foreign, bool ambiguous, string? expectedReason)
    {
        await using var fixture = await Harness.CreateAsync((_, _, _) => throw new InvalidOperationException());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        db.Organizations.Add(new Organization { Id = "tenant-b", Name = "Other" });
        db.Incidents.Add(new Incident { Id = "thread-one", OrganizationId = foreign ? "tenant-b" : "tenant-a", TrackingId = "INC-THREAD-ONE", RequesterEmail = "requester@example.com" });
        db.TicketTimelineEvents.Add(new TicketTimelineEvent { TicketId = "thread-one", EventType = TimelineEventType.CustomerReply, CreatedByUserId = "<thread-id>" });
        if (ambiguous)
        {
            db.Incidents.Add(new Incident { Id = "thread-two", OrganizationId = "tenant-a", TrackingId = "INC-THREAD-TWO", RequesterEmail = "requester@example.com" });
            db.TicketTimelineEvents.Add(new TicketTimelineEvent { TicketId = "thread-two", EventType = TimelineEventType.CustomerReply, CreatedByUserId = "<thread-id>" });
        }
        fixture.Mailbox.Scope = MailboxScope.Organization;
        fixture.Mailbox.OrganizationId = "tenant-a";
        await db.EmailInboxSettings.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Scope, MailboxScope.Organization).SetProperty(x => x.OrganizationId, "tenant-a"));
        await db.SaveChangesAsync();
        var receipt = await db.Set<InboundMessageReceipt>().SingleAsync();
        var message = JsonSerializer.Deserialize<InboundEmailContext>(fixture.Services.GetRequiredService<MailboxCredentialProtector>().Unprotect(fixture.Mailbox, receipt.ProtectedEnvelope))!;
        message = message with { InReplyTo = "<thread-id>" };
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.BeginIngressRoutingScope();
        var route = await scope.ServiceProvider.GetRequiredService<InboundTenantRouter>().ResolveAsync(fixture.Mailbox, message, default);
        Assert.Equal(expectedReason, route.Reason);
        Assert.Equal(expectedReason is null ? "thread-one" : null, route.ReferencedTicketId);
    }

    [Fact]
    public async Task Changed_configuration_rejects_old_runner_before_claiming_a_receipt()
    {
        var called = false;
        await using var fixture = await Harness.CreateAsync((_, _, _) =>
        {
            called = true;
            throw new InvalidOperationException("Old configuration must not execute rules.");
        });
        await using (var update = fixture.Open())
            await update.EmailInboxSettings.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Version, x => x.Version + 1));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.ProcessAsync(
            fixture.Mailbox, fixture.Lease, fixture.ReceiptId, default));
        Assert.Equal("MailboxFenceLostException", error.GetType().Name);
        Assert.False(called);
        await using var verify = fixture.Open();
        Assert.Equal(0, (await verify.Set<InboundMessageReceipt>().SingleAsync()).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Held_or_failed_rule_rolls_back_business_effects_and_retains_durable_attempt(bool fail)
    {
        await using var fixture = await Harness.CreateAsync(async (services, message, ct) =>
        {
            var db = services.GetRequiredService<HelpdeskDbContext>();
            db.Customers.Add(new Customer { Id = "partial-customer", Email = "partial@example.com", OrganizationId = "tenant-a" });
            db.Incidents.Add(new Incident { Id = "partial-incident", OrganizationId = "tenant-a", CustomerId = "partial-customer", TrackingId = "INC-PARTIAL" });
            db.InboundEmailProcessingLogs.Add(new InboundEmailProcessingLog
            {
                MessageId = message.SourceMessageKey!, MailboxId = message.MailboxId,
                MailboxKey = message.MailboxId!.Value.ToString("D"), ActionKey = "create",
                Status = InboundEmailProcessingStatus.Succeeded, TicketId = "partial-incident"
            });
            await db.SaveChangesAsync(ct);
            services.GetRequiredService<IIngressEffectContext>().Capture(MailboxEffectKind.Notification,
                new NotificationDto { Id = Guid.NewGuid(), Title = "Must roll back", Message = "Synthetic" });
            if (fail) throw new IOException("Synthetic rule failure");
            return new InboundEmailRuleProcessingResult(true, true, null);
        });
        await fixture.Coordinator.ProcessAsync(fixture.Mailbox, fixture.Lease, fixture.ReceiptId, default);
        await using var verify = fixture.Open();
        Assert.Empty(await verify.Incidents.ToListAsync());
        Assert.Empty(await verify.Customers.ToListAsync());
        Assert.Empty(await verify.Set<MailboxOutboxEffect>().ToListAsync());
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(1, receipt.Attempts);
        Assert.Equal(fail ? InboundReceiptOutcome.RetryableFailure : InboundReceiptOutcome.NeedsReview, receipt.Outcome);
        Assert.NotEmpty(receipt.ProtectedEnvelope);
        var audit = await verify.InboundEmailProcessingLogs.SingleAsync();
        Assert.Equal(InboundEmailProcessingStatus.Failed, audit.Status);
        Assert.Null(audit.TicketId);
    }

    [Fact]
    public async Task Poison_receipt_stops_after_five_attempts_and_does_not_provision_requester()
    {
        var calls = 0;
        await using var fixture = await Harness.CreateAsync((_, _, _) =>
        {
            calls++;
            throw new InvalidDataException("Synthetic poison input");
        });
        for (var i = 0; i < 7; i++)
            await fixture.Coordinator.ProcessAsync(fixture.Mailbox, fixture.Lease, fixture.ReceiptId, default);
        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(5, calls);
        Assert.Equal(5, receipt.Attempts);
        Assert.Equal(InboundReceiptOutcome.NeedsReview, receipt.Outcome);
        Assert.Empty(await verify.Customers.ToListAsync());
    }

    [Fact]
    public async Task Successful_rule_is_not_replayed_and_receipt_uses_native_delivery_identity()
    {
        var calls = 0;
        await using var fixture = await Harness.CreateAsync(async (services, message, ct) =>
        {
            calls++;
            Assert.StartsWith("ingress:", message.SourceMessageKey);
            Assert.Equal("reused-rfc-id", message.InternetMessageId);
            var incident = new Incident { Id = "created-once", OrganizationId = "tenant-a", TrackingId = "INC-ONCE" };
            var db = services.GetRequiredService<HelpdeskDbContext>();
            db.Incidents.Add(incident);
            await db.SaveChangesAsync(ct);
            return new InboundEmailRuleProcessingResult(true, false, incident);
        });
        await fixture.Coordinator.ProcessAsync(fixture.Mailbox, fixture.Lease, fixture.ReceiptId, default);
        await fixture.Coordinator.ProcessAsync(fixture.Mailbox, fixture.Lease, fixture.ReceiptId, default);
        await using var verify = fixture.Open();
        Assert.Equal(1, calls);
        Assert.Single(await verify.Incidents.ToListAsync());
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(InboundReceiptOutcome.Succeeded, receipt.Outcome);
        Assert.Empty(receipt.ProtectedEnvelope);
        var retry = await EmailSettingsEndpoints.RetryReceiptAsync(fixture.Mailbox.Id, receipt.Id, verify, default);
        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)retry).StatusCode);
        Assert.Equal(InboundReceiptOutcome.Succeeded,
            (await verify.Set<InboundMessageReceipt>().AsNoTracking().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Global_receipt_is_held_when_a_dedicated_assignment_exists_even_when_disabled()
    {
        await using var fixture = await Harness.CreateAsync((_, _, _) => throw new InvalidOperationException("Rules must not execute."));
        await using (var setup = fixture.Open())
        {
            var dedicated = Harness.NewMailbox();
            dedicated.Scope = MailboxScope.Organization;
            dedicated.OrganizationId = "tenant-a";
            dedicated.Enabled = false;
            setup.EmailInboxSettings.Add(dedicated);
            await setup.SaveChangesAsync();
        }
        await fixture.Coordinator.ProcessAsync(fixture.Mailbox, fixture.Lease, fixture.ReceiptId, default);
        await using var verify = fixture.Open();
        var receipt = await verify.Set<InboundMessageReceipt>().SingleAsync();
        Assert.Equal(InboundReceiptOutcome.NeedsReview, receipt.Outcome);
        Assert.Equal("TenantUsesDedicatedMailbox", receipt.Reason);
        Assert.Empty(await verify.Customers.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_manual_retries_accept_one_and_reset_attempt_budget()
    {
        await using var fixture = await Harness.CreateAsync((_, _, _) => throw new InvalidOperationException());
        await using (var setup = fixture.Open())
            await setup.Set<InboundMessageReceipt>().ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Outcome, InboundReceiptOutcome.NeedsReview).SetProperty(x => x.Attempts, 5));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int?> Retry()
        {
            await using var db = fixture.Open();
            await start.Task;
            var result = await EmailSettingsEndpoints.RetryReceiptAsync(fixture.Mailbox.Id, fixture.ReceiptId, db, default);
            return ((IStatusCodeHttpResult)result).StatusCode;
        }
        var first = Task.Run(Retry);
        var second = Task.Run(Retry);
        start.SetResult();
        var statuses = await Task.WhenAll(first, second);
        Assert.Single(statuses, x => x == StatusCodes.Status202Accepted);
        Assert.Single(statuses, x => x == StatusCodes.Status409Conflict);
        await using var verify = fixture.Open();
        Assert.Equal(0, (await verify.Set<InboundMessageReceipt>().SingleAsync()).Attempts);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"mailbox-coordinator-{Guid.NewGuid():N}.db");
        public EmailInboxSettings Mailbox { get; } = NewMailbox();
        public Guid ReceiptId { get; private set; }
        public MailboxLeaseToken Lease { get; private set; } = null!;
        public ServiceProvider Services { get; private set; } = null!;
        public MailboxIngestionCoordinator Coordinator { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(Func<IServiceProvider, InboundEmailContext, CancellationToken, Task<InboundEmailRuleProcessingResult>> rules)
        {
            var fixture = new Harness();
            var services = new ServiceCollection();
            services.AddScoped<HelpdeskDbContext>(_ => fixture.Open());
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<MailboxCredentialProtector>();
            services.AddScoped<MailboxLeaseStore>();
            services.AddScoped<MailboxOutboxStore>();
            services.AddScoped<IIngressEffectContext, IngressEffectContext>();
            services.AddSingleton(Substitute.For<IForwardedEmailParser>());
            services.AddScoped(_ => new RatelDeskIdentityDbContext(new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
                .UseInMemoryDatabase("unused-identity").Options));
            services.AddScoped<InboundTenantRouter>();
            services.AddScoped<IInboundEmailRuleProcessor>(provider => new RuleStub((message, ct) => rules(provider, message, ct)));
            fixture.Services = services.BuildServiceProvider();
            fixture.Coordinator = new MailboxIngestionCoordinator(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
                new ConfigurationBuilder().Build(), NullLogger<MailboxIngestionCoordinator>.Instance);
            await using var db = fixture.Open();
            await db.Database.EnsureCreatedAsync();
            db.Organizations.Add(new Organization { Id = "tenant-a", Name = "Synthetic tenant", DnsName = "example.com" });
            db.EmailInboxSettings.Add(fixture.Mailbox);
            db.Set<MailboxLease>().Add(new MailboxLease { MailboxId = fixture.Mailbox.Id });
            var message = new InboundEmailContext("reused-rfc-id", null, fixture.Mailbox.Id, null,
                fixture.Mailbox.MailboxAddress, "requester@example.com", "Requester", [], [], "Subject", "Body", "Body",
                DateTimeOffset.UtcNow, new Dictionary<string, string>(), []);
            var receipt = new InboundMessageReceipt
            {
                MailboxId = fixture.Mailbox.Id, SourceKey = fixture.Mailbox.SourceKey, TransportKey = "uid-1", InternetMessageId = message.InternetMessageId,
                ProtectedEnvelope = fixture.Services.GetRequiredService<MailboxCredentialProtector>().Protect(fixture.Mailbox.Id, JsonSerializer.Serialize(message))
            };
            db.Set<InboundMessageReceipt>().Add(receipt);
            await db.SaveChangesAsync();
            fixture.ReceiptId = receipt.Id;
            fixture.Lease = (await new MailboxLeaseStore(db, TimeProvider.System)
                .TryAcquireAsync(fixture.Mailbox.Id, "test-owner", TimeSpan.FromMinutes(2)))!;
            return fixture;
        }

        public HelpdeskDbContext Open() => new(new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options,
            new AdminTenantContext(), new HttpContextAccessor());

        public static EmailInboxSettings NewMailbox() => new()
        {
            Id = Guid.NewGuid(), MailHost = "mail.example.com", MailboxAddress = "support@example.com",
            TenantId = string.Empty, ClientId = string.Empty, ClientSecret = string.Empty,
            CredentialVersion = 1, SourceKey = Guid.NewGuid().ToString("N"), Enabled = true, BackgroundSyncEnabled = true
        };

        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            await Services.DisposeAsync();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    private sealed class RuleStub(Func<InboundEmailContext, CancellationToken, Task<InboundEmailRuleProcessingResult>> process)
        : IInboundEmailRuleProcessor
    {
        public Task<InboundEmailRuleProcessingResult> ProcessAsync(InboundEmailContext context, CancellationToken ct = default) => process(context, ct);
    }

    private sealed class AdminTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }
}
