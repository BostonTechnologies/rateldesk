using Helpdesk.Tests.Fixtures;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Storage;
using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Application.Services;

public sealed class InboundInlineImageResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "helpdesk-783-" + Guid.NewGuid().ToString("N"));
    private readonly InlineImageStorageService _storage;
    private readonly InboundInlineImageResolver _resolver;
    private readonly ILogger<InboundInlineImageResolver> _logger = Substitute.For<ILogger<InboundInlineImageResolver>>();

    public InboundInlineImageResolverTests()
    {
        var options = Options.Create(new StorageOptions { RootPath = _root, ImageSigningSecret = "test-inline-image-secret" });
        _storage = new InlineImageStorageService(Substitute.For<IHostEnvironment>(), options,
            new ImageLinkSigner(options, NullLogger<ImageLinkSigner>.Instance), NullLogger<InlineImageStorageService>.Instance);
        _resolver = new InboundInlineImageResolver(_storage, _logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateNames_MapScreenshotAndSignatureByCid_RegardlessOfOrder(bool reverse)
    {
        var attachments = new[] { Attachment("body", SyntheticRaster.Png(1)), Attachment("signature", SyntheticRaster.Png(3)) };
        if (reverse) Array.Reverse(attachments);
        var result = await _resolver.ResolveAsync("incident", "<p>Screenshot</p><img src='cid:body'><p>Signature</p><img src='cid:signature'>", attachments);
        var sources = Sources(result.Html);
        Assert.NotEqual(sources[0], sources[1]);
        SyntheticRaster.AssertPng(1, await ReadImage(sources[0]));
        SyntheticRaster.AssertPng(3, await ReadImage(sources[1]));
        Assert.Equal(2, result.ConsumedAttachmentIndexes.Count);
    }

    [Fact]
    public async Task LaterReplyWithSameName_DoesNotOverwriteOriginalImage()
    {
        var original = await _resolver.ResolveAsync("incident", "<img src='cid:body'>", [Attachment("body", SyntheticRaster.Png(1))]);
        var reply = await _resolver.ResolveAsync("incident", "<img src='cid:body'>", [Attachment("body", SyntheticRaster.Png(2))]);
        Assert.NotEqual(Sources(original.Html)[0], Sources(reply.Html)[0]);
        SyntheticRaster.AssertPng(1, await ReadImage(Sources(original.Html)[0]));
        SyntheticRaster.AssertPng(2, await ReadImage(Sources(reply.Html)[0]));
    }

    [Theory]
    [InlineData("<IMG SRC = ' cid:&lt;BoDy&gt; '>")]
    [InlineData("<img src=cid:BODY>")]
    public async Task CidNormalization_ResolvesBracketWhitespaceCaseAndEntities(string html)
    {
        var result = await _resolver.ResolveAsync("incident", html, [Attachment(" <body> ", SyntheticRaster.Png(1))]);
        SyntheticRaster.AssertPng(1, await ReadImage(Sources(result.Html)[0]));
        Assert.Single(result.ConsumedAttachmentIndexes);
    }

    [Fact]
    public async Task OnlyImageSourcesChange_AndUnreferencedAttachmentsRemainUnconsumed()
    {
        const string html = "<!doctype html><P title='cid:body'>cid:body &amp; text</P><!-- cid:body --><img src='cid:body' /><img src='https://example.com/a'><img src='data:image/png;base64,AA=='>";
        var result = await _resolver.ResolveAsync("incident", html, [Attachment("body", SyntheticRaster.Png(1)), Attachment("download", SyntheticRaster.Png(2))]);
        Assert.Equal(html.Replace("src='cid:body'", $"src='{Sources(result.Html)[0]}'"), result.Html);
        Assert.Equal(new[] { 0 }, result.ConsumedAttachmentIndexes);
    }

    [Fact]
    public async Task MissingCid_RemainsUnresolvedAndLogsWarning()
    {
        const string html = "<img src='cid:missing'>";
        var result = await _resolver.ResolveAsync("incident", html, [Attachment("signature", SyntheticRaster.Png(2))]);
        Assert.Equal(html, result.Html);
        Assert.Empty(result.ConsumedAttachmentIndexes);
        Assert.Contains(_logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log" && Equals(call.GetArguments()[0], LogLevel.Warning));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task DifferentBytesWithDuplicateCid_AreAmbiguous()
    {
        const string html = "<img src='cid:body'><img src='cid:body'>";
        var result = await _resolver.ResolveAsync("incident", html, [Attachment("body", SyntheticRaster.Png(1)), Attachment("BODY", SyntheticRaster.Png(2))]);
        Assert.Equal(html, result.Html);
        Assert.Empty(result.ConsumedAttachmentIndexes);
        Assert.Contains(_logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log" && Equals(call.GetArguments()[0], LogLevel.Warning));
    }

    [Fact]
    public async Task SingleInlineCandidate_DisambiguatesDuplicateCid()
    {
        var result = await _resolver.ResolveAsync("incident", "<img src='cid:body'>",
            [Attachment("body", SyntheticRaster.Png(1)), Attachment("BODY", SyntheticRaster.Png(2)) with { IsInline = true }]);
        SyntheticRaster.AssertPng(2, await ReadImage(Sources(result.Html)[0]));
        Assert.Equal(new[] { 1 }, result.ConsumedAttachmentIndexes);
    }

    [Fact]
    public async Task IdenticalDuplicateCidPayloads_AreConsumedWithoutAmbiguity()
    {
        var result = await _resolver.ResolveAsync("incident", "<img src='cid:body'>",
            [Attachment("body", SyntheticRaster.Png(1)), Attachment("BODY", SyntheticRaster.Png(1))]);
        SyntheticRaster.AssertPng(1, await ReadImage(Sources(result.Html)[0]));
        Assert.Equal(2, result.ConsumedAttachmentIndexes.Count);
    }

    [Fact]
    public async Task NullPayload_IsNotConsumed()
    {
        var result = await _resolver.ResolveAsync("incident", "<img src='cid:body'>", [Attachment("body", SyntheticRaster.Png(1)) with { ContentBytes = null }]);
        Assert.Equal("<img src='cid:body'>", result.Html);
        Assert.Empty(result.ConsumedAttachmentIndexes);
    }

    [Theory]
    [InlineData("image001.png", ".png")]
    [InlineData("../../escape.PNG", ".png")]
    [InlineData("..\\..\\escape.png", ".png")]
    public async Task Storage_IsCollisionSafeIdempotentAndContained(string filename, string extension)
    {
        var first = await _storage.SaveIncidentInlineImageAsync("incident", filename, SyntheticRaster.Png(1));
        var second = await _storage.SaveIncidentInlineImageAsync("incident", filename, SyntheticRaster.Png(2));
        var repeated = await _storage.SaveIncidentInlineImageAsync("incident", filename, SyntheticRaster.Png(1));
        Assert.Equal(new Uri("https://test" + first).AbsolutePath, new Uri("https://test" + repeated).AbsolutePath);
        Assert.NotEqual(first, second);
        SyntheticRaster.AssertPng(1, await ReadImage(first));
        SyntheticRaster.AssertPng(2, await ReadImage(second));
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length);
        Assert.All(files, file =>
        {
            Assert.Equal(Path.Combine(_root, "incidents", "incident", "inline"), Path.GetDirectoryName(file));
            Assert.Equal(extension, Path.GetExtension(file));
            Assert.Matches("^[a-zA-Z0-9_-]+(?:\\.[a-z0-9]+)?$", Path.GetFileName(file));
        });
    }

    [Fact]
    public async Task UnsupportedCidLeavesMessageAndIndependentAttachmentAvailable()
    {
        var document = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        const string html = "<p>Keep this email</p><img src='cid:unsupported'>";
        var result = await _resolver.ResolveAsync("incident", html,
        [
            new("inline.svg", "image/svg+xml", "unsupported", document, IsInline: true),
            new("separate.png", "image/png", null, SyntheticRaster.Png())
        ]);
        Assert.Equal(html, result.Html);
        Assert.Equal(new[] { 0 }, result.ConsumedAttachmentIndexes);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task RasterStorageDecodesBoundsAndNormalizesInput()
    {
        var original = SyntheticRaster.Png();
        byte[] padded = [.. original, .. "trailing synthetic metadata"u8];
        var url = await _storage.SaveIncidentInlineImageAsync("incident", "screen.png", padded);
        var stored = await ReadImage(url);
        SyntheticRaster.AssertPng(80, stored);
        Assert.False(stored.AsSpan().EndsWith("trailing synthetic metadata"u8));

        await Assert.ThrowsAsync<InlineImageRejectedException>(() =>
            _storage.SaveIncidentInlineImageAsync("incident", "document.png", "<html>document</html>"u8.ToArray()));
        await Assert.ThrowsAsync<InlineImageRejectedException>(() =>
            _storage.SaveIncidentInlineImageAsync("incident", "truncated.png", original[..24]));
        await Assert.ThrowsAsync<InlineImageRejectedException>(() =>
            _storage.SaveIncidentInlineImageAsync("incident", "dimension.png", SyntheticRaster.Png(width: 8193)));
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WorklogDropsUnsupportedDataImagesAndStoresScreenshots()
    {
        var worklogs = new WorklogImageStorageService(_storage);
        var screenshot = Convert.ToBase64String(SyntheticRaster.Png());
        var document = Convert.ToBase64String("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray());
        var result = await worklogs.ExtractAndStoreImagesAsync("example",
            $"<p>Reply</p><img src='data:image/svg+xml;base64,{document}'><img src='data:image/png;base64,{screenshot}'>");
        Assert.Contains("<p>Reply</p>", result);
        Assert.StartsWith("/api/worklogs/example/images/", Assert.Single(Sources(result)));
        SyntheticRaster.AssertPng(80, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories))));
    }

    private static InboundEmailAttachmentContext Attachment(string cid, byte[] bytes) => new("image001.png", "image/png", cid, bytes);
    private Task<byte[]> ReadImage(string url) => File.ReadAllBytesAsync(Path.Combine(_root, "incidents", "incident", "inline", Path.GetFileName(new Uri("https://test" + url).AbsolutePath)));
    private static string[] Sources(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode.Descendants("img").Select(node => node.Attributes["src"].DeEntitizeValue).ToArray();
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
