using Helpdesk.Tests.Fixtures;
using Helpdesk.Infrastructure.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Storage;

public class TenantBrandAssetStorageServiceTests
{
    [Fact]
    public async Task SaveLogoAsync_ReturnsSignedUrl_WithValidToken()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var options = Options.Create(new StorageOptions
        {
            RootPath = tempRoot,
            PublicApiBaseUrl = "https://api.example.test",
            ImageSigningSecret = "test-signing-secret-12345"
        });

        var signer = new ImageLinkSigner(options, NullLogger<ImageLinkSigner>.Instance);
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(tempRoot);
        var service = new TenantBrandAssetStorageService(
            env,
            options,
            signer,
            NullLogger<TenantBrandAssetStorageService>.Instance);

        var url = await service.SaveLogoAsync(42, "logo.png", SyntheticRaster.Png());

        var uri = new Uri(url);
        Assert.Contains("/api/tenants/42/branding/logo/logo.png", uri.AbsolutePath, StringComparison.Ordinal);

        var query = QueryHelpers.ParseQuery(uri.Query);
        var token = query.TryGetValue("token", out var values) ? values.ToString() : null;
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(signer.ValidateToken(token!, "tenant-brand", "42", "logo.png"));
    }
}
