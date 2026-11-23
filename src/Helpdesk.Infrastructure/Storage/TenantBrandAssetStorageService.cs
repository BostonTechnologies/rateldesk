using Helpdesk.Application.Services.Branding;
using System.Text.RegularExpressions;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.WorkLogs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Storage;

public sealed class TenantBrandAssetStorageService(
    IHostEnvironment env,
    IOptions<StorageOptions> options,
    IImageLinkSigner imageLinkSigner,
    ILogger<TenantBrandAssetStorageService> logger,
    IInstanceBrandingProvider? brandingProvider = null) : ITenantBrandAssetStorageService
{
    private readonly string _storageRoot = options.Value.ResolveRootPath(env.ContentRootPath);
    private readonly string _publicApiBaseUrl = options.Value.PublicApiBaseUrl ?? string.Empty;
    private readonly IImageLinkSigner _imageLinkSigner = imageLinkSigner;
    private readonly ILogger<TenantBrandAssetStorageService> _logger = logger;

    public async Task<string> SaveLogoAsync(
        int tenantId,
        string filename,
        byte[] content)
    {
        var safeTenantId = SanitizePathSegment(tenantId.ToString());
        var normalized = await SafeInlineImage.NormalizeAsync(filename, content);
        var safeFilename = Path.ChangeExtension(SanitizeFileName(filename), normalized.Extension);
        var root = Path.Combine(_storageRoot, "tenants", safeTenantId, "brand");
        var fullPath = Path.Combine(root, safeFilename);
        await SafeInlineImage.SaveAsync(root, safeFilename, normalized.Content);
        _logger.LogInformation("Saved tenant branding logo. TenantId={TenantId} Path={Path}", safeTenantId, fullPath);

        var expires = DateTimeOffset.UtcNow.AddHours(24);
        var token = _imageLinkSigner.GenerateToken("tenant-brand", safeTenantId, safeFilename, expires);
        var relative =
            $"/api/tenants/{Uri.EscapeDataString(safeTenantId)}/branding/logo/{Uri.EscapeDataString(safeFilename)}?token={Uri.EscapeDataString(token)}";

        return await PublicResourceUrl.BuildAsync(relative, _publicApiBaseUrl, brandingProvider);
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0";

        var clean = Regex.Replace(value, "[^a-zA-Z0-9_-]", "-");
        return string.IsNullOrWhiteSpace(clean) ? "0" : clean;
    }

    private static string SanitizeFileName(string value)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim());
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = Guid.NewGuid().ToString("N");

        return fileName.Replace('|', '-');
    }
}
