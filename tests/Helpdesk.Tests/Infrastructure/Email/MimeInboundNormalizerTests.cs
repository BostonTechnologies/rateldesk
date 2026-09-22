using System.Text;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.Models;
using MimeKit;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class MimeInboundNormalizerTests
{
    [Fact]
    public async Task Attached_message_is_preserved_as_complete_eml_without_changing_outer_body()
    {
        using var nested = CreateNestedMessage();
        using var outer = CreateOuterMessage(new MessagePart("rfc822")
        {
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "request.eml" },
            Message = nested
        });

        var normalized = await MimeInboundNormalizer.NormalizeAsync(outer, Settings(), "source-1", default);

        Assert.Equal("Outer body", normalized.TextBody);
        var attachment = Assert.Single(normalized.Attachments);
        Assert.Equal("request.eml", attachment.Name);
        Assert.Equal("message/rfc822", attachment.ContentType);
        Assert.NotNull(attachment.ContentBytes);
        var eml = Encoding.UTF8.GetString(attachment.ContentBytes!);
        Assert.Contains("Subject: Nested request", eml);
        Assert.Contains("Nested body", eml);
        Assert.Contains("nested evidence", eml);
    }

    [Theory]
    [InlineData("../unsafe.eml", "unsafe.eml")]
    [InlineData(null, "attached-message.eml")]
    public async Task Attached_message_uses_safe_filename(string? supplied, string expected)
    {
        using var nested = CreateNestedMessage();
        using var part = new MessagePart("rfc822") { Message = nested };
        if (supplied is not null)
            part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = supplied };
        else
            part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment);
        using var outer = CreateOuterMessage(part);

        var attachment = Assert.Single((await MimeInboundNormalizer.NormalizeAsync(outer, Settings(), "source-1", default)).Attachments);

        Assert.Equal(expected, attachment.Name);
    }

    [Fact]
    public async Task Attached_messages_count_toward_attachment_limit()
    {
        using var outer = CreateOuterMessage(Enumerable.Range(0, MimeInboundNormalizer.MaxAttachments + 1)
            .Select(index => (MimeEntity)new MessagePart("rfc822")
            {
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = $"nested-{index}.eml" },
                Message = CreateNestedMessage()
            }).ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MimeInboundNormalizer.NormalizeAsync(outer, Settings(), "source-1", default));
    }

    [Fact]
    public async Task Large_attached_message_is_rejected_by_shared_byte_limit()
    {
        using var nested = CreateNestedMessage(new string('x', MimeInboundNormalizer.MaxMessageBytes + 1));
        using var outer = CreateOuterMessage(new MessagePart("rfc822")
        {
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "large.eml" },
            Message = nested
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MimeInboundNormalizer.NormalizeAsync(outer, Settings(), "source-1", default));
    }

    private static MimeMessage CreateNestedMessage(string body = "Nested body")
    {
        var message = new MimeMessage
        {
            Subject = "Nested request",
            Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = body },
                new MimePart("text", "plain")
                {
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "evidence.txt" },
                    Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes("nested evidence")))
                }
            }
        };
        message.From.Add(MailboxAddress.Parse("nested@example.test"));
        message.To.Add(MailboxAddress.Parse("support@example.test"));
        message.Headers.Add("X-Nested-Proof", "retained");
        return message;
    }

    private static MimeMessage CreateOuterMessage(params MimeEntity[] attachments)
    {
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "Outer body" } };
        foreach (var attachment in attachments) mixed.Add(attachment);
        var message = new MimeMessage { Subject = "Outer request", Body = mixed };
        message.From.Add(MailboxAddress.Parse("outer@example.test"));
        message.To.Add(MailboxAddress.Parse("support@example.test"));
        return message;
    }

    private static EmailInboxSettings Settings() => new()
    {
        Id = Guid.NewGuid(), MailboxAddress = "support@example.test"
    };
}
