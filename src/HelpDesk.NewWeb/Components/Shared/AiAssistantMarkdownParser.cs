namespace HelpDesk.NewWeb.Components.Shared;

internal static class AiAssistantMarkdownParser
{
    internal static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];
            if (TryFence(line, out var fence, out var length))
            {
                var code = new List<string>();
                index++;
                while (index < lines.Length && !ClosesFence(lines[index], fence, length)) code.Add(lines[index++]);
                // Preserve the existing code-copy contract, including the newline before a closing fence.
                var closed = index < lines.Length;
                if (closed) index++;
                blocks.Add(new(MarkdownBlockKind.Code, string.Join('\n', code) + (closed && code.Count > 0 ? "\n" : "")));
                continue;
            }
            if (TryTableHeader(lines, index, out var headers, out var alignments))
            {
                var start = index;
                var rows = new List<string[]>();
                index += 2;
                while (index < lines.Length && !IsSpecial(lines[index]) &&
                    !TryTableHeader(lines, index, out _, out _) && TryTableRow(lines[index], out var row))
                {
                    rows.Add(row);
                    index++;
                }
                blocks.Add(new(MarkdownBlockKind.Table, string.Join('\n', lines[start..index]),
                    Table: new(headers, alignments, rows)));
                continue;
            }
            var heading = HeadingLevel(line);
            if (heading > 0)
            {
                blocks.Add(new(MarkdownBlockKind.Heading, line[(heading + 1)..], heading));
                index++;
                continue;
            }
            if (Bullet(line))
            {
                blocks.Add(new(MarkdownBlockKind.Bullet, line[2..]));
                index++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                blocks.Add(new(MarkdownBlockKind.Break, ""));
                index++;
                continue;
            }
            var paragraph = new List<string> { line };
            index++;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !IsSpecial(lines[index]) &&
                !TryTableHeader(lines, index, out _, out _)) paragraph.Add(lines[index++]);
            blocks.Add(new(MarkdownBlockKind.Paragraph, string.Join('\n', paragraph)));
        }
        return blocks;
    }

    private static bool TryTableHeader(string[] lines, int index, out string[] headers, out string[] alignments)
    {
        headers = [];
        alignments = [];
        if (index + 1 >= lines.Length || !TryTableRow(lines[index], out headers) ||
            !TryTableRow(lines[index + 1], out var delimiters) || headers.Length != delimiters.Length) return false;
        alignments = new string[headers.Length];
        for (var column = 0; column < delimiters.Length; column++)
        {
            var delimiter = delimiters[column].AsSpan();
            var left = delimiter.StartsWith(":", StringComparison.Ordinal);
            var right = delimiter.EndsWith(":", StringComparison.Ordinal);
            if (left) delimiter = delimiter[1..];
            if (right && !delimiter.IsEmpty) delimiter = delimiter[..^1];
            if (delimiter.Length < 3 || delimiter.IndexOfAnyExcept('-') >= 0) return false;
            alignments[column] = right ? left ? "center" : "right" : "left";
        }
        return true;
    }

    private static bool TryTableRow(string line, out string[] cells)
    {
        var text = line.Trim();
        var values = new List<string>();
        var start = 0;
        var escaped = false;
        var hasPipe = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '|' && !escaped)
            {
                values.Add(text[start..index].Trim());
                start = index + 1;
                hasPipe = true;
            }
            escaped = text[index] == '\\' && !escaped;
        }
        values.Add(text[start..].Trim());
        if (text.StartsWith('|')) values.RemoveAt(0);
        if (start == text.Length && hasPipe) values.RemoveAt(values.Count - 1);
        cells = values.Select(value => value.Replace("\\|", "|", StringComparison.Ordinal)).ToArray();
        return hasPipe && cells.Length > 0;
    }

    private static bool TryFence(string line, out char fence, out int length)
    {
        var text = line.TrimStart();
        fence = text.Length > 0 ? text[0] : '\0';
        length = 0;
        if (fence is not ('`' or '~')) return false;
        while (length < text.Length && text[length] == fence) length++;
        return length >= 3;
    }

    private static bool ClosesFence(string line, char fence, int length) =>
        TryFence(line, out var found, out var count) && found == fence && count >= length &&
        string.IsNullOrWhiteSpace(line.TrimStart()[count..]);

    private static int HeadingLevel(string line) => line.StartsWith("### ", StringComparison.Ordinal) ? 3 :
        line.StartsWith("## ", StringComparison.Ordinal) ? 2 : line.StartsWith("# ", StringComparison.Ordinal) ? 1 : 0;
    private static bool Bullet(string line) => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
    private static bool IsSpecial(string line) => HeadingLevel(line) > 0 || Bullet(line) || TryFence(line, out _, out _);
}

internal enum MarkdownBlockKind { Paragraph, Heading, Bullet, Break, Code, Table }
internal sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, int HeadingLevel = 0, MarkdownTable? Table = null);
internal sealed record MarkdownTable(string[] Headers, string[] Alignments, IReadOnlyList<string[]> Rows);
