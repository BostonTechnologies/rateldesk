using System.Net;
using System.Text;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Graph;
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

    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/delta")]
    [InlineData("https://attacker.example.test/delta")]
    [InlineData("https://graph.microsoft.com:8443/delta")]
    [InlineData("https://user@graph.microsoft.com/delta")]
    public void Checkpoints_cannot_redirect_credentials_to_another_destination(string cursor) =>
        Assert.Throws<InvalidDataException>(() => GraphMailboxAdapter.ValidateCursor(cursor));

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
