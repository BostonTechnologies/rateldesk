using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.Models;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class GraphMailboxSenderTests
{
    [Theory]
    [InlineData(HttpStatusCode.Accepted, "Accepted by provider", null)]
    [InlineData(HttpStatusCode.Forbidden, "Failed", "GraphSendPermissionDenied")]
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
}
