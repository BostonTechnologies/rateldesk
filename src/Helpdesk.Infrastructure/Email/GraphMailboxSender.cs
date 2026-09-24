using Azure.Identity;
using Helpdesk.Application.Services.Email;
using Helpdesk.Shared.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Graph submission for the selected logical mailbox, independent of legacy ExchangeEmail options.</summary>
public sealed class GraphMailboxSender
{
    private readonly Func<EmailInboxSettings, GraphServiceClient> clientFactory;

    public GraphMailboxSender(MailboxCredentialProtector secrets)
        : this(mailbox => new GraphServiceClient(new ClientSecretCredential(mailbox.TenantId,
            mailbox.ClientId, secrets.Unprotect(mailbox, mailbox.ClientSecret)))) { }

    internal GraphMailboxSender(Func<EmailInboxSettings, GraphServiceClient> clientFactory) =>
        this.clientFactory = clientFactory;

    public async Task<MailboxSubmissionResult> SendAsync(EmailInboxSettings mailbox,
        MailboxOutgoingSettings outgoing, IEnumerable<string> recipients, IEnumerable<string>? cc,
        string subject, string html, IEnumerable<EmailAttachmentData>? attachments,
        CancellationToken ct)
    {
        if (!outgoing.Enabled || mailbox.Archived)
            return new("Needs configuration", "OutgoingDisabled");
        if (mailbox.Authentication != MailboxAuthentication.MicrosoftApplication || mailbox.ClientSecret.Length == 0)
            return new("Needs configuration", "GraphCredentialMissing");
        var to = EmailAddressGuard.NormalizeRecipients(recipients, mailbox.MailboxAddress);
        var copy = EmailAddressGuard.NormalizeRecipients(cc, mailbox.MailboxAddress);
        if (!EmailAddressGuard.IsSingleAddress(mailbox.MailboxAddress) ||
            to.Concat(copy).Any(address => !EmailAddressGuard.IsSingleAddress(address)))
            return new("Failed", "InvalidMailboxAddress");
        if (to.Count == 0 && copy.Count == 0)
            return new("Suppressed", "SelfRecipientOrEmpty");
        var files = attachments?.ToArray() ?? [];
        if (to.Count + copy.Count > 100 || subject.Length > 998 || html.Length > 10 * 1024 * 1024 ||
            files.Length > 25 || files.Sum(file => (long)file.ContentBytes.Length) > 16 * 1024 * 1024)
            return new("Failed", "MessageLimitExceeded");
        if (files.Any(file => file.IsInline && !MailboxAttachmentGuard.IsValidContentId(file.ContentId)))
            return new("Failed", "InvalidInlineContentId");

        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = html },
            From = new Recipient { EmailAddress = new EmailAddress
                { Address = mailbox.MailboxAddress, Name = outgoing.DisplayName } },
            ReplyTo = [new Recipient { EmailAddress = new EmailAddress { Address = mailbox.MailboxAddress } }],
            ToRecipients = to.Select(address => new Recipient
                { EmailAddress = new EmailAddress { Address = address } }).ToList(),
            CcRecipients = copy.Select(address => new Recipient
                { EmailAddress = new EmailAddress { Address = address } }).ToList(),
            Attachments = files.Where(file => file.ContentBytes.Length > 0 && !string.IsNullOrWhiteSpace(file.FileName))
                .Select(file => (Microsoft.Graph.Models.Attachment)new FileAttachment
                {
                    Name = Path.GetFileName(file.FileName), ContentType = file.ContentType,
                    ContentBytes = file.ContentBytes, IsInline = file.IsInline,
                    ContentId = file.IsInline ? MailboxAttachmentGuard.NormalizeContentId(file.ContentId!) : null
                }).ToList()
        };

        try
        {
            var graph = clientFactory(mailbox);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await graph.Users[mailbox.MailboxAddress].SendMail.PostAsync(
                new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody
                    { Message = message, SaveToSentItems = true }, cancellationToken: timeout.Token);
            return new("Accepted by provider", null, [.. to, .. copy]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Microsoft.Kiota.Abstractions.ApiException error) when (error.ResponseStatusCode is 401 or 403)
        {
            return new("Failed", error.ResponseStatusCode == 403
                ? "GraphSendPermissionDenied" : "GraphAuthenticationFailed");
        }
        catch (Exception error) { return new("Outcome unknown", error.GetType().Name); }
    }
}
