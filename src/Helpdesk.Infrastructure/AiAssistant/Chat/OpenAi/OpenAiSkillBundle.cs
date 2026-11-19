using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed record OpenAiSkillBundle(string Sha256, AgentPlugin Plugin)
{
    internal const int MaxArchiveBytes = 1024 * 1024;
    private const long MaxExpandedBytes = 4 * 1024 * 1024;

    public static OpenAiSkillBundle? Load(IConfiguration configuration, string? reference)
    {
        if (reference is null) return null;
        var section = configuration.GetSection($"AiProviders:SkillBundles:{reference}");
        var path = section["Path"];
        var expectedHash = section["Sha256"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || expectedHash is not { Length: 64 })
            throw new ArgumentException("The selected skill bundle needs a deployment-managed ZIP path and SHA-256.");
        try
        {
            using var source = File.OpenRead(path);
            if (source.Length is <= 0 or > MaxArchiveBytes)
                throw new ArgumentException("The skill bundle exceeds the compressed size limit.");
            var bytes = new byte[(int)source.Length];
            source.ReadExactly(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The deployed skill bundle differs from its configured hash.");
            return ReadArchive(bytes);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new ArgumentException("The versioned skill bundle could not be read.");
        }
    }

    public static OpenAiSkillBundle ReadArchive(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaxArchiveBytes)
            throw new ArgumentException("The skill bundle exceeds the compressed size limit.");
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            return new(Convert.ToHexString(SHA256.HashData(bytes)), ReadPlugin(archive, bytes));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            throw new ArgumentException("The versioned skill bundle could not be read.");
        }
    }

    private static AgentPlugin ReadPlugin(ZipArchive archive, byte[] bytes)
    {
        if (archive.Entries.Count is 0 or > 128 || archive.Entries.Sum(entry => entry.Length) > MaxExpandedBytes ||
            archive.Entries.Any(entry => !SafeEntry(entry)) ||
            archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new ArgumentException("The skill bundle contains unsafe paths or exceeds the expanded limits.");

        var manifests = archive.Entries.Where(entry =>
            entry.FullName.EndsWith("/.codex-plugin/plugin.json", StringComparison.Ordinal)).ToArray();
        if (manifests.Length != 1)
            throw new ArgumentException("Package one plugin with a .codex-plugin/plugin.json manifest.");
        var manifest = manifests[0];
        var root = manifest.FullName[..^".codex-plugin/plugin.json".Length];
        if (root.Count(c => c == '/') != 1 || manifest.Length > 16384 ||
            archive.Entries.Any(entry => !IsPluginEntry(entry.FullName, root)))
            throw new ArgumentException("Package the manifest and skills under one plugin directory.");

        using var stream = manifest.Open();
        using var document = JsonDocument.Parse(stream);
        var element = document.RootElement;
        var name = GetString(element, "name");
        var description = GetString(element, "description");
        var version = GetString(element, "version");
        var skills = GetString(element, "skills") ?? "./skills/";
        if (!OpenAiChatProfiles.Identifier(name, 64) || string.IsNullOrWhiteSpace(description) || description.Length > 500 ||
            !OpenAiChatProfiles.Identifier(version, 64) || skills != "./skills/" ||
            element.TryGetProperty("mcpServers", out _) || element.TryGetProperty("hooks", out _) ||
            !archive.Entries.Any(entry => entry.FullName.StartsWith(root + "skills/", StringComparison.Ordinal) &&
                entry.FullName.EndsWith("/SKILL.md", StringComparison.Ordinal)))
            throw new ArgumentException("The bundle must declare a named version and contain at least one local skill.");
        return new("inline", name!, description, new("base64", "application/zip", Convert.ToBase64String(bytes)));
    }

    private static bool IsPluginEntry(string path, string root) =>
        path == root || path == root + ".codex-plugin/" || path == root + ".codex-plugin/plugin.json" ||
        path.StartsWith(root + "skills/", StringComparison.Ordinal);

    private static bool SafeEntry(ZipArchiveEntry entry)
    {
        var path = entry.FullName;
        return path.Length is > 0 and <= 240 && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
            path.TrimEnd('/').Split('/').All(segment => segment is not ("" or "." or "..")) &&
            (entry.ExternalAttributes >> 16 & 0xF000) != 0xA000;
    }

    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
