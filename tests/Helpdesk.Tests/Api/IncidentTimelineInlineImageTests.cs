using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using HtmlAgilityPack;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed partial class LiveHistoryFilteringEndpointsTests
{
    [Theory]
    [InlineData("", "\"")]
    [InlineData("https://api.example.test", "'")]
    [InlineData("", "")]
    public async Task IncidentTimeline_RenewsExpiredInboundImages_WithoutChangingStoredHtml(string publicApiBaseUrl, string quote)
    {
        var root = Path.Combine(Path.GetTempPath(), "timeline-images-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new StorageOptions { RootPath = root, PublicApiBaseUrl = publicApiBaseUrl, ImageSigningSecret = "synthetic-image-secret" };
            var signer = new ImageLinkSigner(Options.Create(options), NullLogger<ImageLinkSigner>.Instance);
            var storage = new InlineImageStorageService(Substitute.For<IHostEnvironment>(), Options.Create(options), signer,
                NullLogger<InlineImageStorageService>.Instance);
            var resolver = new InboundInlineImageResolver(storage, NullLogger<InboundInlineImageResolver>.Instance);
            var resolved = await resolver.ResolveAsync("incident-images", $"<p>Reply</p><img src={quote}cid:sample{quote}>",
                [new InboundEmailAttachmentContext("sample.png", "image/png", "sample", [1, 2, 3], IsInline: true)]);
            var originalSource = ImageSources(resolved.Html).Single();
            var path = originalSource.Split('?')[0];
            var filename = Path.GetFileName(path);
            var expired = path + "?token=" + signer.GenerateToken("incident", "incident-images", filename, DateTimeOffset.UtcNow.AddDays(-1));
            var storedHtml = resolved.Html.Replace(originalSource, expired);
            await using var harness = await LiveHistoryFilteringHarness.CreateAsync(imageLinkSigner: signer, storageOptions: options);
            var timelineEvent = new TicketTimelineEvent
            {
                TicketId = "incident-images", EventType = TimelineEventType.CustomerReply,
                MessageHtml = storedHtml, MessageText = "Synthetic reply"
            };
            await harness.SeedAsync(db =>
            {
                db.Incidents.Add(new Incident { Id = "incident-images", TrackingId = "INC-IMAGES", Title = "Synthetic images", OrganizationId = "org-1" });
                db.TicketTimelineEvents.Add(timelineEvent);
            });

            var timeline = await harness.Client.GetFromJsonAsync<TicketTimelineEventDto[]>("/api/v1/incidents/incident-images/timeline");
            var delivered = Assert.Single(timeline!);
            var refreshed = Assert.Single(ImageSources(delivered.MessageHtml!));
            Assert.NotEqual(expired, refreshed);
            Assert.Equal("Synthetic reply", delivered.MessageText);
            Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync(expired)).StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, await harness.Client.GetByteArrayAsync(refreshed));
            Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync(path)).StatusCode);
            await harness.WithDbAsync(async db => Assert.Equal(storedHtml,
                (await db.TicketTimelineEvents.AsNoTracking().SingleAsync(evt => evt.Id == timelineEvent.Id)).MessageHtml));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IncidentTimeline_OnlyRenewsThisTicketsImageSources_AndUsesCurrentHost()
    {
        var options = new StorageOptions { ImageSigningSecret = "synthetic-image-secret" };
        var signer = new ImageLinkSigner(Options.Create(options), NullLogger<ImageLinkSigner>.Instance);
        const string html = "<p title='preserve'>Reply &amp; text</p><!-- <img src='/api/incidents/incident-images/images/comment.png'> -->" +
            "<img data-src='/api/incidents/incident-images/images/lazy.png' src='https://example.test/logo.png'>" +
            "<img src='/api/incidents/other-ticket/images/private.png?token=expired'>" +
            "<img src='/api/incidents/incident-images/images/nested%2Ffile.png?token=expired'>" +
            "<img src='/api/incidents/incident-images/images/nested%5Cfile.png?token=expired'>" +
            "<img src='data:image/png;base64,AA=='>" +
            "<img src='https://old.example.test/api/incidents/incident-images/images/legacy.png?token=expired'>";
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(imageLinkSigner: signer, storageOptions: options);
        await harness.SeedAsync(db =>
        {
            db.Incidents.Add(new Incident { Id = "incident-images", TrackingId = "INC-IMAGES", Title = "Synthetic images", OrganizationId = "org-1" });
            db.TicketTimelineEvents.Add(new TicketTimelineEvent { TicketId = "incident-images", EventType = TimelineEventType.CustomerReply, MessageHtml = html });
        });

        var timeline = await harness.Client.GetFromJsonAsync<TicketTimelineEventDto[]>("/api/v1/incidents/incident-images/timeline");
        var refreshedHtml = Assert.Single(timeline!).MessageHtml!;
        var refreshed = ImageSources(refreshedHtml).Last();
        Assert.StartsWith("/api/incidents/incident-images/images/legacy.png?token=", refreshed);
        Assert.True(signer.ValidateToken(QueryHelpers.ParseQuery(new Uri(new Uri("https://example.test"), refreshed).Query)["token"]!,
            "incident", "incident-images", "legacy.png"));
        Assert.Equal(html.Replace("https://old.example.test/api/incidents/incident-images/images/legacy.png?token=expired", refreshed), refreshedHtml);
    }

    [Fact]
    public async Task IncidentTimelineStream_RenewsLinks_WithoutMutatingSharedEventOrLosingMetadata()
    {
        var options = new StorageOptions { ImageSigningSecret = "synthetic-image-secret" };
        var signer = new ImageLinkSigner(Options.Create(options), NullLogger<ImageLinkSigner>.Instance);
        var expiredToken = signer.GenerateToken("incident", "incident-images", "sample.png", DateTimeOffset.UtcNow.AddDays(-1));
        var html = $"<img src='/api/incidents/incident-images/images/sample.png?token={expiredToken}'>";
        var channel = Channel.CreateUnbounded<TicketTimelineEventDto>();
        var bus = Substitute.For<ITimelineEventBus>();
        bus.Subscribe("incident-images").Returns(channel.Reader);
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(imageLinkSigner: signer, storageOptions: options, timelineEventBus: bus);
        await harness.SeedAsync(db => db.Incidents.Add(new Incident
        {
            Id = "incident-images", TrackingId = "INC-IMAGES", Title = "Synthetic images", OrganizationId = "org-1"
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await harness.Client.GetAsync("/api/v1/incidents/incident-images/timeline/stream", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal(": connected", await reader.ReadLineAsync(timeout.Token));
        var shared = new TicketTimelineEventDto
        {
            Id = Guid.NewGuid(), TicketId = "incident-images", EventType = TimelineEventType.CustomerReply,
            MessageHtml = html, MessageText = "Synthetic reply", Hours = 2, TechnicianName = "Example technician",
            CcRecipients = ["sample@example.test"], RetryCount = 3
        };
        await channel.Writer.WriteAsync(shared, timeout.Token);
        channel.Writer.Complete();
        string? data = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
            if (line.StartsWith("data: ", StringComparison.Ordinal)) { data = line[6..]; break; }
        var delivered = JsonSerializer.Deserialize<TicketTimelineEventDto>(Assert.IsType<string>(data))!;
        Assert.Equal(shared.Id, delivered.Id);
        Assert.Equal(shared.Hours, delivered.Hours);
        Assert.Equal(shared.TechnicianName, delivered.TechnicianName);
        Assert.Equal(shared.CcRecipients, delivered.CcRecipients);
        Assert.Equal(shared.RetryCount, delivered.RetryCount);
        Assert.Equal(html, shared.MessageHtml);
        var refreshed = Assert.Single(ImageSources(delivered.MessageHtml!));
        var token = QueryHelpers.ParseQuery(new Uri(new Uri("https://example.test"), refreshed).Query)["token"].ToString();
        Assert.True(signer.ValidateToken(token, "incident", "incident-images", "sample.png"));
        Assert.NotEqual(expiredToken, token);
    }

    private static string[] ImageSources(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode.Descendants("img")
            .Select(image => Assert.IsType<string>(image.Attributes["src"]?.DeEntitizeValue)).ToArray();
    }
}
