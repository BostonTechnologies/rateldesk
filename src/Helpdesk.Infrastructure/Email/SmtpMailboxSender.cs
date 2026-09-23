using Helpdesk.Application.Services.Email;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Shared.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Net.Sockets;

namespace Helpdesk.Infrastructure.Email;

public sealed record MailboxSubmissionResult(string Status, string? ErrorCode = null,
    IReadOnlyList<string>? AcceptedRecipients = null, IReadOnlyList<string>? RejectedRecipients = null);

public sealed class SmtpMailboxSender(MailboxDestinationPolicy destinations,
    MailboxOutgoingCredentialProtector secrets, IHtmlToPlainTextConverter textConverter)
{
    public async Task<MailboxSubmissionResult> TestAsync(MailboxOutgoingSettings outgoing, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var client = await ConnectAsync(outgoing, timeout.Token);
            await client.DisconnectAsync(true, timeout.Token);
            return new("Authenticated");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return new("Failed", SafeCode(error)); }
    }

    public async Task<MailboxSubmissionResult> SendAsync(EmailInboxSettings mailbox,
        MailboxOutgoingSettings outgoing, IEnumerable<string> recipients, IEnumerable<string>? cc,
        string subject, string html, IEnumerable<EmailAttachmentData>? attachments,
        Guid deliveryId, CancellationToken ct)
    {
        if (!outgoing.Enabled || mailbox.Archived)
            return new("Needs configuration", "OutgoingDisabled");
        var to = EmailAddressGuard.NormalizeRecipients(recipients, mailbox.MailboxAddress);
        var copy = EmailAddressGuard.NormalizeRecipients(cc, mailbox.MailboxAddress);
        if (!EmailAddressGuard.IsSingleAddress(mailbox.MailboxAddress) ||
            to.Concat(copy).Any(address => !EmailAddressGuard.IsSingleAddress(address)))
            return new("Failed", "InvalidMailboxAddress");
        if (to.Count == 0 && copy.Count == 0)
            return new("Suppressed", "SelfRecipientOrEmpty");
        if (to.Count + copy.Count > 100 || subject.Length > 998 || html.Length > 10 * 1024 * 1024)
            return new("Failed", "MessageLimitExceeded");
        var files = attachments?.ToArray() ?? [];
        if (files.Length > 25 || files.Sum(file => (long)file.ContentBytes.Length) > 16 * 1024 * 1024)
            return new("Failed", "AttachmentLimitExceeded");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(outgoing.DisplayName, mailbox.MailboxAddress));
        message.ReplyTo.Add(new MailboxAddress(outgoing.DisplayName, mailbox.MailboxAddress));
        foreach (var address in to) message.To.Add(MailboxAddress.Parse(address));
        foreach (var address in copy) message.Cc.Add(MailboxAddress.Parse(address));
        message.Subject = subject;
        message.MessageId = $"{deliveryId:N}@{mailbox.MailboxAddress.Split('@')[1]}";
        message.Headers.Add("Auto-Submitted", "auto-generated");
        var body = new BodyBuilder { HtmlBody = html, TextBody = textConverter.Convert(html) };
        foreach (var file in files)
        {
            if (file.ContentBytes.Length == 0 || string.IsNullOrWhiteSpace(file.FileName)) continue;
            var contentType = ContentType.TryParse(file.ContentType, out var parsed)
                ? parsed : new ContentType("application", "octet-stream");
            body.Attachments.Add(Path.GetFileName(file.FileName), file.ContentBytes, contentType);
        }
        message.Body = body.ToMessageBody();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var client = await ConnectAsync(outgoing, timeout.Token);
            try
            {
                await client.SendAsync(message, timeout.Token);
                await client.DisconnectAsync(true, timeout.Token);
                return client.RejectedRecipients.Count == 0
                    ? new("Accepted by provider", null, [.. client.AcceptedRecipients])
                    : new("Needs review", "SmtpPartialRecipientAcceptance",
                        [.. client.AcceptedRecipients], [.. client.RejectedRecipients]);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (SmtpCommandException error) when (error.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted)
            {
                return new("Failed", error.ErrorCode == SmtpErrorCode.SenderNotAccepted
                    ? "SmtpSenderRejected" : "SmtpRecipientRejected",
                    [.. client.AcceptedRecipients], [.. client.RejectedRecipients]);
            }
            catch (Exception error)
            {
                // Once submission has begun, a lost response can mean the server accepted
                // the message. Do not automatically resubmit this logical delivery.
                return new("Outcome unknown", SafeCode(error));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return new("Failed", SafeCode(error)); }
    }

    private async Task<RecipientTrackingSmtpClient> ConnectAsync(MailboxOutgoingSettings outgoing, CancellationToken ct)
    {
        var client = new RecipientTrackingSmtpClient { Timeout = 20000 };
        Socket? socket = null;
        try
        {
            var tls = outgoing.SmtpTlsMode switch
            {
                MailboxTlsMode.TlsOnConnect => SecureSocketOptions.SslOnConnect,
                MailboxTlsMode.StartTls => SecureSocketOptions.StartTls,
                _ => throw new InvalidOperationException("Verified TLS is required.")
            };
            socket = await destinations.ConnectSmtpAsync(outgoing.SmtpHost, outgoing.SmtpPort, ct);
            await client.ConnectAsync(socket, outgoing.SmtpHost, outgoing.SmtpPort, tls, ct);
            if (!client.IsSecure) throw new InvalidOperationException("Verified TLS is required.");
            await client.AuthenticateAsync(outgoing.SmtpUsername, secrets.Unprotect(outgoing), ct);
            return client;
        }
        catch
        {
            client.Dispose();
            socket?.Dispose();
            throw;
        }
    }

    private static string SafeCode(Exception error) => error switch
    {
        AuthenticationException => "SmtpAuthenticationFailed",
        SmtpCommandException => "SmtpCommandRejected",
        SslHandshakeException => "SmtpTlsFailed",
        OperationCanceledException => "SmtpTimedOut",
        _ => error.GetType().Name
    };

    private sealed class RecipientTrackingSmtpClient : SmtpClient
    {
        public List<string> AcceptedRecipients { get; } = [];
        public List<string> RejectedRecipients { get; } = [];

        protected override void OnRecipientAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
        {
            AcceptedRecipients.Add(mailbox.Address);
            base.OnRecipientAccepted(message, mailbox, response);
        }

        protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
        {
            RejectedRecipients.Add(mailbox.Address);
        }

        protected override void OnNoRecipientsAccepted(MimeMessage message) =>
            throw new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted,
                SmtpStatusCode.MailboxUnavailable, "No recipients were accepted by the SMTP server.");
    }
}
