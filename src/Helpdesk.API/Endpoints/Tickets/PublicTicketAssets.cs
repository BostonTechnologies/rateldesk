using System.Security.Cryptography;
using Helpdesk.API.Endpoints.PublicIngress;
using Helpdesk.API.Endpoints.Storage;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.DTOs.PublicAccess;
using HtmlAgilityPack;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.Tickets;

internal sealed class PublicTicketAssets(IDataProtectionProvider keys)
{
    private readonly IDataProtector _codec = keys.CreateProtector("RatelDesk.PublicTicketRasterReference.v1");

    internal string? Rewrite(string? html, PublicLinkCapability grant, string? worklogId)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;
        var document = new HtmlDocument();
        document.LoadHtml(html);
        foreach (var image in document.DocumentNode.Descendants("img"))
        {
            image.Attributes.Remove("srcset");
            if (image.Attributes["src"] is not { } source)
                continue;
            var url = source.DeEntitizeValue;
            if (!Uri.TryCreate(new Uri("https://assets.invalid"), url, out var asset))
                continue;
            var parts = asset.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            if (parts is not ["api", "incidents" or "worklogs", _, "images", _])
                continue;

            var kind = parts[1];
            var owner = parts[2];
            var name = parts[4];
            var expected = kind == "incidents" ? grant.TicketId : worklogId;
            if (owner != expected || !SafeSegment(owner) || !SafeSegment(name))
            {
                image.Attributes.Remove("src");
                continue;
            }
            source.Value = $"{PublicAccessProtocol.BrowserPrefix}/{grant.Id:D}/asset/{_codec.Protect($"{grant.Id:D}|{kind}|{owner}|{name}")}";
        }
        return document.DocumentNode.OuterHtml;
    }

    internal static async Task<IResult> GetAsync(Guid accessId, string reference, HttpContext http, PublicLinkService links,
        PublicTicketAssets assets, HelpdeskDbContext db, IWebHostEnvironment environment, IOptions<StorageOptions> storage, CancellationToken ct)
    {
        if (reference.Length > 1024 ||
            await PublicLinkEndpoints.AuthorizeAsync(http, links, accessId, ct) is not { Purpose: PublicLinkPurpose.Ticket } grant)
            return Results.Unauthorized();
        string[] parts;
        try
        {
            parts = assets._codec.Unprotect(reference).Split('|');
        }
        catch (CryptographicException)
        {
            return Results.NotFound();
        }
        if (parts is not [_, "incidents" or "worklogs", _, _] || parts[0] != $"{accessId:D}" ||
            !SafeSegment(parts[2]) || !SafeSegment(parts[3]))
            return Results.NotFound();

        var ownerIsPublic = parts[1] == "incidents"
            ? parts[2] == grant.TicketId
            : await db.WorkLogs.AnyAsync(row => row.Id == parts[2] && row.TicketId == grant.TicketId && !row.IsInternalNote, ct);
        if (!ownerIsPublic)
            return Results.NotFound();
        var root = storage.Value.ResolveRootPath(environment.ContentRootPath);
        return SignedRasterAsset.Serve(http, Path.Combine(root, parts[1], parts[2], "inline"), parts[3]);
    }

    private static bool SafeSegment(string value) => value.Length is > 0 and <= 255 && value is not ("." or "..") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
