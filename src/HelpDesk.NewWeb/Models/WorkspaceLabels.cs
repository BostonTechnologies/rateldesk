using System.Text.RegularExpressions;

namespace HelpDesk.NewWeb.Models;

public static partial class WorkspaceLabels
{
    /// <summary>Readable fallback without inventing meaning for an unknown internal key.</summary>
    public static string Readable(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" :
        WordBoundary().Replace(value.Replace('_', ' ').Replace('.', ' '), "$1 $2").Trim();

    public static string Source(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "Unknown source";
        var category = source.Split([',', ';', '|'], 2)[0].Trim();
        return Readable(category.Split('.').Last());
    }

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex WordBoundary();
}
