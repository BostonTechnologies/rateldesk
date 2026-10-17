using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.Organization;

public static class TenantBrandingEndpoints
{
    public static void MapTenantBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/tenants")
            .WithTags("Tenant Branding")
            .RequireAuthorization("HelpdeskAdmin");

        admin.MapGet("/{tenantId:int}/branding", async (
            int tenantId,
            [FromServices] IRepository<TenantBranding> repo,
            [FromServices] IConfiguration config) =>
        {
            var existing = await repo.Query().SingleOrDefaultAsync(x => x.TenantId == tenantId);
            if (existing is not null)
                return Results.Ok(existing);

            return Results.Ok(new TenantBranding
            {
                TenantId = tenantId,
                BrandName = config["EmailBrand:BrandName"] ?? "Helpdesk",
                LogoUrl = null,
                FooterHtml = config["EmailBrand:FooterHtml"] ?? string.Empty,
                PrimaryColor = config["EmailBrand:PrimaryColor"] ?? "#0b5fff",
                FromName = config["EmailBrand:FromName"] ?? config["EmailBrand:BrandName"] ?? "Helpdesk",
                ReplyTo = config["EmailBrand:ReplyTo"] ?? string.Empty,
                UpdatedUtc = DateTime.UtcNow
            });
        });

        admin.MapPut("/{tenantId:int}/branding", async (
            int tenantId,
            [FromBody] TenantBranding dto,
            [FromServices] IRepository<TenantBranding> repo,
            [FromServices] IHtmlSanitizerService sanitizer) =>
        {
            var sanitizedFooter = sanitizer.Sanitize(dto.FooterHtml ?? string.Empty);
            var existing = await repo.Query().SingleOrDefaultAsync(x => x.TenantId == tenantId);
            if (existing is null)
            {
                var created = new TenantBranding
                {
                    TenantId = tenantId,
                    BrandName = dto.BrandName ?? string.Empty,
                    LogoUrl = dto.LogoUrl,
                    FooterHtml = sanitizedFooter,
                    PrimaryColor = dto.PrimaryColor ?? "#0b5fff",
                    FromName = dto.FromName ?? string.Empty,
                    ReplyTo = dto.ReplyTo ?? string.Empty,
                    UpdatedUtc = DateTime.UtcNow
                };

                await repo.CreateAsync(created);
                return Results.Ok(created);
            }

            existing.BrandName = dto.BrandName ?? string.Empty;
            existing.LogoUrl = dto.LogoUrl;
            existing.FooterHtml = sanitizedFooter;
            existing.PrimaryColor = dto.PrimaryColor ?? "#0b5fff";
            existing.FromName = dto.FromName ?? string.Empty;
            existing.ReplyTo = dto.ReplyTo ?? string.Empty;
            existing.UpdatedUtc = DateTime.UtcNow;
            await repo.UpdateAsync(existing);
            return Results.Ok(existing);
        });

        admin.MapPost("/{tenantId:int}/branding/logo", async (
            int tenantId,
            [FromForm] LogoUploadForm upload,
            [FromServices] ITenantBrandAssetStorageService storage,
            [FromServices] IRepository<TenantBranding> repo,
            [FromServices] IConfiguration config) =>
        {
            var file = upload.File;
            if (file is null || file.Length == 0)
                return Results.BadRequest("Image file is required.");
            if (string.IsNullOrWhiteSpace(file.ContentType) || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only image uploads are allowed.");

            await using var stream = file.OpenReadStream();
            await using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var logoUrl = await storage.SaveLogoAsync(tenantId, file.FileName, memory.ToArray());

            var existing = await repo.Query().SingleOrDefaultAsync(x => x.TenantId == tenantId);
            if (existing is null)
            {
                existing = new TenantBranding
                {
                    TenantId = tenantId,
                    BrandName = config["EmailBrand:BrandName"] ?? "Helpdesk",
                    FooterHtml = config["EmailBrand:FooterHtml"] ?? string.Empty,
                    PrimaryColor = config["EmailBrand:PrimaryColor"] ?? "#0b5fff",
                    FromName = config["EmailBrand:FromName"] ?? config["EmailBrand:BrandName"] ?? "Helpdesk",
                    ReplyTo = config["EmailBrand:ReplyTo"] ?? string.Empty,
                    UpdatedUtc = DateTime.UtcNow,
                    LogoUrl = logoUrl
                };
                await repo.CreateAsync(existing);
            }
            else
            {
                existing.LogoUrl = logoUrl;
                existing.UpdatedUtc = DateTime.UtcNow;
                await repo.UpdateAsync(existing);
            }

            return Results.Ok(new { url = logoUrl });
        })
        .Accepts<LogoUploadForm>("multipart/form-data");

        app.MapGet("/api/tenants/{tenantId:int}/branding/logo/{filename}", (
            [FromRoute] int tenantId,
            [FromRoute] string filename,
            IWebHostEnvironment env,
            HttpContext httpContext,
            IImageLinkSigner signer,
            ILoggerFactory loggerFactory,
            IOptions<StorageOptions> storageOptions) =>
        {
            var logger = loggerFactory.CreateLogger("TenantBrandingLogo");
            var safeTenantId = tenantId.ToString();
            var safeFilename = Path.GetFileName(filename);
            if (!string.Equals(filename, safeFilename, StringComparison.Ordinal))
                return Results.BadRequest("Invalid file name.");

            var token = httpContext.Request.Query["token"].ToString();
            if (!signer.ValidateToken(token, "tenant-brand", safeTenantId, safeFilename))
            {
                logger.LogWarning("Tenant branding logo token denied. TenantId={TenantId} File={File}", safeTenantId, safeFilename);
                return Results.Unauthorized();
            }

            var storageRoot = string.IsNullOrWhiteSpace(storageOptions.Value.RootPath)
                ? Path.Combine(env.ContentRootPath, "storage")
                : storageOptions.Value.RootPath;
            var rootPath = Path.Combine(storageRoot, "tenants");
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, safeTenantId, "brand", safeFilename));
            var fullRoot = Path.GetFullPath(rootPath);
            if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
                return Results.BadRequest("Invalid path.");
            if (!File.Exists(fullPath))
                return Results.NotFound();

            logger.LogInformation("Serving tenant branding logo. TenantId={TenantId} Path={Path}", safeTenantId, fullPath);
            var contentType = ContentTypeHelper.GetContentType(safeFilename);
            return Results.File(fullPath, contentType);
        })
        .WithTags("Tenant Branding")
        .WithName("GetTenantBrandLogo")
        .WithSummary("Gets a stored tenant branding logo");
    }

    public sealed class LogoUploadForm
    {
        public IFormFile? File { get; set; }
    }
}
