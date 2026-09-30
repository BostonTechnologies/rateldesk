using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Helpdesk.Application.WorkLogs;
using HtmlAgilityPack;

namespace Helpdesk.API.Endpoints.Incidents;

internal static class IncidentInlineImageLinks
{
    public static string? Refresh(string? html, string incidentId, IImageLinkSigner signer, string? publicApiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;

        var safeIncidentId = Regex.Replace(incidentId, "[^a-zA-Z0-9_-]", "-");
        var prefix = $"/api/incidents/{Uri.EscapeDataString(safeIncidentId)}/images/";
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var replacements = new List<(int Start, int Length, string Value)>();

        foreach (var image in document.DocumentNode.Descendants("img"))
        {
            var source = image.Attributes["src"];
            if (source is null)
                continue;

            var value = source.DeEntitizeValue.Trim();
            // Resolve root-relative and historical absolute links, but only for this ticket.
            if (!Uri.TryCreate(new Uri("https://helpdesk.invalid"), value, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var filename = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
            if (string.IsNullOrWhiteSpace(filename) || filename is "." or ".." ||
                filename.IndexOfAny(['/', '\\', '|']) >= 0)
                continue;

            var token = signer.GenerateToken("incident", safeIncidentId, filename, expires);
            var relative = $"{prefix}{Uri.EscapeDataString(filename)}?token={Uri.EscapeDataString(token)}";
            // Never send a renewed bearer token to an arbitrary host from stored HTML.
            var absoluteHttpSource = Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);
            var refreshed = absoluteHttpSource && !string.IsNullOrWhiteSpace(publicApiBaseUrl)
                ? $"{publicApiBaseUrl.TrimEnd('/')}{relative}"
                : relative;
            var encoded = WebUtility.HtmlEncode(refreshed);
            if (source.QuoteType == AttributeValueQuote.None)
                encoded = $"\"{encoded}\"";
            replacements.Add((source.ValueStartIndex, source.ValueLength, encoded));
        }

        // Change only src values; preserve the original email markup and other attributes.
        var result = new StringBuilder(html);
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            result.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Value);
        return result.ToString();
    }
}
