using System.Net;
using System.Text;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Storage;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Email;

public sealed class InboundInlineImageResolver(
    IInlineImageStorageService storage,
    ILogger<InboundInlineImageResolver> logger) : IInboundInlineImageResolver
{
    public async Task<InboundInlineImageResult> ResolveAsync(
        string incidentId,
        string html,
        IReadOnlyList<InboundEmailAttachmentContext> attachments,
        string? graphMessageId = null,
        string? internetMessageId = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["IncidentId"] = incidentId,
            ["GraphMessageId"] = graphMessageId,
            ["InternetMessageId"] = internetMessageId
        });
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var candidates = attachments.Select((attachment, index) => (Attachment: attachment, Index: index))
            .Where(x => x.Attachment.ContentBytes is not null && !string.IsNullOrWhiteSpace(x.Attachment.ContentId))
            .ToLookup(x => NormalizeCid(x.Attachment.ContentId!), StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<int>();
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var replacements = new List<(int Start, int Length, string Value)>();
        foreach (var node in document.DocumentNode.Descendants("img"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = node.Attributes["src"];
            var value = source?.DeEntitizeValue.Trim();
            if (source is null || value is null || !value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                continue;

            var cid = NormalizeCid(value[4..]);
            if (!resolved.TryGetValue(cid, out var url))
            {
                var matches = candidates[cid].ToList();
                var preferred = matches.Where(x => x.Attachment.IsInline == true).ToList();
                if (preferred.Count == 1)
                    matches = preferred;

                if (matches.Count == 0)
                {
                    logger.LogWarning("Inbound inline image missing. ContentId={ContentId}", cid);
                    resolved[cid] = null;
                    continue;
                }

                var selected = matches[0];
                if (matches.Any(x => !x.Attachment.ContentBytes!.AsSpan().SequenceEqual(selected.Attachment.ContentBytes)))
                {
                    logger.LogWarning("Inbound inline image ambiguous. ContentId={ContentId} AttachmentIds={AttachmentIds} Names={Names}",
                        cid, string.Join(",", matches.Select(x => x.Attachment.AttachmentId)), string.Join(",", matches.Select(x => x.Attachment.Name)));
                    resolved[cid] = null;
                    continue;
                }

                try
                {
                    if (!SafeInlineImage.MatchesDeclaredType(selected.Attachment.Name, selected.Attachment.ContentType))
                        throw new InlineImageRejectedException();
                    url = await storage.SaveIncidentInlineImageAsync(incidentId, selected.Attachment.Name, selected.Attachment.ContentBytes!);
                    foreach (var match in matches)
                        consumed.Add(match.Index);
                }
                catch (InlineImageRejectedException)
                {
                    // An explicit attachment remains available through the attachment download flow.
                    foreach (var match in matches.Where(item => item.Attachment.IsInline == true))
                        consumed.Add(match.Index);
                    logger.LogWarning("Inbound inline image blocked. AttachmentId={AttachmentId}", selected.Attachment.AttachmentId);
                }
                resolved[cid] = url;
            }

            if (url is not null)
            {
                var encoded = WebUtility.HtmlEncode(url);
                if (source.QuoteType == AttributeValueQuote.None)
                    encoded = $"\"{encoded}\"";
                replacements.Add((source.ValueStartIndex, source.ValueLength, encoded));
            }
        }

        // Use DOM source offsets to preserve all original markup outside the src values.
        var result = new StringBuilder(html);
        foreach (var replacement in replacements.OrderByDescending(x => x.Start))
            result.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Value);
        return new InboundInlineImageResult(result.ToString(), consumed);
    }

    private static string NormalizeCid(string cid) => cid.Trim().Trim('<', '>').Trim();
}
