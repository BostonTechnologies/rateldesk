using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.Branding;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.EmailTemplates;

public sealed class TenantBrandingResolver(
    IRepository<TenantBranding> tenantBrandingRepository,
    IImageLinkSigner imageLinkSigner,
    IConfiguration configuration,
    IInstanceBrandingProvider? instanceBrandingProvider = null) : ITenantBrandingResolver
{
    private static readonly TimeSpan EmailLogoTokenLifetime = TimeSpan.FromDays(30);
    private static readonly Regex TenantLogoPathPattern = new(
        "^/api/tenants/(?<tenantId>[^/]+)/branding/logo/(?<filename>[^/?#]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<TenantBrandingResolved> ResolveAsync(
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var defaults = await BuildDefaultAsync(cancellationToken);
        if (!int.TryParse(tenantId, out var parsedTenantId))
            return defaults;

        var branding = (await tenantBrandingRepository.GetAllAsync())
            .FirstOrDefault(x => x.TenantId == parsedTenantId);
        if (branding is null)
            return defaults;

        var logoHtml = BuildLogoHtml(branding.LogoUrl, defaults.LogoHtml, defaults.TemplateBrand.ApplicationUrl);
        return new TenantBrandingResolved
        {
            BrandName = string.IsNullOrWhiteSpace(branding.BrandName) ? defaults.BrandName : branding.BrandName,
            LogoHtml = logoHtml,
            FooterHtml = string.IsNullOrWhiteSpace(branding.FooterHtml) ? defaults.FooterHtml : branding.FooterHtml,
            PrimaryColor = string.IsNullOrWhiteSpace(branding.PrimaryColor) ? defaults.PrimaryColor : branding.PrimaryColor,
            FromName = string.IsNullOrWhiteSpace(branding.FromName) ? defaults.FromName : branding.FromName,
            ReplyTo = string.IsNullOrWhiteSpace(branding.ReplyTo) ? defaults.ReplyTo : branding.ReplyTo,
            TemplateBrand = defaults.TemplateBrand
        };
    }

    private async Task<TenantBrandingResolved> BuildDefaultAsync(CancellationToken cancellationToken)
    {
        var brand = instanceBrandingProvider is null
            ? new InstanceBrandingSnapshot(
                configuration["EmailBrand:BrandName"] ?? "RatelDesk", "", configuration["PublicWebAppUrl"] ?? "", "", "", "",
                configuration["EmailBrand:LogoUrl"] ?? "/branding/rateldesk-wordmark.webp", "/branding/rateldesk-mark.webp", "/favicon.ico",
                configuration["EmailBrand:FromName"] ?? configuration["EmailBrand:BrandName"] ?? "RatelDesk", "Service management")
            : await instanceBrandingProvider.GetEffectiveAsync(cancellationToken);
        var defaultBrandName = brand.ApplicationName;
        var defaultLogoHtml = configuration["EmailBrand:LogoHtml"] ?? BuildDefaultLogoHtml(brand.ApplicationUrl);
        var defaultFooterHtml = configuration["EmailBrand:FooterHtml"] ?? """
            <div style="margin:0;">
              This message was sent by the support platform. You can reply to ticket emails to add an update.
            </div>
            """;
        var defaultColor = configuration["EmailBrand:PrimaryColor"] ?? "#0ea5e9";
        var defaultReplyTo = configuration["EmailBrand:ReplyTo"] ?? string.Empty;
        var defaultFromName = configuration["EmailBrand:FromName"] ?? brand.EmailFromDisplayName;

        return new TenantBrandingResolved
        {
            BrandName = defaultBrandName,
            LogoHtml = defaultLogoHtml,
            FooterHtml = defaultFooterHtml,
            PrimaryColor = defaultColor,
            FromName = defaultFromName,
            ReplyTo = defaultReplyTo,
            TemplateBrand = new EmailBrandingTemplateContext
            {
                ApplicationName = brand.ApplicationName,
                OrganizationName = brand.OrganizationName,
                ApplicationUrl = brand.ApplicationUrl,
                OrganizationUrl = brand.OrganizationUrl,
                SupportUrl = brand.SupportUrl,
                SupportEmail = brand.SupportEmail,
                LogoUrl = brand.LogoUrl,
                EmailFromDisplayName = brand.EmailFromDisplayName,
                Tagline = brand.Tagline
            }
        };
    }

    private string BuildLogoHtml(string? logoUrl, string fallback, string? applicationUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl))
            return fallback;

        var resolvedUrl = ResolveEmailLogoUrl(logoUrl.Trim(), applicationUrl);
        if (string.IsNullOrWhiteSpace(resolvedUrl))
            return fallback;

        var encodedUrl = HtmlEncoder.Default.Encode(resolvedUrl);
        return $"<img src=\"{encodedUrl}\" alt=\"RatelDesk\" width=\"220\" style=\"display:block; width:220px; max-width:100%; height:auto; border:0;\" />";
    }

    private string? ResolveEmailLogoUrl(string logoUrl, string? applicationUrl)
    {
        if (TryBuildFreshTenantLogoUrl(logoUrl, applicationUrl, out var refreshedUrl))
            return refreshedUrl;

        if (TryCreateHttpAbsoluteUri(logoUrl, out var absoluteUri))
            return absoluteUri.ToString();

        return BuildAbsoluteUrl(logoUrl, applicationUrl);
    }

    private bool TryBuildFreshTenantLogoUrl(string logoUrl, string? applicationUrl, out string? refreshedUrl)
    {
        refreshedUrl = null;
        var path = ExtractPath(logoUrl);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var match = TenantLogoPathPattern.Match(path);
        if (!match.Success)
            return false;

        var tenantId = Uri.UnescapeDataString(match.Groups["tenantId"].Value);
        var filename = Uri.UnescapeDataString(match.Groups["filename"].Value);
        var token = imageLinkSigner.GenerateToken(
            "tenant-brand",
            tenantId,
            filename,
            DateTimeOffset.UtcNow.Add(EmailLogoTokenLifetime));

        var relative =
            $"/api/tenants/{Uri.EscapeDataString(tenantId)}/branding/logo/{Uri.EscapeDataString(filename)}?token={Uri.EscapeDataString(token)}";
        var baseUrl = ExtractBaseUrl(logoUrl) ?? GetPublicApiBaseUrl(applicationUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        refreshedUrl = $"{baseUrl.TrimEnd('/')}{relative}";
        return true;
    }

    private static string? ExtractPath(string logoUrl)
    {
        if (TryCreateHttpAbsoluteUri(logoUrl, out var absoluteUri))
            return absoluteUri.AbsolutePath;

        var queryIndex = logoUrl.IndexOfAny(['?', '#']);
        var path = queryIndex >= 0 ? logoUrl[..queryIndex] : logoUrl;
        return path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";
    }

    private static string? ExtractBaseUrl(string logoUrl)
    {
        if (!TryCreateHttpAbsoluteUri(logoUrl, out var absoluteUri))
            return null;

        return absoluteUri.IsDefaultPort
            ? $"{absoluteUri.Scheme}://{absoluteUri.Host}"
            : $"{absoluteUri.Scheme}://{absoluteUri.Host}:{absoluteUri.Port}";
    }

    private string? BuildAbsoluteUrl(string logoUrl, string? applicationUrl)
    {
        var baseUrl = GetPublicApiBaseUrl(applicationUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var normalizedPath = logoUrl.StartsWith("/", StringComparison.Ordinal) ? logoUrl : $"/{logoUrl}";
        return $"{baseUrl.TrimEnd('/')}{normalizedPath}";
    }

    private string? GetPublicApiBaseUrl(string? applicationUrl) =>
        (string.IsNullOrWhiteSpace(configuration["StorageOptions:PublicApiBaseUrl"])
            ? applicationUrl
            : configuration["StorageOptions:PublicApiBaseUrl"])?.TrimEnd('/');

    private static bool TryCreateHttpAbsoluteUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private string BuildDefaultLogoHtml(string? applicationUrl)
    {
        var configuredLogoUrl = configuration["EmailBrand:LogoUrl"];
        if (!string.IsNullOrWhiteSpace(configuredLogoUrl))
            return BuildLogoHtml(configuredLogoUrl, string.Empty, applicationUrl);

        var publicApiBaseUrl = GetPublicApiBaseUrl(applicationUrl);
        if (string.IsNullOrWhiteSpace(publicApiBaseUrl))
            return string.Empty;

        var configuredLogoPath = configuration["EmailBrand:LogoPath"];
        var logoPath = string.IsNullOrWhiteSpace(configuredLogoPath)
            ? "/email-brand/rateldesk-email-wordmark.png"
            : configuredLogoPath;
        var normalizedPath = logoPath.StartsWith("/", StringComparison.Ordinal) ? logoPath : $"/{logoPath}";
        return BuildLogoHtml($"{publicApiBaseUrl}{normalizedPath}", string.Empty, applicationUrl);
    }
}
