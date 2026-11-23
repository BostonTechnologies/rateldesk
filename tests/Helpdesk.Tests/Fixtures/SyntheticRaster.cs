using SkiaSharp;

namespace Helpdesk.Tests.Fixtures;

internal static class SyntheticRaster
{
    internal static byte[] Png(byte red = 80, int width = 2, int height = 2)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(red, 30, 50));
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    internal static void AssertPng(byte red, byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(2, bitmap.Width);
        Assert.Equal(2, bitmap.Height);
        Assert.Equal(new SKColor(red, 30, 50), bitmap.GetPixel(0, 0));
    }
}
