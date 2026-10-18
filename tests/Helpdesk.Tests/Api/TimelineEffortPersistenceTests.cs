using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Helpdesk.API.Background;
using Helpdesk.API.DependencyInjection;
using Helpdesk.API.Endpoints.Changes;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Endpoints.Tickets;
using Helpdesk.API.Endpoints.WorkLogs;
using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.Changes;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed partial class TimelineEffortPersistenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // This regression deliberately uses only the pre-fix contracts so the same
    // test can reproduce the reload failure against the unmodified application.
    [Theory]
    [InlineData("Incident", false)]
    [InlineData("Request", false)]
    [InlineData("Change", false)]
    [InlineData("Incident", true)]
    [InlineData("Request", true)]
    [InlineData("Change", true)]
    public async Task Created_quarter_hour_internal_note_survives_fresh_scope_timeline_get(string module, bool postgres)
    {
        await using var harness = await EffortHarness.CreateAsync(postgres);
        await harness.SeedTicketAsync(module);

        var created = await harness.PostAsync(module, 0.25, true);
        Assert.Equal(0.25, created.GetProperty("hours").GetDouble());
        var live = Assert.Single(harness.Bus.Events);
        Assert.Equal(0.25, live.Hours);
        await harness.WithDbAsync(async db =>
        {
            var log = await db.Set<WorkLog>().AsNoTracking().SingleAsync();
            Assert.Equal(0.25, log.Hours);
            Assert.Equal(created.GetProperty("id").GetString(), log.Id);
            Assert.Equal(0.25, (await db.Tickets.AsNoTracking().SingleAsync()).TimeSpentHours);
        });

        // HTTP requests use independent request scopes and database contexts.
        // The second GET also covers a browser reload/reopen of the same ticket.
        foreach (var order in new[] { "asc", "desc" })
        {
            var timeline = await harness.TimelineAsync(module, order);
            var persisted = Assert.Single(timeline.EnumerateArray());
            Assert.Equal(live.Id, persisted.GetProperty("id").GetGuid());
            Assert.Equal(0.25, persisted.GetProperty("hours").GetDouble());
        }
        Assert.Empty(harness.Email.ReceivedCalls());
        Assert.Empty(harness.Notifications.ReceivedCalls());
    }

    [Theory]
    [InlineData("Incident", false)]
    [InlineData("Request", false)]
    [InlineData("Change", false)]
    [InlineData("Incident", true)]
    [InlineData("Request", true)]
    [InlineData("Change", true)]
    public async Task Repeated_notes_preserve_each_worklog_identity_fraction_and_explicit_zero(string module, bool postgres)
    {
        await using var harness = await EffortHarness.CreateAsync(postgres);
        await harness.SeedTicketAsync(module);
        var expected = new List<(string Id, double Hours, bool Internal, string Actor)>();
        foreach (var (hours, internalNote, actor) in new[]
        {
            (0.25, true, "technician-one"), (0.625, false, "technician-two"), (0d, false, "technician-one")
        })
        {
            harness.UseActor(actor);
            var created = await harness.PostAsync(module, hours, internalNote);
            expected.Add((created.GetProperty("id").GetString()!, hours, internalNote, actor));
        }

        var liveNotes = harness.Bus.Events.Where(evt => evt.EventType != TimelineEventType.EmailDelivery).ToArray();
        Assert.Equal(3, liveNotes.Length);
        var firstGet = await harness.TimelineAsync(module, "asc");
        var reopened = await harness.TimelineAsync(module, "desc");
        var ascendingNoteIds = firstGet.EnumerateArray()
            .Where(item => item.GetProperty("eventType").GetInt32() != (int)TimelineEventType.EmailDelivery)
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        var descendingNoteIds = reopened.EnumerateArray()
            .Where(item => item.GetProperty("eventType").GetInt32() != (int)TimelineEventType.EmailDelivery)
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(liveNotes.Select(item => item.Id).ToArray(), ascendingNoteIds);
        Assert.Equal(ascendingNoteIds.Reverse().ToArray(), descendingNoteIds);
        foreach (var (id, hours, internalNote, actor) in expected)
        {
            var noteType = internalNote ? TimelineEventType.InternalNote : TimelineEventType.Worklog;
            var live = Assert.Single(liveNotes, evt => Field(JsonSerializer.SerializeToElement(evt, JsonOptions), "workLogId").GetString() == id);
            Assert.Equal(hours, live.Hours);
            Assert.Equal(hours, Field(JsonSerializer.SerializeToElement(live, JsonOptions), "recordedHours").GetDouble());
            Assert.Equal(actor, live.CreatedByUserId);
            Assert.Equal(noteType, live.EventType);
            foreach (var timeline in new[] { firstGet, reopened })
            {
                var item = Assert.Single(timeline.EnumerateArray(), evt =>
                    evt.GetProperty("eventType").GetInt32() == (int)noteType &&
                    Field(evt, "workLogId").ValueKind == JsonValueKind.String && Field(evt, "workLogId").GetString() == id);
                Assert.Equal(live.Id, item.GetProperty("id").GetGuid());
                Assert.Equal(hours, item.GetProperty("hours").GetDouble());
                Assert.Equal(hours, Field(item, "recordedHours").GetDouble());
                Assert.Equal(actor, item.GetProperty("createdByUserId").GetString());
                Assert.Equal("Identical note", item.GetProperty("messageText").GetString());
            }
        }
        await harness.WithDbAsync(async db =>
        {
            Assert.Equal(3, await db.Set<WorkLog>().CountAsync());
            Assert.Equal(0.875, (await db.Tickets.AsNoTracking().SingleAsync()).TimeSpentHours);
            var events = await db.TicketTimelineEvents.Where(evt => evt.EventType != TimelineEventType.EmailDelivery).ToListAsync();
            foreach (var expectedLog in expected)
            {
                var persisted = Assert.Single(events, evt => db.Entry(evt).Property<string?>("WorkLogId").CurrentValue == expectedLog.Id);
                Assert.Equal(expectedLog.Hours, db.Entry(persisted).Property<double?>("Hours").CurrentValue);
            }
        });
        Assert.Equal(2, harness.Email.ReceivedCalls().Count());
        await harness.Notifications.Received(2).NotifyUserAsync("customer-own", "ticket-updated", "ticket-effort");
        var firstDeliveries = firstGet.EnumerateArray()
            .Where(item => item.GetProperty("eventType").GetInt32() == (int)TimelineEventType.EmailDelivery).ToArray();
        var reopenedDeliveries = reopened.EnumerateArray()
            .Where(item => item.GetProperty("eventType").GetInt32() == (int)TimelineEventType.EmailDelivery).ToArray();
        Assert.Equal(2, firstDeliveries.Length);
        Assert.Equal(2, reopenedDeliveries.Length);
        Assert.Distinct(firstDeliveries.Select(item => item.GetProperty("id").GetGuid()));
        var internalWorklogId = Assert.Single(expected, item => item.Internal).Id;
        Assert.DoesNotContain(firstDeliveries.Concat(reopenedDeliveries), item => Field(item, "workLogId").GetString() == internalWorklogId);
        foreach (var publicNote in expected.Where(item => !item.Internal))
        {
            var firstDelivery = Assert.Single(firstDeliveries, item => Field(item, "workLogId").GetString() == publicNote.Id);
            var reopenedDelivery = Assert.Single(reopenedDeliveries, item => Field(item, "workLogId").GetString() == publicNote.Id);
            Assert.Equal(firstDelivery.GetProperty("id").GetGuid(), reopenedDelivery.GetProperty("id").GetGuid());
            foreach (var delivery in new[] { firstDelivery, reopenedDelivery })
            {
                Assert.Equal(JsonValueKind.Null, Field(delivery, "recordedHours").ValueKind);
                Assert.Equal(0, delivery.GetProperty("hours").GetDouble());
            }
        }
    }

    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task Historical_rows_remain_unavailable_without_guessing_from_identical_worklogs(string module)
    {
        await using var harness = await EffortHarness.CreateAsync();
        await harness.SeedTicketAsync(module);
        await harness.PostAsync(module, 0.25, true);
        var legacyId = Guid.NewGuid();
        var systemId = Guid.NewGuid();
        await harness.WithDbAsync(async db =>
        {
            var matching = await db.TicketTimelineEvents.AsNoTracking().SingleAsync();
            db.TicketTimelineEvents.AddRange(
                new TicketTimelineEvent { Id = legacyId, TicketId = matching.TicketId, CreatedUtc = matching.CreatedUtc,
                    CreatedByUserId = matching.CreatedByUserId, CreatedByUserName = matching.CreatedByUserName,
                    EventType = matching.EventType, MessageHtml = matching.MessageHtml, MessageText = matching.MessageText },
                new TicketTimelineEvent { Id = systemId, TicketId = matching.TicketId,
                    EventType = TimelineEventType.SystemNotification, MessageText = "System event" });
            await db.SaveChangesAsync();
        });

        var timeline = await harness.TimelineAsync(module);
        foreach (var id in new[] { legacyId, systemId })
        {
            var item = Assert.Single(timeline.EnumerateArray(), evt => evt.GetProperty("id").GetGuid() == id);
            Assert.Equal(JsonValueKind.Null, Field(item, "recordedHours").ValueKind);
            Assert.Equal(JsonValueKind.Null, Field(item, "workLogId").ValueKind);
            Assert.Equal(0, item.GetProperty("hours").GetDouble());
        }
        Assert.Single(timeline.EnumerateArray(), evt => Field(evt, "recordedHours").ValueKind == JsonValueKind.Number);
    }

    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task Persisted_effort_keeps_tenant_and_customer_internal_note_boundaries(string module)
    {
        await using var harness = await EffortHarness.CreateAsync();
        await harness.SeedTicketAsync(module);
        await harness.PostAsync(module, 0.25, true);
        await harness.PostAsync(module, 0.625, false);

        harness.UseActor("customer-own");
        var customerTimeline = await harness.TimelineAsync(module);
        Assert.DoesNotContain(customerTimeline.EnumerateArray(), item => item.GetProperty("eventType").GetInt32() == (int)TimelineEventType.InternalNote);
        var publicNote = Assert.Single(customerTimeline.EnumerateArray(), item => item.GetProperty("eventType").GetInt32() == (int)TimelineEventType.Worklog);
        Assert.Equal(0.625, Field(publicNote, "recordedHours").GetDouble());

        harness.UseActor("tenant-reader");
        var staffTimeline = await harness.TimelineAsync(module);
        Assert.Contains(staffTimeline.EnumerateArray(), item => item.GetProperty("eventType").GetInt32() == (int)TimelineEventType.InternalNote && Field(item, "recordedHours").GetDouble() == 0.25);
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.PostAsJsonAsync(EffortHarness.Route(module) + "/worklogs",
            new CreateWorkLogDto { Notes = "Read does not grant write", Hours = 0.25, IsInternalNote = true })).StatusCode);

        harness.UseActor("customer-peer");
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.GetAsync(EffortHarness.Route(module) + "/timeline")).StatusCode);
        harness.UseActor("foreign-reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.GetAsync(EffortHarness.Route(module) + "/timeline")).StatusCode);
    }

    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task Signed_public_history_reads_authoritative_legacy_worklogs_without_guessing_staff_history(string module)
    {
        await using var harness = await EffortHarness.CreateAsync();
        await harness.SeedTicketAsync(module);
        const string publicWorklogId = "legacy-public-worklog";
        var legacyEventId = Guid.NewGuid();
        var loggedAt = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);
        await harness.WithDbAsync(async db =>
        {
            db.Set<WorkLog>().AddRange(
                new WorkLog { Id = publicWorklogId, TicketId = "ticket-effort", Hours = 0.625, NotesHtml = "Identical note",
                    NotesText = "Identical note", TechnicianId = "technician-one", LoggedAt = loggedAt },
                new WorkLog { Id = "legacy-private-worklog", TicketId = "ticket-effort", Hours = 0.25, NotesHtml = "Private note",
                    NotesText = "Private note", IsInternalNote = true, TechnicianId = "technician-one", LoggedAt = loggedAt });
            db.TicketTimelineEvents.Add(new TicketTimelineEvent { Id = legacyEventId, TicketId = "ticket-effort",
                EventType = TimelineEventType.Worklog, MessageHtml = "Identical note", MessageText = "Identical note",
                CreatedByUserId = "technician-one", CreatedByUserName = "Technician One", CreatedUtc = new DateTimeOffset(loggedAt) });
            await db.SaveChangesAsync();
        });

        var staffTimeline = await harness.TimelineAsync(module);
        var oldStaffRow = Assert.Single(staffTimeline.EnumerateArray());
        Assert.Equal(legacyEventId, oldStaffRow.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, Field(oldStaffRow, "recordedHours").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(oldStaffRow, "workLogId").ValueKind);
        var publicRoute = "/api/v1/tickets/public/timeline?trackingId=" + module + "-EFFORT&email=customer%40example.test&token=synthetic-signed-token";
        using var response = await harness.Client.GetAsync(publicRoute);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var publicTimeline = await response.Content.ReadFromJsonAsync<JsonElement>();
        var publicRow = Assert.Single(publicTimeline.EnumerateArray());
        Assert.Equal(publicWorklogId, Field(publicRow, "workLogId").GetString());
        Assert.Equal(0.625, Field(publicRow, "recordedHours").GetDouble());
        Assert.Equal(0.625, publicRow.GetProperty("hours").GetDouble());
        Assert.Equal("Identical note", publicRow.GetProperty("messageText").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync(publicRoute.Replace("synthetic-signed-token", "invalid-token"))).StatusCode);
    }

    private static JsonElement Field(JsonElement element, string name)
    {
        Assert.True(element.TryGetProperty(name, out var value), $"Missing {name}: {element}");
        return value;
    }

    internal sealed class EffortHarness : IAsyncDisposable
    {
        private readonly SqliteConnection? connection;
        private readonly ServiceIdentityMigrationTests.DatabaseFixture? providerDatabase;
        private readonly WebApplication app;
        private EffortHarness(SqliteConnection? connection, WebApplication app, HttpClient client, CapturingBus bus,
            IEmailService email, INotificationService notifications, ServiceIdentityMigrationTests.DatabaseFixture? providerDatabase)
        {
            this.connection = connection; this.app = app; Client = client; Bus = bus; Email = email; Notifications = notifications;
            this.providerDatabase = providerDatabase;
        }
        public HttpClient Client { get; }
        public CapturingBus Bus { get; }
        public IEmailService Email { get; }
        public INotificationService Notifications { get; }
        public static string Route(string module) => "/api/v1/" + module.ToLowerInvariant() + "s/ticket-effort";
        public void UseActor(string actor) => Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", actor);

        public static async Task<EffortHarness> CreateAsync(bool postgres = false)
        {
            SqliteConnection? connection = null;
            ServiceIdentityMigrationTests.DatabaseFixture? providerDatabase = null;
            if (postgres)
                providerDatabase = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(true);
            else
            {
                connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddHttpContextAccessor();
            if (providerDatabase is not null)
                builder.Services.AddScoped<HelpdeskDbContext>(_ => providerDatabase.Open());
            else
                builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection!));
            builder.Services.AddScoped<ITenantContext>(_ => new AdminTenant());
            builder.Services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            builder.Services.AddScoped<IRequestSender, RequestSender>();
            builder.Services.AddScoped<IRequestHandler<CreateWorkLogCommand, WorkLog>, CreateWorkLogCommandHandler>();
            builder.Services.AddSingleton<ICurrentUserAccessService>(new ActorAccess());
            // The production endpoint groups also map lifecycle and approval
            // routes with inferred services, even though these tests only call
            // timeline/worklog routes. Keep those boundaries inert in this host.
            builder.Services.AddSingleton(Substitute.For<IDomainEventPublisher>());
            builder.Services.AddSingleton(Substitute.For<ICorrelationContext>());
            builder.Services.AddSingleton(Substitute.For<IChangeTemplateService>());
            builder.Services.AddSingleton(Substitute.For<ITicketNotificationService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaInitializer>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaCompletionService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaRepository>());
            builder.Services.AddSingleton(Substitute.For<ISlaClockService>());
            builder.Services.AddSingleton(Substitute.For<ISlaEscalationEvaluator>());
            builder.Services.AddSingleton(Substitute.For<IBackgroundJobQueue>());
            builder.Services.AddSingleton(Substitute.For<ISupportNotificationService>());
            builder.Services.AddSingleton(Substitute.For<ISupportAccessService>());
            builder.Services.AddSingleton(Substitute.For<Helpdesk.Application.Services.Tickets.ITicketRefGeneratorService>());
            var bus = new CapturingBus();
            builder.Services.AddSingleton<ITimelineEventBus>(bus);
            var notifications = Substitute.For<INotificationService>();
            builder.Services.AddSingleton(notifications);
            var email = Substitute.For<IEmailService>();
            email.SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IEnumerable<EmailAttachmentData>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>())
                .Returns(true);
            builder.Services.AddSingleton(email);
            var sanitizer = Substitute.For<IHtmlSanitizerService>();
            sanitizer.Sanitize(Arg.Any<string>()).Returns(info => info.Arg<string>());
            builder.Services.AddSingleton(sanitizer);
            var images = Substitute.For<IWorklogImageStorageService>();
            images.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(info => info.ArgAt<string>(1));
            builder.Services.AddSingleton(images);
            var text = Substitute.For<IHtmlToPlainTextConverter>();
            text.Convert(Arg.Any<string>()).Returns(info => info.Arg<string>());
            builder.Services.AddSingleton(text);
            var renderer = Substitute.For<IEmailTemplateRenderer>();
            renderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns(info => info.Arg<string>());
            builder.Services.AddSingleton(renderer);
            builder.Services.AddSingleton(Substitute.For<IEmailLayoutResolver>());
            var branding = Substitute.For<ITenantBrandingResolver>();
            branding.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new TenantBrandingResolved());
            builder.Services.AddSingleton(branding);
            var signer = Substitute.For<IPublicTicketLinkSigner>();
            signer.GenerateToken(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>()).Returns("synthetic-signed-token");
            signer.ValidateToken("synthetic-signed-token", Arg.Any<string>(), "customer@example.test").Returns(true);
            builder.Services.AddSingleton(signer);
            builder.Services.AddSingleton(Substitute.For<IImageLinkSigner>());
            builder.Services.Configure<StorageOptions>(_ => { });
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, ActorAuthentication>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                foreach (var policy in new[] { "HelpdeskAdmin", "HelpdeskStaff", "IncidentAccess", "IncidentManager", "RequestAccess", "RequestManager", "ChangeAccess", "ChangeManager" })
                    options.AddPolicy(policy, permission => permission.RequireAuthenticatedUser());
            });
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization();
            app.MapWorkLogEndpoints(); app.MapRequestEndpoints(); app.MapChangeEndpoints(); app.MapPublicTicketEndpoints();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                if (postgres) await db.Database.MigrateAsync();
                else await db.Database.EnsureCreatedAsync();
            }
            await app.StartAsync();
            var harness = new EffortHarness(connection, app, app.GetTestClient(), bus, email, notifications, providerDatabase);
            harness.UseActor("technician-one");
            return harness;
        }

        public Task SeedTicketAsync(string module) => WithDbAsync(async db =>
        {
            db.Organizations.Add(new Organization { Id = "org-effort", Name = "Effort organization" });
            db.Customers.Add(new Customer { Id = "customer-own", OrganizationId = "org-effort", Email = "customer@example.test", Name = "Customer" });
            Ticket ticket = module switch { "Incident" => new Incident(), "Request" => new Request(), _ => new Change() };
            ticket.Id = "ticket-effort"; ticket.Title = "Timeline effort regression"; ticket.TrackingId = module + "-EFFORT";
            ticket.OrganizationId = "org-effort"; ticket.CustomerId = "customer-own"; ticket.RequesterEmail = "customer@example.test";
            db.Tickets.Add(ticket);
            db.EmailTemplates.AddRange(new EmailTemplate { Name = "TicketUpdated", Subject = "Update", HtmlContent = "Update" },
                new EmailTemplate { Name = "ChangeUpdate", Subject = "Update", HtmlContent = "Update" });
            await db.SaveChangesAsync();
        });
        public async Task<JsonElement> PostAsync(string module, double hours, bool internalNote)
        {
            using var response = await Client.PostAsJsonAsync(Route(module) + "/worklogs",
                new CreateWorkLogDto { Hours = hours, IsInternalNote = internalNote, Notes = "Identical note" });
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async Task<JsonElement> TimelineAsync(string module, string order = "asc")
        {
            using var response = await Client.GetAsync(Route(module) + "/timeline?order=" + order);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async Task WithDbAsync(Func<HelpdeskDbContext, Task> action)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>());
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await app.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
            if (providerDatabase is not null) await providerDatabase.DisposeAsync();
        }
    }

    internal sealed class CapturingBus : ITimelineEventBus
    {
        private readonly Helpdesk.Infrastructure.Services.TimelineEventBus inner = new();
        private int subscriptionCount;
        public int SubscriptionCount => Volatile.Read(ref subscriptionCount);
        public List<TicketTimelineEventDto> Events { get; } = [];
        public TaskCompletionSource<bool> Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask PublishAsync(TicketTimelineEventDto evt) { Events.Add(evt); return inner.PublishAsync(evt); }
        public ChannelReader<TicketTimelineEventDto> Subscribe(string ticketId)
        {
            var reader = inner.Subscribe(ticketId);
            Interlocked.Increment(ref subscriptionCount);
            Subscribed.TrySetResult(true);
            return reader;
        }
        public void Unsubscribe(string ticketId, ChannelReader<TicketTimelineEventDto> reader)
        {
            inner.Unsubscribe(ticketId, reader);
            Interlocked.Decrement(ref subscriptionCount);
        }
    }
    private sealed class AdminTenant : ITenantContext
    {
        public string? TenantId => "org-effort";
        public string? UserId => "technician-one";
        public bool IsHelpdeskAdmin => true;
    }
    private sealed class ActorAccess : ICurrentUserAccessService
    {
        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            var actor = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var customer = actor.StartsWith("customer-", StringComparison.Ordinal);
            var organization = actor == "foreign-reader" ? "org-foreign" : "org-effort";
            var permissionSuffix = customer ? ".User" : actor == "tenant-reader" ? ".Read" : ".Write";
            var permissions = new[] { "Incident", "Request", "Change" }.Select(module => module + permissionSuffix).ToHashSet();
            return Task.FromResult(new CurrentUserAccessProfile(true, actor, actor + "@example.test", organization, "Organization",
                customer ? actor : null, false, new HashSet<string>(), permissions, new HashSet<string> { organization }, new HashSet<string>())
            { ScopedPermissionGrants = permissions.Select(permission => new ScopedPermissionGrant(permission, organization)).ToHashSet() });
        }
    }
    private sealed class ActorAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public ActorAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers.Authorization.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "technician-one";
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actor), new Claim(ClaimTypes.Name, actor)], Scheme.Name)), Scheme.Name)));
        }
    }
}
