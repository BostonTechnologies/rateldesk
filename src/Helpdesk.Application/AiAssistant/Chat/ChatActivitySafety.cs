using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Application.AiAssistant.Chat;

public static partial class ChatActivitySafety
{
    public static ChatActivityMetadata Extract(string? toolName, string? argumentsJson, bool completed,
        bool failed, long? durationMs)
    {
        var name = SafeName(toolName) ?? "tool";
        var kind = "core";
        string? provider = null, skill = null;
        if (name == "skill_load")
        {
            kind = "skill";
            skill = ArgumentName(argumentsJson, allowCanonical: false);
            name = skill ?? "Load skill";
        }
        else if (name is "load_tool" or "search_tools")
        {
            kind = "discovery";
            name = name == "search_tools" ? "Discover tools" : ArgumentName(argumentsJson, allowCanonical: true) ?? "Load tool";
        }
        else if (name.Contains('/'))
        {
            // AiAssistant McpToolAdapter.Name is the canonical server/tool pair.
            // Do not reverse LLM aliases: that needs registry knowledge.
            var parts = name.Split('/');
            provider = parts[0]; name = parts[1]; kind = "mcp";
        }
        return new(name, kind, provider, skill, durationMs is >= 0 ? durationMs : null,
            failed ? "failed" : completed ? "completed" : "running");
    }

    private static string? ArgumentName(string? json, bool allowCanonical)
    {
        if (json is null || json.Length > 32768) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var matches = document.RootElement.EnumerateObject().Where(x => string.Equals(x.Name, "name", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Value.ValueKind != JsonValueKind.String) return null;
            var name = SafeName(matches[0].Value.GetString());
            return !allowCanonical && name?.Contains('/') == true ? null : name;
        }
        catch (JsonException) { return null; }
    }

    private static string? SafeName(string? name) => name is { Length: > 0 and <= 128 }
        && NamePattern().IsMatch(name) ? name : null;

    [GeneratedRegex("\\A[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)?\\z")]
    private static partial Regex NamePattern();
}
