using System.Net;
using System.Text;
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
            "INC-123 update", "<p>Hello</p>", [], default);

        Assert.True(expectedStatus == result.Status, $"Expected {expectedStatus}; got {result.Status}/{result.ErrorCode}.");
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal("/v1.0/users/support%40tenant-a.example.test/sendMail", transport.Path);
        Assert.Contains("support@tenant-a.example.test", transport.Body);
        Assert.Contains("requester@example.test", transport.Body);
        Assert.Contains("tech@example.test", transport.Body);
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
