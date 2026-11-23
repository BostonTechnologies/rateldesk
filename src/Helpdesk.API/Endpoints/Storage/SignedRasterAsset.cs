using Helpdesk.Infrastructure.Storage;

namespace Helpdesk.API.Endpoints.Storage;

internal static class SignedRasterAsset
{
    internal static IResult Serve(HttpContext context, string directory, string filename)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (filename is "." or ".." || filename.Contains('\\') || Path.GetFileName(filename) != filename)
            return Results.BadRequest("Invalid file name.");

        FileStream file;
        try
        {
            file = File.Open(Path.Combine(directory, filename), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            return Results.NotFound();
        }

        Span<byte> prefix = stackalloc byte[12];
        if (file.Length is < 12 or > SafeInlineImage.MaxStoredBytes)
        {
            file.Dispose();
            return Results.NotFound();
        }
        file.ReadExactly(prefix);
        if (SafeInlineImage.VerifiedContentType(filename, prefix) is not { } contentType)
        {
            file.Dispose();
            return Results.NotFound();
        }

        file.Position = 0;
        return Results.File(file, contentType);
    }
}
