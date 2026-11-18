using System.Text.RegularExpressions;
using Helpdesk.Application.AiAssistant.Chat;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed class OpenAiTextDrafts
{
    private const int MaximumLength = 128000;
    private readonly Dictionary<(string Id, int Part), string> parts = new();
    private string? current;

    public string? Apply(ProviderStreamEvent update)
    {
        if (update.ItemId is not { Length: <= 512 } id || update.Text is null || update.ContentIndex is < 0 or > 31) return null;
        if (current != id)
        {
            current = id;
            parts.Clear();
        }
        var key = (id, update.ContentIndex);
        var text = update.Kind == ProviderStreamEventKind.TextDelta
            ? parts.GetValueOrDefault(key) + update.Text : update.Text;
        parts[key] = text[..Math.Min(text.Length, MaximumLength)];
        return OpenAiChatRun.ProviderText(string.Join("\n", parts.OrderBy(pair => pair.Key.Part).Select(pair => pair.Value)), MaximumLength);
    }
}

internal static partial class OpenAiCredentialRedaction
{
    public static string Redact(string text) => Token().Replace(text, "[redacted credential]");

    [GeneratedRegex(@"\b(?:rdk_[a-fA-F0-9]{32}_[a-fA-F0-9]{64}|sk-[A-Za-z0-9_-]{16,})\b")]
    private static partial Regex Token();
}
