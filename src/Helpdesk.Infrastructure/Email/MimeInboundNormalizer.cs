using System.Text.Encodings.Web;
using Helpdesk.Application.Services.Email;
using Helpdesk.Shared.Models;
using MimeKit;

namespace Helpdesk.Infrastructure.Email;

public static class MimeInboundNormalizer
{
    public const int MaxMessageBytes = 25 * 1024 * 1024;
    public const int MaxAttachments = 50;

    public static async Task<InboundSourceMessage> ReadAsync(Stream raw, EmailInboxSettings settings, string key, CancellationToken ct)
    {
        try
        {
            using var message = await MimeMessage.LoadAsync(new ParserOptions { MaxMimeDepth = 32 }, raw, ct);
            return new(key, await NormalizeAsync(message, settings, key, ct));
        }
        catch (Exception error) when (error is FormatException or InvalidDataException)
        {
            return new(key, null, HoldReason: "InvalidOrOversizedMime");
        }
    }

    public static async Task<InboundEmailContext> NormalizeAsync(MimeMessage message, EmailInboxSettings settings, string key, CancellationToken ct)
    {
        var attachments = new List<InboundEmailAttachmentContext>();
        long total = 0;
        foreach (var part in message.BodyParts.OfType<MimePart>().Where(p => p.IsAttachment || p.ContentId is not null))
        {
            if (attachments.Count >= MaxAttachments) throw new InvalidDataException("AttachmentCountExceeded");
            using var stream = new MemoryStream();
            await part.Content.DecodeToAsync(stream, ct);
            total += stream.Length;
            if (total > MaxMessageBytes) throw new InvalidDataException("MessageSizeExceeded");
            attachments.Add(new(Path.GetFileName(part.FileName ?? "attachment"), part.ContentType.MimeType,
                part.ContentId, stream.ToArray(), attachments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), !part.IsAttachment));
        }
        var senders = message.From.Mailboxes.Take(2).ToArray();
        if (senders.Length != 1) throw new InvalidDataException("AmbiguousSender");
        var from = senders[0];
        var repeated = message.Headers.GroupBy(h => h.Field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(h => h.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
        return new InboundEmailContext(message.MessageId ?? string.Empty, null, settings.Id, settings.OrganizationId,
            settings.MailboxAddress, from?.Address ?? string.Empty, from?.Name,
            message.To.Mailboxes.Select(x => x.Address).ToArray(), message.Cc.Mailboxes.Select(x => x.Address).ToArray(),
            message.Subject ?? string.Empty, message.HtmlBody ?? HtmlEncoder.Default.Encode(message.TextBody ?? string.Empty),
            message.TextBody ?? string.Empty, message.Date,
            repeated.ToDictionary(x => x.Key, x => string.Join("\n", x.Value), StringComparer.OrdinalIgnoreCase), attachments)
        {
            RepeatedHeaders = repeated, InReplyTo = message.InReplyTo, References = message.References.ToArray(), SourceMessageKey = key
        };
    }
}
