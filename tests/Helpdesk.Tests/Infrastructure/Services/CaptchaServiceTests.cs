using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Helpdesk.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Infrastructure.Services;

public sealed class CaptchaServiceTests
{
    [Theory]
    [InlineData("012345")]
    [InlineData("678901")]
    public void Captcha_png_has_valid_chunks_checksums_and_bounded_visible_rgb_pixels(string challenge)
    {
        var png = CaptchaService.RenderCaptchaImage(challenge);
        Assert.InRange(png.Length, 100, 37000);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var chunks = new List<string>();
        byte[]? imageData = null;
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            Assert.InRange(length, 0, png.Length - offset - 12);
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add(type);
            var payload = png.AsSpan(offset + 8, length);
            // Independent framework implementation checks the encoder's CRC.
            Assert.Equal(Crc32.HashToUInt32(png.AsSpan(offset + 4, length + 4)),
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + length + 8, 4)));
            if (type == "IHDR")
            {
                Assert.Equal(13, length);
                Assert.Equal(200, BinaryPrimitives.ReadInt32BigEndian(payload));
                Assert.Equal(60, BinaryPrimitives.ReadInt32BigEndian(payload[4..]));
                Assert.Equal(new byte[] { 8, 2, 0, 0, 0 }, payload[8..].ToArray());
            }
            if (type == "IDAT") imageData = payload.ToArray();
            if (type == "IEND") Assert.Equal(0, length);
            offset += length + 12;
        }
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks);
        Assert.NotNull(imageData);
        using var source = new MemoryStream(imageData);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        var scanlines = new byte[(200 * 3 + 1) * 60];
        zlib.ReadExactly(scanlines);
        Assert.Equal(-1, zlib.ReadByte());
        for (var row = 0; row < 60; row++) Assert.Equal(0, scanlines[row * 601]);

        // Every glyph position contains dark text; the image also retains its
        // white background and lighter interference lines/pixels.
        for (var digit = 0; digit < 6; digit++)
        {
            var darkPixels = 0;
            for (var row = 0; row < 36; row++)
                for (var x = 12 + digit * 26; x < 32 + digit * 26; x++)
                    if (scanlines[row * 601 + 1 + x * 3] < 80) darkPixels++;
            Assert.True(darkPixels > 0, $"Digit {digit} must remain visible.");
        }
        Assert.Contains((byte)255, scanlines);
        Assert.Contains(scanlines, value => value is >= 130 and < 230);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    public void Image_renderer_rejects_non_challenge_input(string challenge)
    {
        Assert.Throws<ArgumentException>(() => CaptchaService.RenderCaptchaImage(challenge));
    }

    [Fact]
    public void Captcha_accepts_the_correct_answer_once_and_consumes_an_incorrect_attempt()
    {
        var service = Service();
        var first = Generate(service);
        var second = Generate(service);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Matches("^[1-9][0-9]{5}$", first.Challenge);
        Assert.True(service.ValidateCaptcha(first.Id, first.Challenge));
        Assert.False(service.ValidateCaptcha(first.Id, first.Challenge));
        Assert.False(service.ValidateCaptcha(second.Id, "incorrect"));
        Assert.False(service.ValidateCaptcha(second.Id, second.Challenge));
        Assert.False(service.ValidateCaptcha(Guid.NewGuid(), first.Challenge));
    }

    [Fact]
    public async Task Concurrent_answers_cannot_consume_the_same_challenge_twice()
    {
        var service = Service();
        var challenge = Generate(service);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 32).Select(async _ =>
        {
            await start.Task;
            return service.ValidateCaptcha(challenge.Id, challenge.Challenge);
        }).ToArray();
        start.SetResult();
        Assert.Single(await Task.WhenAll(attempts), accepted => accepted);
    }

    [Fact]
    public void Captcha_expires_at_five_minutes_without_real_time_waits()
    {
        var clock = new CacheClock();
        var service = Service(clock);
        var valid = Generate(service);
        var expired = Generate(service);
        clock.UtcNow = clock.UtcNow.AddMinutes(5).AddTicks(-1);
        Assert.True(service.ValidateCaptcha(valid.Id, valid.Challenge));
        clock.UtcNow = clock.UtcNow.AddTicks(1);
        Assert.False(service.ValidateCaptcha(expired.Id, expired.Challenge));
    }

    [Fact]
    public void Capacity_and_per_client_slots_are_recovered_after_consume_expiry_and_render_failure()
    {
        var clock = new CacheClock();
        var service = Service(clock, new CaptchaOptions { Capacity = 2, OutstandingPerClient = 1 });
        var a = Generate(service, "client-a");
        Assert.Null(service.TryGenerate("client-a", default, _ => throw new InvalidOperationException("No rendering on rejection.")));
        var b = Generate(service, "client-b");
        Assert.Null(service.TryGenerate("client-c", default, _ => throw new InvalidOperationException("No rendering when full.")));
        Assert.False(service.ValidateCaptcha(b.Id, "wrong"));
        Assert.Throws<InvalidOperationException>(() => service.TryGenerate("client-c", default, _ => throw new InvalidOperationException("Renderer failure.")));
        using var cancelled = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => service.TryGenerate("client-c", cancelled.Token, _ =>
        {
            cancelled.Cancel();
            return [1];
        }));
        Assert.NotEqual(Guid.Empty, Generate(service, "client-c").Id);
        clock.UtcNow += TimeSpan.FromMinutes(5);
        Assert.NotEqual(Guid.Empty, Generate(service, "client-a").Id);
        Assert.False(service.ValidateCaptcha(a.Id, a.Challenge));
    }

    [Fact]
    public async Task Concurrent_render_budget_rejects_without_starting_another_renderer()
    {
        var service = Service(options: new CaptchaOptions { ConcurrentRenders = 1 });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pending = Task.Run(() => service.TryGenerate("client-a", default, _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return [1];
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Null(service.TryGenerate("client-b", default, _ => throw new InvalidOperationException("A second renderer started.")));
        }
        finally
        {
            release.Set();
        }
        Assert.NotNull(await pending);
        Assert.NotEqual(Guid.Empty, Generate(service, "client-b").Id);
    }

    private static CaptchaService Service(TimeProvider? clock = null, CaptchaOptions? options = null)
    {
        var configured = Options.Create(options ?? new CaptchaOptions());
        return new(new CaptchaChallengeStore(configured, clock ?? TimeProvider.System), configured);
    }

    private static (Guid Id, string Challenge) Generate(CaptchaService service, string client = "fixture")
    {
        string answer = string.Empty;
        var challenge = service.TryGenerate(client, default, value =>
        {
            answer = value;
            return CaptchaService.RenderCaptchaImage(value);
        });
        return (Assert.IsType<CaptchaChallenge>(challenge).Id, answer);
    }

    private sealed class CacheClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
