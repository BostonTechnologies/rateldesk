namespace Helpdesk.Application.Services.Email;

public class EmailAttachmentData
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] ContentBytes { get; set; } = Array.Empty<byte>();
    public string? ContentId { get; set; }
    public bool IsInline { get; set; }
}
