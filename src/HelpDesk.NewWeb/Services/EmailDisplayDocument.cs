using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace HelpDesk.NewWeb.Services;

/// <summary>Display-only sanitation. Stored/sent HTML and upstream sanitizer contracts are unchanged.</summary>
public static class EmailDisplayDocument
{
    // This is the only script permitted by the document CSP. The sandbox has no same-origin,
    // forms, popup or navigation privileges. It reports geometry and keyboard dismissal only.
    private const string Bridge = "(()=>{const send=()=>parent.postMessage({type:'rateldesk-email-size',height:document.documentElement.scrollHeight},'*');new ResizeObserver(send).observe(document.body);addEventListener('load',send);addEventListener('message',e=>{if(e.source===parent&&e.data==='rateldesk-email-measure')send()});addEventListener('keydown',e=>{if(e.key==='Escape')parent.postMessage({type:'rateldesk-email-escape'},'*')});send()})()";
    private static readonly string BridgeHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Bridge)));
    private static readonly Regex SignedImagePath = new(@"^/api/(?:incidents|worklogs)/[a-zA-Z0-9_-]+/images/[a-zA-Z0-9_.%-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex RasterData = new(@"^data:image/(?:png|jpeg|gif|webp);base64,[a-zA-Z0-9+/=\r\n]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record Link(string Text, string Url);
    public sealed record Result(string Html, string Text, IReadOnlyList<Link> Links, int BlockedImages);

    public static Result Create(string? html, string? text, string applicationBaseUri)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(["a", "abbr", "b", "blockquote", "br", "caption", "center", "code", "col", "colgroup", "dd", "del", "div", "dl", "dt", "em", "font", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "li", "ol", "p", "pre", "s", "small", "span", "strong", "sub", "sup", "table", "tbody", "td", "th", "thead", "tr", "u", "ul"]);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(["href", "src", "alt", "title", "style", "width", "height", "colspan", "rowspan", "align", "valign", "bgcolor", "color", "size", "face"]);
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedCssProperties.UnionWith(["color", "background-color", "font-family", "font-size", "font-weight", "font-style", "text-align", "text-decoration", "line-height", "white-space", "vertical-align", "padding", "padding-left", "padding-right", "padding-top", "padding-bottom", "margin", "margin-left", "margin-right", "margin-top", "margin-bottom", "border", "border-color", "border-width", "border-style", "border-collapse", "border-spacing", "width", "max-width", "height", "list-style-type"]);
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith(["https", "http", "mailto", "data"]);
        var sourceDocument = new HtmlParser().ParseDocument(html ?? string.Empty);
        var source = sourceDocument.Body!.InnerHtml;
        // A complete email may put its foreground/background on body or html. Sanitizing
        // a fragment would discard those wrappers; retain their display attributes first.
        foreach (var wrapper in new[] { sourceDocument.Body, sourceDocument.DocumentElement })
        {
            var attributes = string.Join(" ", new[] { "style", "bgcolor", "color" }
                .Where(name => wrapper.HasAttribute(name))
                .Select(name => $"{name}=\"{WebUtility.HtmlEncode(wrapper.GetAttribute(name))}\""));
            if (attributes.Length > 0) source = $"<div {attributes}>{source}</div>";
        }
        var document = new HtmlParser().ParseDocument(sanitizer.Sanitize(source));
        var links = new List<Link>();
        var origin = new Uri(applicationBaseUri).GetLeftPart(UriPartial.Authority);
        foreach (var anchor in document.QuerySelectorAll("a"))
        {
            var href = anchor.GetAttribute("href");
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto")
                links.Add(new Link(string.IsNullOrWhiteSpace(anchor.TextContent) ? uri.AbsoluteUri : anchor.TextContent, uri.AbsoluteUri));
            // Links are exposed as normal, explicitly labelled controls outside the sandbox.
            anchor.RemoveAttribute("href");
        }
        var blockedImages = 0;
        foreach (var image in document.QuerySelectorAll("img"))
        {
            var source = image.GetAttribute("src") ?? string.Empty;
            var alt = image.GetAttribute("alt");
            image.SetAttribute("alt", string.IsNullOrWhiteSpace(alt) ? "Email image" : alt);
            if (source.Length <= 4 * 1024 * 1024 && RasterData.IsMatch(source)) continue;
            // Rebase historical absolute image links onto the existing local gateway, never the
            // host supplied by an email. The image endpoint still verifies its scoped bearer token.
            if (Uri.TryCreate(new Uri(origin), source, out var imageUri) &&
                imageUri.Scheme is "http" or "https" && SignedImagePath.IsMatch(imageUri.AbsolutePath) &&
                imageUri.Query.StartsWith("?token=", StringComparison.Ordinal) && imageUri.Query.Length > 7 &&
                !imageUri.Query.Contains('&') && !imageUri.AbsolutePath.Contains("..", StringComparison.Ordinal))
            {
                image.SetAttribute("src", origin + imageUri.PathAndQuery);
                image.SetAttribute("referrerpolicy", "no-referrer");
                continue;
            }
            blockedImages++;
            var placeholder = document.CreateElement("span");
            placeholder.SetAttribute("role", "img");
            placeholder.TextContent = $"[Image unavailable: {image.GetAttribute("alt")}]";
            image.Replace(placeholder);
        }
        // URLs, CSS variables and escape sequences cannot participate in display CSS. The
        // property allowlist also excludes positioning, filters, animation and active resources.
        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            var style = element.GetAttribute("style") ?? string.Empty;
            if (style.Contains("url", StringComparison.OrdinalIgnoreCase) || style.Contains("var(", StringComparison.OrdinalIgnoreCase) || style.Contains('\\'))
                element.RemoveAttribute("style");
        }
        var body = document.Body!;
        var plain = string.IsNullOrWhiteSpace(text) ? ToText(body.InnerHtml) : text;
        var csp = $"default-src 'none'; script-src 'sha256-{BridgeHash}'; style-src 'unsafe-inline'; img-src data: {origin}; base-uri 'none'; form-action 'none'; connect-src 'none'";
        var output = $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\"><meta http-equiv=\"Content-Security-Policy\" content=\"{WebUtility.HtmlEncode(csp)}\"><style>html{{color-scheme:light;background:#fff;color:#111}}body{{margin:12px;font:14px/1.5 Arial,sans-serif;overflow-wrap:anywhere}}table{{max-width:100%}}img{{max-width:100%;height:auto}}pre{{white-space:pre-wrap}}a{{text-decoration:underline}}*{{box-sizing:border-box}}</style></head><body>{body.InnerHtml}<script>{Bridge}</script></body></html>";
        return new Result(output, plain ?? string.Empty, links.Distinct().ToArray(), blockedImages);
    }

    private static string ToText(string html)
    {
        var spaced = Regex.Replace(html, @"<br\s*/?>|</(?:p|div|li|tr|h[1-6]|blockquote)>", "\n", RegexOptions.IgnoreCase);
        return new HtmlParser().ParseDocument(spaced).Body!.TextContent.Trim();
    }
}
