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
        foreach (var part in EnumerateAttachments(message.Body))
        {
            if (attachments.Count >= MaxAttachments) throw new InvalidDataException("AttachmentCountExceeded");
            using var stream = new MemoryStream();
            string name;
            string contentType;
            string? contentId;
            bool isInline;
            switch (part)
            {
                case MimePart mimePart:
                    await mimePart.Content.DecodeToAsync(stream, ct);
                    name = SafeFileName(mimePart.FileName, "attachment");
                    contentType = mimePart.ContentType.MimeType;
                    contentId = mimePart.ContentId;
                    isInline = !mimePart.IsAttachment;
                    break;
                case MessagePart messagePart when messagePart.Message is not null:
                    await messagePart.Message.WriteToAsync(stream, ct);
                    name = SafeFileName(messagePart.ContentDisposition?.FileName ?? messagePart.ContentType.Name,
                        "attached-message.eml");
                    if (!name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)) name += ".eml";
                    contentType = "message/rfc822";
                    contentId = messagePart.ContentId;
                    isInline = false;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported MIME attachment type: {part.GetType().Name}");
            }
            total += stream.Length;
            if (total > MaxMessageBytes) throw new InvalidDataException("MessageSizeExceeded");
            attachments.Add(new(name, contentType, contentId, stream.ToArray(),
                attachments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), isInline));
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

    private static IEnumerable<MimeEntity> EnumerateAttachments(MimeEntity? entity)
    {
        switch (entity)
        {
            case null:
                yield break;
            case MessagePart messagePart:
                // Preserve an attached message as one opaque .eml. Its nested parts belong
                // to that file and must not also appear as attachments of the outer message.
                yield return messagePart;
                yield break;
            case Multipart multipart:
                foreach (var child in multipart)
                foreach (var attachment in EnumerateAttachments(child))
                    yield return attachment;
                yield break;
            case MimePart mimePart when mimePart.IsAttachment || mimePart.ContentId is not null:
                yield return mimePart;
                yield break;
            case MimePart:
                yield break;
            default:
                throw new InvalidDataException($"Unsupported MIME entity type: {entity.GetType().Name}");
        }
    }

    private static string SafeFileName(string? supplied, string fallback)
    {
        var normalized = supplied?.Replace('\\', '/');
        var name = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? fallback : name;
    }
}
