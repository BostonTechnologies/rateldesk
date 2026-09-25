using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Html;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class GraphMailboxSenderTests
{
    [Fact]
    public async Task Same_source_Graph_credential_rotation_can_be_confirmed_for_one_queued_delivery()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
        await using var db = new HelpdeskDbContext(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "tenant-a", Name = "Tenant A" });
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), Scope = MailboxScope.Organization, OrganizationId = "tenant-a",
            Provider = InboundMailboxProvider.Graph, Authentication = MailboxAuthentication.MicrosoftApplication,
            MailboxAddress = "support@tenant-a.example.test", SourceKey = "stable-graph-source",
            ClientSecret = "old-synthetic-protected-secret", Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var outgoing = new MailboxOutgoingSettings
        {
            MailboxId = mailbox.Id, Enabled = true, Transport = MailboxOutgoingTransport.Graph
        };
        db.EmailInboxSettings.Add(mailbox);
        db.Set<MailboxOutgoingSettings>().Add(outgoing);
        db.Incidents.Add(new Incident { Id = "graph-ticket", TrackingId = "INC-GRAPH", OrganizationId = "tenant-a" });
        await db.SaveChangesAsync();
        var transport = new GraphSendFixture(HttpStatusCode.Accepted);
        var selectedSecrets = new List<string>();
        var graph = new GraphMailboxSender(selected =>
        {
            selectedSecrets.Add(selected.ClientSecret);
            return new GraphServiceClient(new HttpClient(transport, disposeHandler: false),
                new AnonymousAuthenticationProvider());
        });
        var effects = new IngressEffectContext();
        var store = new MailboxOutboxStore(db, effects, TimeProvider.System);
        var config = new ConfigurationBuilder().Build();
        var sender = new MailboxEmailService(new MailboxSenderResolver(db),
            new SmtpMailboxSender(new MailboxDestinationPolicy(config),
                new MailboxOutgoingCredentialProtector(new EphemeralDataProtectionProvider()),
                new HtmlToPlainTextConverter()), graph, store, effects,
            NullLogger<MailboxEmailService>.Instance);
        Assert.True(await sender.SendEmailAsync(new EmailSendRequest(
            ["requester@example.test"], "Graph credential repair", "<p>Original Graph content</p>")
        { TicketId = "graph-ticket" }));
        var row = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Kind == MailboxEffectKind.Email);
        var original = MailboxOutboxStore.Deserialize<IngressEmailEffect>(row.Payload);
        mailbox.ClientSecret = "rotated-synthetic-protected-secret";
        mailbox.Version++;
        await db.SaveChangesAsync();
        var claim = Assert.IsType<MailboxOutboxEffect>(await store.TryClaimAsync(row.Id, "first", default));
        var held = await sender.SendPinnedAsync(mailbox.Id, original.OrganizationId, original.TicketId,
            original.Recipients, original.Cc, original.Subject, original.Html, original.Attachments,
            original.ReplyTo, row.Id, default, original.MailboxConfigurationVersion, original.OutgoingConfigurationVersion);
        Assert.Equal("SenderConfigurationChanged", held.ErrorCode);
        Assert.Empty(selectedSecrets);
        Assert.True(await store.CompleteAsync(claim, false, held.ErrorCode, default, requiresReview: true));
        var retry = new MailboxOutgoingRetryService(db, new MailboxSenderResolver(db), TimeProvider.System);
        var preview = await retry.PreviewAsync(row.DeliveryEventId!.Value, default);
        Assert.True(preview.CanRetry);
        Assert.Equal("Queued", (await retry.RetryAsync(row.DeliveryEventId.Value,
            new ConfirmMailboxOutgoingRetryRequest(outgoing.Version, true)
            {
                ExpectedMailboxId = mailbox.Id, ExpectedMailboxVersion = mailbox.Version,
                ExpectedFence = preview.Fence
            }, "admin", default)).Status);
        var adopted = MailboxOutboxStore.Deserialize<IngressEmailEffect>((await db.Set<MailboxOutboxEffect>()
            .AsNoTracking().SingleAsync(x => x.Id == row.Id)).Payload);
        var accepted = await sender.SendPinnedAsync(mailbox.Id, adopted.OrganizationId, adopted.TicketId,
            adopted.Recipients, adopted.Cc, adopted.Subject, adopted.Html, adopted.Attachments,
            adopted.ReplyTo, row.Id, default, adopted.MailboxConfigurationVersion, adopted.OutgoingConfigurationVersion);
        Assert.Equal("Accepted by provider", accepted.Status);
        Assert.Equal(new[] { "rotated-synthetic-protected-secret" }, selectedSecrets);
        Assert.Equal("/v1.0/users/support%40tenant-a.example.test/sendMail", transport.Path);
        Assert.Contains("Graph credential repair", transport.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_send_grant_does_not_disable_receiving_for_the_selected_Graph_mailbox()
    {
        using var transport = new GraphReadAllowedSendDeniedFixture();
        var selectedCredentials = new List<(Guid MailboxId, string Secret)>();
        GraphServiceClient Client(EmailInboxSettings selected)
        {
            selectedCredentials.Add((selected.Id, selected.ClientSecret));
            return new GraphServiceClient(
                new HttpClient(transport, disposeHandler: false), new AnonymousAuthenticationProvider());
        }
        var secrets = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
        var adapter = new GraphMailboxAdapter(secrets, Client);
        var sender = new GraphMailboxSender(Client);
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), Provider = InboundMailboxProvider.Graph,
            Authentication = MailboxAuthentication.MicrosoftApplication,
            MailboxAddress = "support@tenant-a.example.test", MailboxFolder = "inbox",
            InitialImport = InitialMailImport.All, ClientSecret = "synthetic-protected"
        };
        var outgoing = new MailboxOutgoingSettings
        {
            MailboxId = mailbox.Id, Enabled = true, Transport = MailboxOutgoingTransport.Graph
        };

        var first = await adapter.FetchAsync(mailbox, new MailboxIngestionState(), new HashSet<string>(), default);
        var received = Assert.Single(first.Messages);
        Assert.Equal("graph-item-1", received.Key);
        Assert.Equal("requester@tenant-a.example.test", received.Message!.FromEmail);

        var delivery = await sender.SendAsync(mailbox, outgoing, ["requester@tenant-a.example.test"], [],
            "Synthetic confirmation", "<p>Received</p>", [], default);
        Assert.Equal("Needs review", delivery.Status);
        Assert.Equal("GraphSendPermissionDenied", delivery.ErrorCode);

        var readCheck = await adapter.TestAsync(mailbox, default);
        Assert.True(readCheck.Success);
        var next = await adapter.FetchAsync(mailbox,
            new MailboxIngestionState { Cursor = first.NextCursor, Initialized = true },
            new HashSet<string> { received.Key }, default);
        Assert.Empty(next.Messages);
        Assert.Equal(1, transport.MimeFetches);
        Assert.Equal(1, transport.SendAttempts);
        Assert.Equal(4, selectedCredentials.Count);
        Assert.All(selectedCredentials, selected =>
            Assert.Equal((mailbox.Id, mailbox.ClientSecret), selected));
        Assert.All(transport.Paths, path => Assert.Contains("/users/support%40tenant-a.example.test/", path));
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, "Accepted by provider", null)]
    [InlineData(HttpStatusCode.Forbidden, "Needs review", "GraphSendPermissionDenied")]
    [InlineData(HttpStatusCode.Unauthorized, "Needs review", "GraphAuthenticationFailed")]
    public async Task Sdk_uses_selected_mailbox_and_reports_send_grant_separately_from_read(
        HttpStatusCode response, string expectedStatus, string? expectedCode)
    {
        var transport = new GraphSendFixture(response);
        var sender = new GraphMailboxSender(_ => new GraphServiceClient(
            new HttpClient(transport, disposeHandler: false), new AnonymousAuthenticationProvider()));
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), Provider = InboundMailboxProvider.Graph,
            Authentication = MailboxAuthentication.MicrosoftApplication,
            MailboxAddress = "support@tenant-a.example.test", ClientSecret = "synthetic-protected" };
        var outgoing = new MailboxOutgoingSettings { MailboxId = mailbox.Id, Enabled = true,
            Transport = MailboxOutgoingTransport.Graph, DisplayName = "Tenant A support" };

        var result = await sender.SendAsync(mailbox, outgoing,
            ["requester@example.test", mailbox.MailboxAddress], ["tech@example.test"],
            "INC-123 update", "<p>Hello</p><img src=\"cid:logo@tenant-a.example.test\">",
            [new EmailAttachmentData { FileName = "logo.png", ContentType = "image/png",
                ContentBytes = [1, 2, 3], ContentId = "logo@tenant-a.example.test", IsInline = true },
             new EmailAttachmentData { FileName = "forwarded.eml", ContentType = "message/rfc822",
                ContentBytes = Encoding.UTF8.GetBytes("From: sender@example.test\r\nSubject: Forwarded\r\n\r\nMessage") }],
            default, ["hidden@example.test", "tech@example.test", mailbox.MailboxAddress]);

        Assert.True(expectedStatus == result.Status, $"Expected {expectedStatus}; got {result.Status}/{result.ErrorCode}.");
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal("/v1.0/users/support%40tenant-a.example.test/sendMail", transport.Path);
        Assert.Contains("support@tenant-a.example.test", transport.Body);
        Assert.Contains("requester@example.test", transport.Body);
        Assert.Contains("tech@example.test", transport.Body);
        using var request = JsonDocument.Parse(transport.Body);
        Assert.True(request.RootElement.TryGetProperty("Message", out var message), transport.Body);
        var blind = message.GetProperty("bccRecipients").EnumerateArray().ToArray();
        Assert.Equal("hidden@example.test", Assert.Single(blind).GetProperty("emailAddress")
            .GetProperty("address").GetString());
        Assert.True(message.TryGetProperty("attachments", out var attachmentList), transport.Body);
        var attachments = attachmentList
            .EnumerateArray().ToArray();
        var image = Assert.Single(attachments, attachment => attachment.GetProperty("name").GetString() == "logo.png");
        Assert.True(image.GetProperty("isInline").GetBoolean());
        Assert.Equal("logo@tenant-a.example.test", image.GetProperty("contentId").GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(image.GetProperty("contentBytes").GetString()!));
        var forwarded = Assert.Single(attachments, attachment => attachment.GetProperty("name").GetString() == "forwarded.eml");
        Assert.Equal("message/rfc822", forwarded.GetProperty("contentType").GetString());
        Assert.Contains("Subject: Forwarded", Encoding.UTF8.GetString(
            Convert.FromBase64String(forwarded.GetProperty("contentBytes").GetString()!)));
    }

    private sealed class GraphSendFixture(HttpStatusCode response) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return response == HttpStatusCode.Accepted
                ? new HttpResponseMessage(response)
                : new HttpResponseMessage(response)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"synthetic\"}}",
                        Encoding.UTF8, "application/json")
                };
        }
    }

    private sealed class GraphReadAllowedSendDeniedFixture : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public int MimeFetches { get; private set; }
        public int SendAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.EndsWith("/sendMail", StringComparison.Ordinal))
            {
                SendAttempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"Authorization_RequestDenied\"}}",
                        Encoding.UTF8, "application/json")
                });
            }
            if (path.EndsWith("/$value", StringComparison.Ordinal))
            {
                MimeFetches++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "From: requester@tenant-a.example.test\r\nTo: support@tenant-a.example.test\r\nSubject: Request\r\n\r\nPlease help\r\n",
                        Encoding.UTF8, "message/rfc822")
                });
            }
            if (path.EndsWith("/mailFolders/inbox", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"inbox\"}", Encoding.UTF8, "application/json")
                });
            if (path.Contains("/messages/delta", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"value":[{"id":"graph-item-1","isRead":false}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/support%40tenant-a.example.test/mailFolders/inbox/messages/delta?cursor=complete"}""",
                        Encoding.UTF8, "application/json")
                });
            throw new InvalidOperationException($"Unexpected Graph fixture request: {request.Method} {request.RequestUri}");
        }
    }
}
