using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using Helpdesk.API.Endpoints.PublicIngress;
using Helpdesk.API.Endpoints.Tickets;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Security;
using Helpdesk.ServiceDefaults.Security;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.PublicAccess;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Tests.Fixtures;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class PublicTicketEndpointsTests
{
    [Theory]
    [InlineData("request", false)]
    [InlineData("request", true)]
    [InlineData("incident", false)]
    [InlineData("change", false)]
    public async Task Scoped_view_and_timeline_emit_only_customer_visible_fields(string kind, bool internalOnly)
    {
        await using var harness = await Harness.CreateAsync();
        Ticket ticket = kind switch { "request" => new Request(), "change" => new Change(), _ => new Incident() };
        await harness.SeedAsync(ticket);
        await using (var db = harness.Database.Open())
        {
            if (!internalOnly)
            {
                db.WorkLogs.Add(new WorkLog
                {
                    Id = "distinctive-public-worklog", TicketId = ticket.Id, NotesText = "Public update", Hours = 0.5,
                    NotesHtml = "<b>Public update</b>", TechnicianId = "distinctive-staff-id", LoggedAt = DateTime.UtcNow.AddMinutes(-1)
                });
            }
            db.WorkLogs.Add(new WorkLog
            {
                Id = "distinctive-private-worklog", TicketId = ticket.Id, NotesText = "Private diagnostic",
                IsInternalNote = true, LoggedAt = DateTime.UtcNow
            });
            db.TicketTimelineEvents.Add(new TicketTimelineEvent
            {
                TicketId = ticket.Id, WorkLogId = "distinctive-private-worklog", EventType = TimelineEventType.TechnicianReply,
                CreatedByUserName = "Distinctive Staff Name", MessageText = "Private reply"
            });
            await db.SaveChangesAsync();
        }

        var grant = await PublicLinkFixture.ExchangeAsync(harness.Services, await PublicLinkFixture.IssueAsync(harness.Services, ticket.Id));
        using var response = await PublicLinkFixture.SendAsync(harness.Client, grant, "ticket");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ticket.TrackingId, view.GetProperty("trackingId").GetString());
        Assert.Equal(ticket.Title, view.GetProperty("title").GetString());
        Assert.Equal(internalOnly ? null : "Public update", view.GetProperty("lastPublicNote").GetString());
        Assert.False(view.TryGetProperty("id", out _));

        using var timeline = await PublicLinkFixture.SendAsync(harness.Client, grant, "timeline");
        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        var raw = await timeline.Content.ReadAsStringAsync();
        Assert.DoesNotContain("distinctive", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private", raw, StringComparison.Ordinal);
        var items = JsonSerializer.Deserialize<PublicTicketTimelineItemDto[]>(raw, JsonSerializerOptions.Web);
        Assert.Equal(internalOnly ? 0 : 1, items!.Length);
        if (!internalOnly)
        {
            var row = Assert.Single(items);
            Assert.Equal("<b>Public update</b>", row.MessageHtml);
            Assert.Equal(0.5, row.Hours);
            Assert.False(row.IsCustomer);
        }
        using var invalid = await PublicLinkFixture.SendAsync(harness.Client, grant with { Grant = "invalid" }, "timeline");
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Beta9Evidence.Record("B8-PUBLIC-VIEW-INTERNAL-NOTE-DISCLOSURE", $"{kind}-{internalOnly}",
            new { internalOnly, publicTimelineCount = items.Length, invalidGrantStatus = (int)invalid.StatusCode });
    }

    [Fact]
    public async Task Public_replies_are_in_instant_order_with_no_staff_metadata_or_cross_ticket_rows()
    {
        await using var harness = await Harness.CreateAsync();
        var ticket = new Incident();
        await harness.SeedAsync(ticket);
        var other = new Incident { Id = "other-ticket", TrackingId = "INC-OTHER", OrganizationId = ticket.OrganizationId };
        await using (var db = harness.Database.Open())
        {
            db.Tickets.Add(other);
            var first = new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
            db.TicketTimelineEvents.AddRange(
                new() { TicketId = ticket.Id, EventType = TimelineEventType.CustomerReply, CreatedUtc = first, MessageText = "First" },
                new() { TicketId = ticket.Id, EventType = TimelineEventType.TechnicianReply, CreatedUtc = first.AddMinutes(1).ToOffset(TimeSpan.FromHours(-4)),
                    MessageText = "Second", CreatedByUserId = "distinctive-staff-id", CreatedByUserName = "Distinctive Staff Name" },
                new() { TicketId = ticket.Id, EventType = TimelineEventType.SystemNotification, CreatedUtc = first.AddMinutes(2), MessageText = "Private system event" },
                new() { TicketId = other.Id, EventType = TimelineEventType.CustomerReply, CreatedUtc = first.AddMinutes(3), MessageText = "Other ticket" });
            await db.SaveChangesAsync();
        }

        var grant = await PublicLinkFixture.ExchangeAsync(harness.Services, await PublicLinkFixture.IssueAsync(harness.Services, ticket.Id));
        using var response = await PublicLinkFixture.SendAsync(harness.Client, grant, "timeline");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("distinctive", raw, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(raw);
        Assert.All(document.RootElement.EnumerateArray(), row =>
            Assert.Equal(["createdUtc", "isCustomer", "eventType", "messageHtml", "messageText", "hours"],
                row.EnumerateObject().Select(property => property.Name)));
        var items = JsonSerializer.Deserialize<PublicTicketTimelineItemDto[]>(raw, JsonSerializerOptions.Web)!;
        Assert.Equal(["Second", "First"], items.Select(row => row.MessageText));
        Assert.Equal([false, true], items.Select(row => row.IsCustomer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migrations_and_concurrent_exchange_preserve_binding_and_one_browser(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var ticket = new Incident();
        await harness.SeedAsync(ticket);
        var launch = await PublicLinkFixture.IssueAsync(harness.Services, ticket.Id);
        var first = new PublicLinkExchangeRequest(launch, null, PublicLinkFixture.Browser());
        var second = first with { BrowserKey = PublicLinkFixture.Browser() };

        using var preview = await harness.Client.GetAsync(PublicAccessProtocol.ApiPrefix + "/exchange");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, preview.StatusCode);
        var results = await Task.WhenAll(harness.ExchangeAsync(first), harness.ExchangeAsync(second));
        try
        {
            var accepted = Assert.Single(results, response => response.IsSuccessStatusCode);
            var rejected = Assert.Single(results, response => !response.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            var winner = ReferenceEquals(accepted, results[0]) ? first : second;
            var grant = (await accepted.Content.ReadFromJsonAsync<PublicLinkExchangeResponse>())!;
            using var repeat = await harness.ExchangeAsync(winner);
            Assert.Equal(grant.AccessId, (await repeat.Content.ReadFromJsonAsync<PublicLinkExchangeResponse>())!.AccessId);
            using var wrong = await PublicLinkFixture.SendAsync(harness.Client, grant with { AccessId = Guid.NewGuid() }, "ticket");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            using var differentPurpose = await harness.Client.GetAsync($"{PublicAccessProtocol.ApiPrefix}/{grant.AccessId:D}/ticket");
            Assert.Equal(HttpStatusCode.Unauthorized, differentPurpose.StatusCode);

            await using var db = harness.Database.Open();
            var row = await db.Set<PublicLinkCapability>().SingleAsync();
            Assert.NotEqual(launch, row.LaunchDigest);
            Assert.Equal("customer@example.test", row.Recipient);
            Assert.Equal(ticket.OrganizationId, row.OrganizationId);
            Assert.NotNull(row.GrantExpiresAt);
            row.RevokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync();
            using var revoked = await PublicLinkFixture.SendAsync(harness.Client, grant, "ticket");
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }
        finally
        {
            foreach (var result in results) result.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_exchange_validates_recipient_and_expiry_and_stops_at_the_persisted_cutoff(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var ticket = new Incident();
        await harness.SeedAsync(ticket);
        var signer = harness.Services.GetRequiredService<IPublicTicketLinkSigner>();
        const string recipient = "customer@example.test";
        var token = signer.GenerateToken(ticket.TrackingId, recipient, DateTimeOffset.UtcNow.AddMinutes(5));
        var legacy = new LegacyPublicLink(PublicLinkPurpose.Ticket, ticket.TrackingId, null, recipient, token);
        var browser = PublicLinkFixture.Browser();
        using var denied = await harness.ExchangeAsync(new(null, legacy with { Email = "other@example.test" }, browser));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var noncanonical = await harness.ExchangeAsync(new(null, legacy with { Token = token + "=" }, browser));
        Assert.Equal(HttpStatusCode.Unauthorized, noncanonical.StatusCode);
        using var expired = await harness.ExchangeAsync(new(null, legacy with
        {
            Token = signer.GenerateToken(ticket.TrackingId, recipient, DateTimeOffset.UtcNow.AddMinutes(-1))
        }, browser));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var accepted = await harness.ExchangeAsync(new(null, legacy, browser));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var consumed = await harness.ExchangeAsync(new(null, legacy, PublicLinkFixture.Browser()));
        Assert.Equal(HttpStatusCode.Unauthorized, consumed.StatusCode);

        await using var db = harness.Database.Open();
        var policy = await db.Set<PublicLinkCompatibility>().SingleAsync();
        Assert.InRange(policy.LegacyCutoffUnix, DateTimeOffset.UtcNow.AddDays(29).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
        policy.LegacyCutoffUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync();
        using var retired = await harness.ExchangeAsync(new(null, legacy, browser));
        Assert.Equal(HttpStatusCode.Unauthorized, retired.StatusCode);
    }

    [Fact]
    public async Task Replacement_queues_one_email_to_original_recipient_and_revokes_previous_grant()
    {
        await using var harness = await Harness.CreateAsync();
        var ticket = new Incident();
        await harness.SeedAsync(ticket);
        var mailboxId = await harness.SeedMailboxAsync(ticket.OrganizationId!);
        var launch = await PublicLinkFixture.IssueAsync(harness.Services, ticket.Id);
        var grant = await PublicLinkFixture.ExchangeAsync(harness.Services, launch);
        var request = new PublicLinkExchangeRequest(launch, null, PublicLinkFixture.Browser());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var replacement = await harness.Client.PostAsJsonAsync(PublicAccessProtocol.ApiPrefix + "/replacement", request);
            Assert.Equal(HttpStatusCode.Accepted, replacement.StatusCode);
        }
        await using var db = harness.Database.Open();
        var effect = await db.Set<MailboxOutboxEffect>().SingleAsync();
        var stored = MailboxOutboxStore.Deserialize<IngressEmailEffect>(effect.Payload);
        Assert.Equal(mailboxId, stored.MailboxId);
        Assert.Equal(ticket.OrganizationId, stored.OrganizationId);
        Assert.NotNull(effect.DeliveryEventId);
        Assert.Contains("/public/link#!rd-mail.", stored.Html);
        await using var scope = harness.Services.CreateAsyncScope();
        var message = scope.ServiceProvider.GetRequiredService<MailboxOutboxStore>().PrepareEmailForDelivery(effect);
        Assert.Equal(["customer@example.test"], message.Recipients);
        var replacementLaunch = Regex.Match(message.Html, "/public/link#([A-Za-z0-9_-]{43})").Groups[1].Value;
        Assert.NotEmpty(replacementLaunch);
        Assert.NotEqual(launch, replacementLaunch);
        Assert.DoesNotContain(replacementLaunch, effect.Payload);
        using var exchanged = await harness.ExchangeAsync(new(replacementLaunch, null, PublicLinkFixture.Browser()));
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        using var denied = await PublicLinkFixture.SendAsync(harness.Client, grant, "ticket");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Legacy_replacement_requires_the_signed_binding_even_after_redemption_and_can_replace_a_recent_expired_link()
    {
        await using var harness = await Harness.CreateAsync();
        var ticket = new Incident();
        await harness.SeedAsync(ticket);
        var signer = harness.Services.GetRequiredService<IPublicTicketLinkSigner>();
        const string recipient = "customer@example.test";
        var legacy = new LegacyPublicLink(PublicLinkPurpose.Ticket, ticket.TrackingId, null, recipient,
            signer.GenerateToken(ticket.TrackingId, recipient, DateTimeOffset.UtcNow.AddMinutes(5)));
        var browser = PublicLinkFixture.Browser();
        using var accepted = await harness.ExchangeAsync(new(null, legacy, browser));
        var grant = (await accepted.Content.ReadFromJsonAsync<PublicLinkExchangeResponse>())!;
        using var mismatched = await harness.Client.PostAsJsonAsync(PublicAccessProtocol.ApiPrefix + "/replacement",
            new PublicLinkExchangeRequest(null, legacy with { Email = "other@example.test" }, browser));
        Assert.Equal(HttpStatusCode.Accepted, mismatched.StatusCode);
        using var stillAuthorized = await PublicLinkFixture.SendAsync(harness.Client, grant, "ticket");
        Assert.Equal(HttpStatusCode.OK, stillAuthorized.StatusCode);

        using var expired = await harness.Client.PostAsJsonAsync(PublicAccessProtocol.ApiPrefix + "/replacement",
            new PublicLinkExchangeRequest(null, legacy with
            {
                Token = signer.GenerateToken(ticket.TrackingId, recipient, DateTimeOffset.UtcNow.AddMinutes(-5))
            }, browser));
        Assert.Equal(HttpStatusCode.Accepted, expired.StatusCode);

        await using var db = harness.Database.Open();
        var effect = await db.Set<MailboxOutboxEffect>().SingleAsync();
        Assert.Equal([recipient], MailboxOutboxStore.Deserialize<IngressEmailEffect>(effect.Payload).Recipients);
    }

    private sealed class Harness(ServiceIdentityMigrationTests.DatabaseFixture database, WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public IServiceProvider Services => app.Services;
        public HttpClient Client => client;
        public ServiceIdentityMigrationTests.DatabaseFixture Database => database;

        public static async Task<Harness> CreateAsync(bool postgres = false)
        {
            var database = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
            await using (var db = database.Open())
            {
                Assert.False(db.Database.HasPendingModelChanges());
                await db.Database.MigrateAsync();
            }
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddScoped(_ => database.Open());
            PublicLinkFixture.AddServices(builder);
            builder.Services.AddScoped<IIngressEffectContext, IngressEffectContext>();
            builder.Services.AddScoped<MailboxOutboxStore>();
            var branding = Substitute.For<ITenantBrandingResolver>();
            branding.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TenantBrandingResolved());
            builder.Services.AddSingleton(branding);
            var app = builder.Build();
            app.UsePublicLinkResponseHeaders();
            app.MapPublicLinkEndpoints();
            app.MapPublicTicketEndpoints();
            await app.StartAsync();
            return new Harness(database, app, app.GetTestClient());
        }

        public async Task SeedAsync(Ticket ticket)
        {
            await using var db = database.Open();
            ticket.Id = "synthetic-ticket-id";
            ticket.Title = "Public example";
            ticket.TrackingId = "PUB-REGRESSION";
            ticket.OrganizationId = "public-organization";
            db.Organizations.Add(new Organization { Id = ticket.OrganizationId, Name = "Example organization" });
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
        }

        public Task<HttpResponseMessage> ExchangeAsync(PublicLinkExchangeRequest request) =>
            client.PostAsJsonAsync(PublicAccessProtocol.ApiPrefix + "/exchange", request);

        public async Task<Guid> SeedMailboxAsync(string organizationId)
        {
            await using var db = database.Open();
            var mailbox = new EmailInboxSettings
            {
                MailboxAddress = "outgoing@example.test", SourceKey = "public-link-fixture", Scope = MailboxScope.Organization,
                OrganizationId = organizationId, Enabled = true
            };
            db.EmailInboxSettings.Add(mailbox);
            db.Add(new MailboxOutgoingSettings
            {
                MailboxId = mailbox.Id, Enabled = true, Transport = MailboxOutgoingTransport.Smtp,
                ProtectedSmtpPassword = "fixture-mailbox-credential"
            });
            await db.SaveChangesAsync();
            return mailbox.Id;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
