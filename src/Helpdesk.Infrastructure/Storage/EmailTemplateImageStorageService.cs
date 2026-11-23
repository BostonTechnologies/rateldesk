using Helpdesk.Application.Services.Branding;
using System.Text.RegularExpressions;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.WorkLogs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Storage;

public sealed class EmailTemplateImageStorageService(
    IHostEnvironment env,
    IOptions<StorageOptions> options,
    IImageLinkSigner imageLinkSigner,
    ILogger<EmailTemplateImageStorageService> logger,
    IInstanceBrandingProvider? brandingProvider = null) : IEmailTemplateImageStorageService
{
    private readonly string _storageRoot = options.Value.ResolveRootPath(env.ContentRootPath);
    private readonly string _publicApiBaseUrl = options.Value.PublicApiBaseUrl ?? string.Empty;
    private readonly IImageLinkSigner _imageLinkSigner = imageLinkSigner;
    private readonly ILogger<EmailTemplateImageStorageService> _logger = logger;

    public async Task<string> SaveTemplateInlineImageAsync(
        string templateName,
        string filename,
        byte[] content)
    {
        var safeTemplateName = SanitizePathSegment(templateName);
        var normalized = await SafeInlineImage.NormalizeAsync(filename, content);
        var safeFilename = Path.ChangeExtension(SanitizeFileName(filename), normalized.Extension);
        var root = Path.Combine(_storageRoot, "email-templates", safeTemplateName, "inline");
        var fullPath = Path.Combine(root, safeFilename);
        await SafeInlineImage.SaveAsync(root, safeFilename, normalized.Content);
        _logger.LogInformation("Saved email template inline image. Template={Template} Path={Path}", safeTemplateName, fullPath);

        var expires = DateTimeOffset.UtcNow.AddHours(24);
        var token = _imageLinkSigner.GenerateToken("email-template", safeTemplateName, safeFilename, expires);
        var relative =
            $"/api/email-templates/{Uri.EscapeDataString(safeTemplateName)}/images/{Uri.EscapeDataString(safeFilename)}?token={Uri.EscapeDataString(token)}";

        return await PublicResourceUrl.BuildAsync(relative, _publicApiBaseUrl, brandingProvider);
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var clean = Regex.Replace(value, "[^a-zA-Z0-9_-]", "-");
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
    }

    private static string SanitizeFileName(string value)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim());
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = Guid.NewGuid().ToString("N");

        fileName = fileName.Replace('|', '-');
        return fileName;
    }
}
