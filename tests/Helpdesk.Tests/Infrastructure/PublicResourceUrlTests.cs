using Helpdesk.Tests.Fixtures;
using Helpdesk.Application.Services.Branding;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure;

public sealed class PublicResourceUrlTests
{
    [Theory]
    [InlineData("", "https://desk.example.test", false)]
    [InlineData("https://media.example.test/", "https://media.example.test", false)]
    [InlineData("", "https://desk.example.test", true)]
    public async Task SavedSignedMediaUsesConfiguredPublicOrigin(string apiOrigin, string expectedOrigin, bool relativeStorage)
    {
        var root = Path.Combine(Path.GetTempPath(), "rateldesk-media-" + Guid.NewGuid().ToString("N"));
        try
        {
            var branding = Substitute.For<IInstanceBrandingProvider>();
            branding.GetEffectiveAsync(Arg.Any<CancellationToken>()).Returns(new InstanceBrandingSnapshot(
                "Desk", "", "https://desk.example.test", "", "", "", "", "", "", "", ""));
            var options = Options.Create(new StorageOptions { RootPath = relativeStorage ? "media" : root, PublicApiBaseUrl = apiOrigin });
            var signer = Substitute.For<IImageLinkSigner>();
            signer.GenerateToken(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>()).Returns("signed-token");
            var environment = Substitute.For<IHostEnvironment>();
            environment.ContentRootPath.Returns(root);
            var inline = new InlineImageStorageService(environment, options, signer, NullLogger<InlineImageStorageService>.Instance, branding);
            var tenant = new TenantBrandAssetStorageService(environment, options, signer, NullLogger<TenantBrandAssetStorageService>.Instance, branding);
            var template = new EmailTemplateImageStorageService(environment, options, signer, NullLogger<EmailTemplateImageStorageService>.Instance, branding);

            var urls = new[]
            {
                await inline.SaveIncidentInlineImageAsync("incident", "image.png", SyntheticRaster.Png()),
                await inline.SaveWorklogInlineImageAsync("worklog", "image.png", SyntheticRaster.Png()),
                await tenant.SaveLogoAsync(1, "logo.png", SyntheticRaster.Png()),
                await template.SaveTemplateInlineImageAsync("template", "image.png", SyntheticRaster.Png())
            };
            Assert.All(urls, url =>
            {
                Assert.StartsWith(expectedOrigin + "/api/", url);
                Assert.EndsWith("?token=signed-token", url);
            });
            var storageRoot = relativeStorage ? Path.Combine(root, "media") : root;
            Assert.Equal(4, Directory.GetFiles(storageRoot, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
