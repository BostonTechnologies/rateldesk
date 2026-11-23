using System.Text.RegularExpressions;
using Helpdesk.Application.WorkLogs;

namespace Helpdesk.Infrastructure.Storage;

public sealed class WorklogImageStorageService(IInlineImageStorageService inlineImageStorageService) : IWorklogImageStorageService
{
    private static readonly Regex InlineImageRegex = new(
        "<img\\b[^>]*?\\bsrc\\s*=\\s*[\"'](?<src>data:image/(?<type>[a-zA-Z0-9.+-]+);base64,(?<data>[^\"']+))[\"'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IInlineImageStorageService _inlineImageStorageService = inlineImageStorageService;

    public async Task<string> ExtractAndStoreImagesAsync(string worklogId, string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var result = new System.Text.StringBuilder(html.Length);
        var offset = 0;
        foreach (Match match in InlineImageRegex.Matches(html))
        {
            result.Append(html, offset, match.Index - offset);
            offset = match.Index + match.Length;
            var extension = SafeInlineImage.ExtensionForMediaType($"image/{match.Groups["type"].Value}");
            var base64 = match.Groups["data"];
            if (extension is null || base64.Length > ((SafeInlineImage.MaxInputBytes + 2) / 3) * 4)
                continue;

            try
            {
                var bytes = Convert.FromBase64String(base64.Value);
                var url = await _inlineImageStorageService.SaveWorklogInlineImageAsync(
                    worklogId, $"{Guid.NewGuid():N}{extension}", bytes);
                var source = match.Groups["src"];
                result.Append(html, match.Index, source.Index - match.Index);
                result.Append(System.Net.WebUtility.HtmlEncode(url));
                result.Append(html, source.Index + source.Length, offset - source.Index - source.Length);
            }
            catch (Exception failure) when (failure is FormatException or InlineImageRejectedException)
            {
                continue;
            }
        }

        return result.Append(html, offset, html.Length - offset).ToString();
    }
}
