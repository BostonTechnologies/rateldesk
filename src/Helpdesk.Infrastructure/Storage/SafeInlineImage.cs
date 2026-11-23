using System.Diagnostics;
using SkiaSharp;

namespace Helpdesk.Infrastructure.Storage;

public sealed class InlineImageRejectedException() : Exception("The inline image is invalid or exceeds supported limits.");

public static class SafeInlineImage
{
    public const int MaxInputBytes = 12 * 1024 * 1024;
    public const int MaxStoredBytes = 24 * 1024 * 1024;
    private const int MaxDimension = 8192;
    private const long MaxPixels = 12_000_000;
    private const int MaxFrames = 32;
    private static readonly TimeSpan ProcessingBudget = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim Decoders = new(2, 2);
    private static readonly SemaphoreSlim Admitted = new(6, 6);

    public static string? ExtensionForMediaType(string contentType) => contentType.Split(';', 2)[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" or "image/x-ms-bmp" => ".bmp",
        _ => null
    };

    public static bool MatchesDeclaredType(string filename, string contentType) =>
        ExtensionForMediaType(contentType) is { } extension && CanonicalExtension(filename) == extension;

    public static string? VerifiedContentType(string filename, ReadOnlySpan<byte> prefix) => CanonicalExtension(filename) switch
    {
        ".png" when prefix.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) => "image/png",
        ".jpg" when prefix.StartsWith(new byte[] { 255, 216, 255 }) => "image/jpeg",
        ".gif" when prefix.StartsWith("GIF87a"u8) || prefix.StartsWith("GIF89a"u8) => "image/gif",
        ".webp" when prefix.Length >= 12 && prefix.StartsWith("RIFF"u8) && prefix[8..].StartsWith("WEBP"u8) => "image/webp",
        ".bmp" when prefix.StartsWith("BM"u8) => "image/bmp",
        _ => null
    };

    internal static async Task<(byte[] Content, string Extension)> NormalizeAsync(string filename, byte[] content)
    {
        var mime = VerifiedContentType(filename, content);
        if (mime is null || content.Length > MaxInputBytes || !Admitted.Wait(0))
            throw new InlineImageRejectedException();

        var elapsed = Stopwatch.StartNew();
        if (!await Decoders.WaitAsync(ProcessingBudget))
        {
            Admitted.Release();
            throw new InlineImageRejectedException();
        }

        // Native codecs are synchronous. A timed-out conversion keeps its slot
        // until it exits, so its resources cannot overlap with another decode.
        var decoding = Task.Run(() => Decode(content, mime, elapsed));
        var remaining = ProcessingBudget - elapsed.Elapsed;
        try
        {
            return await decoding.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)
                ?? throw new InlineImageRejectedException();
        }
        catch (TimeoutException)
        {
            throw new InlineImageRejectedException();
        }
    }

    private static (byte[] Content, string Extension)? Decode(byte[] content, string mime, Stopwatch elapsed)
    {
        try
        {
            if (elapsed.Elapsed >= ProcessingBudget)
                return null;
            using var stream = new SKMemoryStream(content);
            using var codec = SKCodec.Create(stream);
            var format = mime switch
            {
                "image/jpeg" => SKEncodedImageFormat.Jpeg,
                "image/gif" => SKEncodedImageFormat.Gif,
                "image/webp" => SKEncodedImageFormat.Webp,
                "image/bmp" => SKEncodedImageFormat.Bmp,
                _ => SKEncodedImageFormat.Png
            };
            if (codec is null || codec.EncodedFormat != format)
                return null;
            var source = codec.Info;
            if (source.Width is <= 0 or > MaxDimension || source.Height is <= 0 or > MaxDimension ||
                (long)source.Width * source.Height > MaxPixels || codec.FrameCount > MaxFrames ||
                elapsed.Elapsed >= ProcessingBudget)
                return null;

            // Keep one raster frame and drop metadata, animation and trailing bytes.
            var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap();
            if (!bitmap.TryAllocPixels(info) ||
                codec.GetPixels(info, bitmap.GetPixels(), bitmap.RowBytes, new SKCodecOptions(0)) != SKCodecResult.Success ||
                elapsed.Elapsed >= ProcessingBudget)
                return null;

            var jpeg = format == SKEncodedImageFormat.Jpeg;
            using var encoded = bitmap.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? 90 : 100);
            if (encoded is null || encoded.Size > MaxStoredBytes || elapsed.Elapsed >= ProcessingBudget)
                return null;
            return (encoded.ToArray(), jpeg ? ".jpg" : ".png");
        }
        finally
        {
            Decoders.Release();
            Admitted.Release();
        }
    }

    internal static async Task SaveAsync(string directory, string filename, byte[] content)
    {
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content);
            File.Move(temporaryPath, Path.Combine(directory, filename), overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string CanonicalExtension(string filename)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        return extension == ".jpeg" ? ".jpg" : extension;
    }
}
