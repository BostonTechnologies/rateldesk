using System.Net;
using System.Text;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class GraphMailboxAdapterTests
{
    [Fact]
    public async Task Graph_delta_pages_fetch_complete_MIME_without_IMAP_and_preserve_case_and_repeated_headers()
    {
        var transport = new GraphTransport();
        var adapter = new GraphMailboxAdapter(new MailboxCredentialProtector(new EphemeralDataProtectionProvider()),
            _ => new GraphServiceClient(new HttpClient(transport, disposeHandler: false), new AnonymousAuthenticationProvider()));
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "support@example.test", MailboxFolder = "inbox", InitialImport = InitialMailImport.All };
        var state = new MailboxIngestionState { MailboxId = mailbox.Id };
        var first = await adapter.FetchAsync(mailbox, state, new HashSet<string>(), default);
        var source = Assert.Single(first.Messages);
        Assert.Equal("AbC-Case", source.Key);
        Assert.Equal(mailbox.Id, source.Message!.MailboxId);
        Assert.Equal("requester@example.test", source.Message.FromEmail);
        Assert.Equal(2, source.Message.RepeatedHeaders["Received"].Count);
        Assert.Equal("example.txt", Assert.Single(source.Message.Attachments).Name);
        Assert.False(first.InitializationComplete);
        state.Cursor = first.NextCursor;
        var second = await adapter.FetchAsync(mailbox, state, new HashSet<string> { source.Key }, default);
        Assert.Empty(second.Messages);
        Assert.True(second.InitializationComplete);
        await adapter.AcknowledgeAsync(mailbox, source.Key, default);
        Assert.All(transport.Preferences, value => Assert.Contains("IdType=\"ImmutableId\"", value));
        Assert.Equal(1, transport.MimeFetches);
        Assert.Contains(transport.Paths, path => path.Contains("AbC-Case"));
    }

    [Fact]
    public async Task Expired_delta_cursor_rescans_without_reprocessing_durable_message_keys()
    {
        var transport = new GraphTransport();
        var adapter = new GraphMailboxAdapter(new MailboxCredentialProtector(new EphemeralDataProtectionProvider()),
            _ => new GraphServiceClient(new HttpClient(transport, disposeHandler: false), new AnonymousAuthenticationProvider()));
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "support@example.test", MailboxFolder = "inbox" };
        var result = await adapter.FetchAsync(mailbox,
            new MailboxIngestionState { Cursor = "https://graph.microsoft.com/v1.0/delta?expired=true", Initialized = true },
            new HashSet<string> { "AbC-Case" }, default);
        Assert.Empty(result.Messages);
        Assert.Equal(0, transport.MimeFetches);
        Assert.Equal(2, transport.Paths.Count);
        Assert.Contains("page=2", result.NextCursor);
    }

    [Fact]
    public async Task Missing_mime_item_becomes_tombstone_and_valid_neighbor_reaches_safe_checkpoint()
    {
        var transport = new PartialPageTransport(HttpStatusCode.NotFound);
        var adapter = CreateAdapter(transport);
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), MailboxAddress = "support@example.test", MailboxFolder = "inbox",
            InitialImport = InitialMailImport.All
        };

        var batch = await adapter.FetchAsync(mailbox, new MailboxIngestionState { MailboxId = mailbox.Id },
            new HashSet<string>(), default);

        Assert.Equal(2, batch.Messages.Count);
        var missing = Assert.Single(batch.Messages, message => message.Key == "missing-id");
        Assert.Null(missing.Message);
        Assert.Equal("SourceMessageMissing", missing.HoldReason);
        Assert.True(missing.Ignore);
        Assert.NotNull(Assert.Single(batch.Messages, message => message.Key == "valid-id").Message);
        Assert.Equal("https://graph.microsoft.com/v1.0/delta?cursor=complete", batch.NextCursor);
    }

    [Fact]
    public async Task Transient_mime_failure_does_not_return_an_advanced_checkpoint()
    {
        var transport = new PartialPageTransport(HttpStatusCode.ServiceUnavailable);
        var adapter = CreateAdapter(transport);
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), MailboxAddress = "support@example.test", MailboxFolder = "inbox",
            InitialImport = InitialMailImport.All
        };
        var state = new MailboxIngestionState { MailboxId = mailbox.Id, Cursor = "https://graph.microsoft.com/v1.0/delta?cursor=before" };

        await Assert.ThrowsAnyAsync<Exception>(() => adapter.FetchAsync(mailbox, state, new HashSet<string>(), default));

        Assert.Equal("https://graph.microsoft.com/v1.0/delta?cursor=before", state.Cursor);
    }

    [Fact]
    public async Task Graph_ingestion_preserves_attached_message_as_eml()
    {
        var adapter = CreateAdapter(new AttachedMessageTransport());
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), MailboxAddress = "support@example.test", MailboxFolder = "inbox",
            InitialImport = InitialMailImport.All
        };

        var batch = await adapter.FetchAsync(mailbox, new MailboxIngestionState(), new HashSet<string>(), default);

        var attachment = Assert.Single(Assert.Single(batch.Messages).Message!.Attachments);
        Assert.Equal("graph-nested.eml", attachment.Name);
        Assert.Contains("Nested Graph body", Encoding.UTF8.GetString(attachment.ContentBytes!));
    }

    [Fact]
    public async Task Move_not_found_with_available_source_remains_retryable()
    {
        var transport = new MoveNotFoundTransport(HttpStatusCode.OK);
        var adapter = CreateAdapter(transport);
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), MailboxAddress = "support@example.test", MailboxFolder = "inbox",
            MarkReadAfterSuccess = true, ProcessedFolder = "missing-destination"
        };

        var error = await Assert.ThrowsAnyAsync<ApiException>(() =>
            adapter.AcknowledgeAsync(mailbox, "source-id", default));

        Assert.Equal(404, error.ResponseStatusCode);
        Assert.True(transport.MarkReadAttempted);
        Assert.True(transport.MoveAttempted);
        Assert.True(transport.SourceProbeAttempted);
    }

    [Fact]
    public async Task Move_not_found_with_definitively_missing_source_is_terminal()
    {
        var transport = new MoveNotFoundTransport(HttpStatusCode.NotFound);
        var adapter = CreateAdapter(transport);
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), MailboxAddress = "support@example.test", MailboxFolder = "inbox",
            MarkReadAfterSuccess = true, ProcessedFolder = "processed"
        };

        await Assert.ThrowsAsync<InboundSourceMissingException>(() =>
            adapter.AcknowledgeAsync(mailbox, "source-id", default));

        Assert.True(transport.MarkReadAttempted);
        Assert.True(transport.MoveAttempted);
        Assert.True(transport.SourceProbeAttempted);
    }

    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/delta")]
    [InlineData("https://attacker.example.test/delta")]
    [InlineData("https://graph.microsoft.com:8443/delta")]
    [InlineData("https://user@graph.microsoft.com/delta")]
    public void Checkpoints_cannot_redirect_credentials_to_another_destination(string cursor) =>
        Assert.Throws<InvalidDataException>(() => GraphMailboxAdapter.ValidateCursor(cursor));

    private static GraphMailboxAdapter CreateAdapter(HttpMessageHandler transport) => new(
        new MailboxCredentialProtector(new EphemeralDataProtectionProvider()),
        _ => new GraphServiceClient(new HttpClient(transport, disposeHandler: false), new AnonymousAuthenticationProvider()));

    private sealed class PartialPageTransport(HttpStatusCode missingStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.ToString();
            if (path.Contains("missing-id") && path.Contains("$value"))
                return Task.FromResult(new HttpResponseMessage(missingStatus)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"synthetic\"}}", Encoding.UTF8, "application/json")
                });
            if (path.Contains("valid-id") && path.Contains("$value"))
            {
                var mime = File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "tests", "Fixtures", "inbound", "multipart-request.eml"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(mime, Encoding.UTF8, "message/rfc822")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"value":[{"id":"missing-id","isRead":false},{"id":"valid-id","isRead":false}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta?cursor=complete"}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AttachedMessageTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.ToString().Contains("$value"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "From: outer@example.test\r\nTo: support@example.test\r\nSubject: Outer\r\nContent-Type: multipart/mixed; boundary=outer\r\n\r\n--outer\r\nContent-Type: text/plain\r\n\r\nOuter Graph body\r\n--outer\r\nContent-Type: message/rfc822; name=graph-nested.eml\r\nContent-Disposition: attachment; filename=graph-nested.eml\r\n\r\nFrom: nested@example.test\r\nTo: support@example.test\r\nSubject: Nested Graph request\r\nContent-Type: text/plain\r\n\r\nNested Graph body\r\n--outer--\r\n",
                        Encoding.UTF8, "message/rfc822")
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"value":[{"id":"attached-id","isRead":false}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta?cursor=attached"}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MoveNotFoundTransport(HttpStatusCode sourceProbeStatus) : HttpMessageHandler
    {
        public bool MarkReadAttempted { get; private set; }
        public bool MoveAttempted { get; private set; }
        public bool SourceProbeAttempted { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Patch)
            {
                MarkReadAttempted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (path.EndsWith("/move", StringComparison.Ordinal))
            {
                MoveAttempted = true;
                return Task.FromResult(Error(HttpStatusCode.NotFound, "ErrorItemNotFound"));
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/messages/source-id", StringComparison.Ordinal))
            {
                SourceProbeAttempted = true;
                return Task.FromResult(sourceProbeStatus == HttpStatusCode.OK
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"source-id\"}", Encoding.UTF8, "application/json")
                    }
                    : Error(sourceProbeStatus, "ErrorItemNotFound"));
            }
            throw new InvalidOperationException($"Unexpected Graph request: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\"}}}}", Encoding.UTF8, "application/json")
        };
    }

    private sealed class GraphTransport : HttpMessageHandler
    {
        public List<string> Preferences { get; } = [];
        public List<string> Paths { get; } = [];
        public int MimeFetches { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Preferences.Add(string.Join(",", request.Headers.GetValues("Prefer")));
            var path = request.RequestUri!.ToString(); Paths.Add(path);
            if (path.Contains("expired=true")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)
                { Content = new StringContent("{\"error\":{\"code\":\"syncStateNotFound\"}}", Encoding.UTF8, "application/json") });
            if (request.Method == HttpMethod.Patch) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (path.Contains("$value"))
            {
                MimeFetches++;
                var mime = File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "tests", "Fixtures", "inbound", "multipart-request.eml"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(mime, Encoding.UTF8, "message/rfc822") });
            }
            var json = path.Contains("page=2")
                ? """{"value":[{"id":"AbC-Case","isRead":true}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta?cursor=done"}"""
                : """{"value":[{"id":"AbC-Case","isRead":false}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/delta?page=2"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
