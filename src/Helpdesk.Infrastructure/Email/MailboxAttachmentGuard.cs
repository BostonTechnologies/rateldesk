namespace Helpdesk.Infrastructure.Email;

internal static class MailboxAttachmentGuard
{
    public static string NormalizeContentId(string contentId) => contentId.Trim('<', '>');

    public static bool IsValidContentId(string? contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId)) return false;
        var value = NormalizeContentId(contentId);
        return value.Length is > 0 and <= 255 &&
            value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character) &&
                character is not '<' and not '>');
    }
}
