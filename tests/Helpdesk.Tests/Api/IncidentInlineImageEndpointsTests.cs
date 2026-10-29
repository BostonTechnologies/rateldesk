using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Storage;
using System.Text.Json;
using Helpdesk.Shared.Models;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public partial class IncidentCcRecipientsEndpointsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("https://api.example.test")]
    public async Task Peek_RefreshesDistinctHashedAndLegacyImageUrls_AndServesOriginalBytes(string publicApiBaseUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "helpdesk-783-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new StorageOptions { RootPath = root, ImageSigningSecret = "test-image-signing-secret", PublicApiBaseUrl = publicApiBaseUrl });
            var signer = new ImageLinkSigner(options, NullLogger<ImageLinkSigner>.Instance);
            var storage = new InlineImageStorageService(Substitute.For<IHostEnvironment>(), options, signer, NullLogger<InlineImageStorageService>.Instance);
            var first = await storage.SaveIncidentInlineImageAsync("inc-images", "image001.png", [1, 2]);
            var second = await storage.SaveIncidentInlineImageAsync("inc-images", "image001.png", [3, 4]);
            await File.WriteAllBytesAsync(Path.Combine(root, "incidents", "inc-images", "inline", "legacy.png"), [5, 6]);
            string Expired(string url) => url.Split('?')[0] + "?token=" + signer.GenerateToken("incident", "inc-images", Path.GetFileName(url.Split('?')[0]), DateTimeOffset.UtcNow.AddDays(-1));
            var sources = new[] { Expired(first), Expired(second), Expired("/api/incidents/inc-images/images/legacy.png") };
            await _incidentRepo.CreateAsync(new Incident
            {
                Id = "inc-images",
                Title = "Images",
                TrackingId = "INC-IMAGES",
                OriginalEmailHtml = string.Join("", sources.Select(source => $"<img src=\"{source}\">"))
            });
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton<IOptions<StorageOptions>>(options);
                services.AddSingleton<IImageLinkSigner>(signer);
            }));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");
            var peek = await client.GetFromJsonAsync<JsonElement>("/api/v1/incidents/inc-images/peek");
            var document = new HtmlDocument();
            document.LoadHtml(peek.GetProperty("html").GetString());
            Assert.All(document.DocumentNode.Descendants("img"), node => Assert.True(node.Attributes["src"] is not null, peek.GetProperty("html").GetString()));
            var refreshed = document.DocumentNode.Descendants("img").Select(node => node.Attributes["src"].DeEntitizeValue).ToArray();
            Assert.Equal(3, refreshed.Distinct().Count());
            for (var index = 0; index < sources.Length; index++)
            {
                Assert.Equal(sources[index].Split('?')[0], refreshed[index].Split('?')[0]);
                Assert.NotEqual(sources[index], refreshed[index]);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(sources[index])).StatusCode);
                Assert.Equal(new byte[] { (byte)(index * 2 + 1), (byte)(index * 2 + 2) }, await client.GetByteArrayAsync(refreshed[index]));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

public partial class IncidentCcRecipientsEndpointsTests
{
    [Fact]
    public async Task Peek_ReturnsCompletePlainTextAndSanitizedHtmlWithoutChangingStoredMessage()
    {
        var text = new string('x', 1000);
        const string html = "<body style='color:#222;background-color:#fff'><p>Original</p><script>alert(1)</script></body>";
        await _incidentRepo.CreateAsync(new Incident { Id = "inc-display", Title = "Synthetic", TrackingId = "UX-164", OriginalEmailHtml = html, OriginalEmailText = text });
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");
        using var response = await client.GetAsync("/api/v1/incidents/inc-display/peek");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var peek = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(text, peek.GetProperty("text").GetString());
        Assert.Equal(400, peek.GetProperty("snippetText").GetString()!.Length);
        Assert.DoesNotContain("<script", peek.GetProperty("html").GetString());
        Assert.Contains("background-color", peek.GetProperty("html").GetString());
        Assert.Equal(html, (await _incidentRepo.GetAsync("inc-display"))!.OriginalEmailHtml);
    }

    [Fact]
    public async Task Peek_RejectsReaderWithoutTicketOrganizationAccess()
    {
        await _incidentRepo.CreateAsync(new Incident { Id = "inc-private-preview", Title = "Synthetic", TrackingId = "UX-164", OriginalEmailText = "Restricted content" });
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "Incident.Read");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/incidents/inc-private-preview/peek")).StatusCode);
    }
}
