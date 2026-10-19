using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Helpdesk.Infrastructure.Services;

/// <summary>
/// Defines methods for generating and validating CAPTCHA challenges.
/// </summary>
/// <remarks>This interface provides functionality to create CAPTCHA challenges and validate user responses. It is
/// designed to help prevent automated interactions by requiring users to solve a challenge.</remarks>
public interface ICaptchaService
{
    (Guid Id, string Challenge) GenerateCaptcha();
    byte[] RenderCaptchaImage(string challenge);
    bool ValidateCaptcha(Guid id, string answer);
}

/// <summary>
/// Provides functionality for generating and validating CAPTCHA challenges.
/// </summary>
/// <remarks>This service generates CAPTCHA challenges consisting of a unique identifier and a random string.  The
/// challenges are stored in memory with a time-to-live (TTL) of 5 minutes.  Callers can validate a CAPTCHA by providing
/// the identifier and the user's response.</remarks>
/// <param name="cache"></param>
public class CaptchaService(IMemoryCache cache) : ICaptchaService
{
    private readonly IMemoryCache _cache = cache;
    private readonly Lock _validationLock = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const int Width = 200;
    private const int Height = 60;
    private const int RowBytes = Width * 3;
    private const int PixelScale = 4;
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;
    private static readonly IReadOnlyDictionary<char, string[]> DigitGlyphs = new Dictionary<char, string[]>
    {
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['3'] = ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['5'] = ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
        ['6'] = ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "10000", "10000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00001", "01110"]
    };

    public (Guid Id, string Challenge) GenerateCaptcha()
    {
        var id = Guid.NewGuid();
        var text = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        _cache.Set(id, text, Ttl);
        return (id, text);
    }

    public byte[] RenderCaptchaImage(string challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.Length != 6 || challenge.Any(character => !char.IsAsciiDigit(character)))
            throw new ArgumentException("A CAPTCHA image requires exactly six decimal digits.", nameof(challenge));
        var random = Random.Shared;
        var textColor = new Pixel((byte)random.Next(20, 80), (byte)random.Next(20, 80), (byte)random.Next(20, 80));

        var image = new byte[RowBytes * Height];
        Array.Fill(image, (byte)255);

        var x = 12;
        foreach (var c in challenge)
        {
            if (!DigitGlyphs.TryGetValue(c, out var glyph))
            {
                continue;
            }

            var yOffset = random.Next(2, 9);
            for (var row = 0; row < GlyphHeight; row++)
            {
                var bits = glyph[row];
                for (var col = 0; col < GlyphWidth; col++)
                {
                    if (bits[col] != '1')
                    {
                        continue;
                    }

                    FillBlock(
                        image,
                        x + (col * PixelScale),
                        yOffset + (row * PixelScale),
                        PixelScale,
                        textColor);
                }
            }

            x += (GlyphWidth * PixelScale) + 6;
        }

        for (var i = 0; i < 6; i++)
        {
            var x1 = random.Next(0, Width);
            var y1 = random.Next(0, Height);
            var x2 = random.Next(0, Width);
            var y2 = random.Next(0, Height);
            var lineColor = new Pixel((byte)random.Next(150, 210), (byte)random.Next(150, 210), (byte)random.Next(150, 210));
            DrawLine(image, x1, y1, x2, y2, lineColor);
        }

        for (var i = 0; i < 220; i++)
        {
            var noiseX = random.Next(0, Width);
            var noiseY = random.Next(0, Height);
            SetPixel(image, noiseX, noiseY, new Pixel((byte)random.Next(130, 230), (byte)random.Next(130, 230), (byte)random.Next(130, 230)));
        }

        return EncodePng(image);
    }

    private static void FillBlock(byte[] image, int x, int y, int size, Pixel color)
    {
        for (var yy = y; yy < y + size; yy++)
        {
            for (var xx = x; xx < x + size; xx++)
            {
                if ((uint)xx < Width && (uint)yy < Height)
                {
                    SetPixel(image, xx, yy, color);
                }
            }
        }
    }

    private static void DrawLine(byte[] image, int x1, int y1, int x2, int y2, Pixel color)
    {
        var dx = Math.Abs(x2 - x1);
        var sx = x1 < x2 ? 1 : -1;
        var dy = -Math.Abs(y2 - y1);
        var sy = y1 < y2 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            if ((uint)x1 < Width && (uint)y1 < Height)
            {
                SetPixel(image, x1, y1, color);
            }

            if (x1 == x2 && y1 == y2)
            {
                break;
            }

            var e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x1 += sx;
            }
            if (e2 <= dx)
            {
                error += dx;
                y1 += sy;
            }
        }
    }

    public bool ValidateCaptcha(Guid id, string answer)
    {
        lock (_validationLock)
        {
            if (_cache.TryGetValue<string>(id, out var expected))
            {
                _cache.Remove(id);
                return string.Equals(expected, answer, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }

    private readonly record struct Pixel(byte Red, byte Green, byte Blue);

    private static void SetPixel(byte[] image, int x, int y, Pixel color)
    {
        var offset = y * RowBytes + x * 3;
        image[offset] = color.Red;
        image[offset + 1] = color.Green;
        image[offset + 2] = color.Blue;
    }

    // This writes only the fixed 200x60, opaque RGB CAPTCHA raster. It never
    // decodes uploaded images or accepts caller-controlled dimensions/chunks.
    // PNG structure: https://www.w3.org/TR/png/#5DataRep
    private static byte[] EncodePng(byte[] pixels)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var row = 0; row < Height; row++)
            {
                zlib.WriteByte(0); // PNG filter type None for this scanline.
                zlib.Write(pixels.AsSpan(row * RowBytes, RowBytes));
            }
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8; // Eight bits per color channel.
        header[9] = 2; // Truecolor RGB, no alpha, palette or interlacing.
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(type);
        png.Write(data);
        var crc = UpdateCrc(UpdateCrc(uint.MaxValue, type), data) ^ uint.MaxValue;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return crc;
    }
}
