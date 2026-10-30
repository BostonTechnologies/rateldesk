using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Templates;

public class TenantBrandingResolverTests
{
    [Fact]
    public async Task ResolveAsync_UsesPersistedInstanceUrlForDefaultAndTenantLogoLinks()
    {
        var repo = Substitute.For<IRepository<TenantBranding>>();
        repo.GetAllAsync().Returns(new[] { new TenantBranding { TenantId = 1, LogoUrl = "/api/tenants/1/branding/logo/logo.png?token=expired" } });
        var branding = Substitute.For<Helpdesk.Application.Services.Branding.IInstanceBrandingProvider>();
        branding.GetEffectiveAsync(Arg.Any<CancellationToken>()).Returns(new Helpdesk.Application.Services.Branding.InstanceBrandingSnapshot(
            "Desk", "", "https://desk.example.test", "", "", "", "", "", "", "", ""));
        var resolver = new TenantBrandingResolver(repo, new TestImageLinkSigner(), new ConfigurationBuilder().Build(), branding);
        var defaults = await resolver.ResolveAsync(null);
        var tenant = await resolver.ResolveAsync("1");
        Assert.Contains("https://desk.example.test/email-brand/rateldesk-email-wordmark.png", defaults.LogoHtml);
        Assert.Contains("https://desk.example.test/api/tenants/1/branding/logo/logo.png?token=", tenant.LogoHtml);
        Assert.DoesNotContain("token=expired", tenant.LogoHtml);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsDefaults_WhenTenantBrandingMissing()
    {
        var repo = Substitute.For<IRepository<TenantBranding>>();
        repo.GetAllAsync().Returns(Array.Empty<TenantBranding>());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailBrand:BrandName"] = "System Brand",
                ["EmailBrand:FooterHtml"] = "<p>System Footer</p>",
                ["EmailBrand:PrimaryColor"] = "#123456",
                ["EmailBrand:FromName"] = "System Sender",
                ["EmailBrand:ReplyTo"] = "noreply@example.test"
            })
            .Build();

        var resolver = new TenantBrandingResolver(repo, new TestImageLinkSigner(), config);
        var resolved = await resolver.ResolveAsync("999");

        Assert.Equal("System Brand", resolved.BrandName);
        Assert.Equal("<p>System Footer</p>", resolved.FooterHtml);
        Assert.Equal("#123456", resolved.PrimaryColor);
        Assert.Equal("System Sender", resolved.FromName);
        Assert.Equal("noreply@example.test", resolved.ReplyTo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task ResolveAsync_Uses_BundledPngWordmark_ForDefaultEmailBrand(string? configuredLogoPath)
    {
        var repo = Substitute.For<IRepository<TenantBranding>>();
        repo.GetAllAsync().Returns(Array.Empty<TenantBranding>());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageOptions:PublicApiBaseUrl"] = "https://api.example.test",
                ["EmailBrand:LogoPath"] = configuredLogoPath
            })
            .Build();

        var resolver = new TenantBrandingResolver(repo, new TestImageLinkSigner(), config);
        var resolved = await resolver.ResolveAsync(null);

        Assert.Contains("https://api.example.test/email-brand/rateldesk-email-wordmark.png", resolved.LogoHtml);
    }

    [Theory]
    [InlineData("/custom/email-logo.png")]
    [InlineData("custom/email-logo.png")]
    public async Task ResolveAsync_PreservesConfiguredDefaultLogoPath(string configuredLogoPath)
    {
        var repo = Substitute.For<IRepository<TenantBranding>>();
        repo.GetAllAsync().Returns(Array.Empty<TenantBranding>());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageOptions:PublicApiBaseUrl"] = "https://api.example.test",
                ["EmailBrand:LogoPath"] = configuredLogoPath
            })
            .Build();

        var resolver = new TenantBrandingResolver(repo, new TestImageLinkSigner(), config);
        var resolved = await resolver.ResolveAsync(null);

        Assert.Contains("https://api.example.test/custom/email-logo.png", resolved.LogoHtml);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsTenantOverride_AndBuildsLogoHtml()
    {
        var repo = Substitute.For<IRepository<TenantBranding>>();
        repo.GetAllAsync().Returns(new[]
        {
            new TenantBranding
            {
                TenantId = 42,
                BrandName = "Tenant Brand",
                LogoUrl = "/api/tenants/42/branding/logo/logo.png?token=abc",
                FooterHtml = "<p>Tenant Footer</p>",
                PrimaryColor = "#abcdef",
                FromName = "Tenant Sender",
                ReplyTo = "support@tenant.test"
            }
        });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageOptions:PublicApiBaseUrl"] = "https://api.example.test"
            })
            .Build();

        var resolver = new TenantBrandingResolver(repo, new TestImageLinkSigner(), config);
        var resolved = await resolver.ResolveAsync("42");

        Assert.Equal("Tenant Brand", resolved.BrandName);
        Assert.Contains("<img", resolved.LogoHtml);
        Assert.Contains("https://api.example.test/api/tenants/42/branding/logo/logo.png?token=ok", resolved.LogoHtml);
        Assert.Equal("<p>Tenant Footer</p>", resolved.FooterHtml);
        Assert.Equal("#abcdef", resolved.PrimaryColor);
        Assert.Equal("Tenant Sender", resolved.FromName);
        Assert.Equal("support@tenant.test", resolved.ReplyTo);
    }

    [Fact]
    public async Task ResolveAsync_RefreshesExpiredLogoToken_ForAbsoluteTenantLogoUrl()
    {
        var repo = Substitute.For<IRepository<TenantBranding>>();
        repo.GetAllAsync().Returns(new[]
        {
            new TenantBranding
            {
                TenantId = 42,
                BrandName = "Tenant Brand",
                LogoUrl = "https://old.example.test/api/tenants/42/branding/logo/logo.png?signature=expired"
            }
        });
        var config = new ConfigurationBuilder().Build();

        var resolver = new TenantBrandingResolver(repo, new TestImageLinkSigner(), config);
        var resolved = await resolver.ResolveAsync("42");

        Assert.Contains("https://old.example.test/api/tenants/42/branding/logo/logo.png?token=ok", resolved.LogoHtml);
        Assert.DoesNotContain("expired", resolved.LogoHtml);
    }

    [Fact]
    public async Task ResolveAsync_OmitsRelativeLogo_WhenPublicBaseUrlMissing()
    {
        var repo = Substitute.For<IRepository<TenantBranding>>();
        repo.GetAllAsync().Returns(new[]
        {
            new TenantBranding
            {
                TenantId = 42,
                BrandName = "Tenant Brand",
                LogoUrl = "/custom/logo.png"
            }
        });
        var config = new ConfigurationBuilder().Build();

        var resolver = new TenantBrandingResolver(repo, new TestImageLinkSigner(), config);
        var resolved = await resolver.ResolveAsync("42");

        Assert.Equal(string.Empty, resolved.LogoHtml);
    }

    private sealed class TestImageLinkSigner : IImageLinkSigner
    {
        public string GenerateToken(string scope, string id, string filename, DateTimeOffset expires) => "ok";

        public bool ValidateToken(string token, string scope, string id, string filename) => true;
    }
}
