using System.Collections.Concurrent;
using System.Net;
using System.Text;
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
            mailbox.MarkReadAfterSuccess = false;
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
    public async Task Transaction_retry_preserves_distinct_incident_ids_and_one_attachment_file()
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
                await new TicketAttachmentService(db, fileStore, effects).SaveAsync(ids[0],
                    [new AttachmentUpload("evidence.txt", "text/plain", [1, 2, 3])], null, default);
                var attachment = await db.Attachments.SingleAsync();
                var filePath = fileStore.GetReadPath(attachment.FilePath)!;
                Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(filePath));
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

        public static async Task<MixedHarness> CreateAsync(InboundMailboxProvider globalProvider, IInboundMailboxAdapter? overrideAdapter = null)
        {
            var fixture = new MixedHarness();
            var registrations = new ServiceCollection();
            registrations.AddScoped<HelpdeskDbContext>(_ => fixture.Open());
            registrations.AddSingleton<TimeProvider>(fixture.Clock);
            registrations.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            registrations.AddSingleton<MailboxCredentialProtector>();
            registrations.AddScoped<MailboxLeaseStore>();
            registrations.AddScoped<MailboxOutboxStore>();
            registrations.AddScoped<IIngressEffectContext, IngressEffectContext>();
            registrations.AddSingleton(Substitute.For<IForwardedEmailParser>());
            registrations.AddScoped(_ => new RatelDeskIdentityDbContext(new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
                .UseInMemoryDatabase("unused-mixed-identity").Options));
            registrations.AddScoped<InboundTenantRouter>();
            registrations.AddScoped<IInboundEmailRuleProcessor, InboundEmailRuleProcessor>();
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
            fixture.services = registrations.BuildServiceProvider();
            fixture.Coordinator = new MailboxIngestionCoordinator(fixture.services.GetRequiredService<IServiceScopeFactory>(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EmailIngestion:Enabled"] = "true" }).Build(),
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
            for (var i = 0; i < 10; i++)
                fixture.AddMessage(fixture.Global, $"message-{i}", $"requester@tenant{i}.example.com");
            for (var i = 10; i < 13; i++)
                fixture.AddMessage(fixture.Mailboxes.Single(x => x.OrganizationId == $"tenant-{i}"), $"message-{i}", $"requester@external{i}.example.net");
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

        public int FetchCount(Guid mailboxId) => Adapters.Values.Sum(x => x.Fetches.GetValueOrDefault(mailboxId));

        public IServiceScope CreateScope() => services.CreateScope();

        public HelpdeskDbContext Open() => new(new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString()).Options,
            new AdminTenantContext(), new HttpContextAccessor());

        private static EmailInboxSettings NewMailbox(InboundMailboxProvider provider, string? organization) => new()
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

    private sealed class ControlledAdapter(InboundMailboxProvider provider) : IInboundMailboxAdapter
    {
        public InboundMailboxProvider Provider => provider;
        public ConcurrentDictionary<Guid, ConcurrentQueue<InboundSourceMessage>> Feeds { get; } = new();
        public ConcurrentDictionary<Guid, int> Fetches { get; } = new();
        public Guid? BlockedMailbox { get; set; }
        public TaskCompletionSource BlockedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MailboxConnectionTest> TestAsync(EmailInboxSettings settings, CancellationToken ct) =>
            Task.FromResult(new MailboxConnectionTest(true, "Synthetic protocol fixture"));

        public async Task<InboundSourceBatch> FetchAsync(EmailInboxSettings settings, MailboxIngestionState state,
            IReadOnlySet<string> knownKeys, CancellationToken ct)
        {
            Fetches.AddOrUpdate(settings.Id, 1, (_, count) => count + 1);
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

        public Task AcknowledgeAsync(EmailInboxSettings settings, string key, CancellationToken ct) => Task.CompletedTask;
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
