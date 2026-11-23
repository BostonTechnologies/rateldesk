using Helpdesk.API.Endpoints.Storage;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.Email;

public static class EmailTemplateEndpoints
{
    public static void MapEmailTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/email-templates")
            .WithTags("Email Templates")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async ([FromServices] IRepository<EmailTemplate> repo) =>
            await repo.GetAllAsync());

        group.MapPost("/", async (
            [FromBody] EmailTemplate template,
            [FromServices] IRepository<EmailTemplate> repo,
            [FromServices] IHtmlSanitizerService sanitizer) =>
        {
            template.HtmlContent = SanitizeTemplateHtml(sanitizer, template.HtmlContent);
            template.CreatedUtc = DateTime.UtcNow;
            template.UpdatedUtc = null;
            if (template.Version < 1)
                template.Version = 1;
            await repo.CreateAsync(template);
            return Results.Created($"/api/v1/email-templates/{template.Id}", template);
        });

        group.MapPut("/{id:int}", async (
            int id,
            [FromBody] EmailTemplate template,
            [FromServices] IRepository<EmailTemplate> repo,
            [FromServices] IHtmlSanitizerService sanitizer) =>
        {
            var existing = await repo.GetAsync(id.ToString());
            if (existing is null)
                return Results.NotFound();

            existing.Name = template.Name;
            existing.Subject = template.Subject;
            existing.HtmlContent = SanitizeTemplateHtml(sanitizer, template.HtmlContent);
            existing.LayoutId = template.LayoutId;
            existing.UpdatedUtc = DateTime.UtcNow;
            existing.Version = Math.Max(existing.Version + 1, 1);

            await repo.UpdateAsync(existing);
            return Results.Ok(existing);
        });

        group.MapDelete("/{id:int}", async (int id, [FromServices] IRepository<EmailTemplate> repo) =>
        {
            var existing = await repo.GetAsync(id.ToString());
            if (existing is null)
                return Results.NotFound();
            if (existing.IsSystem)
                return Results.BadRequest("System templates cannot be deleted.");

            await repo.DeleteAsync(id.ToString());
            return Results.NoContent();
        });

        group.MapPost("/preview", async (
            [FromBody] TemplatePreviewRequest request,
            [FromServices] IRepository<EmailTemplate> templateRepository,
            [FromServices] IEmailLayoutResolver layoutResolver,
            [FromServices] ITenantBrandingResolver tenantBrandingResolver,
            [FromServices] IEmailTemplateRenderer templateRenderer,
            [FromServices] ITemplateEngine templateEngine,
            CancellationToken ct) =>
        {
            var template = await ResolveTemplateAsync(templateRepository, request.TemplateId, request.TemplateName);
            if (template is null &&
                string.IsNullOrWhiteSpace(request.DraftHtmlContent) &&
                string.IsNullOrWhiteSpace(request.DraftSubject))
            {
                return Results.NotFound("Template not found.");
            }

            var effectiveSubject = request.DraftSubject ?? template?.Subject ?? string.Empty;
            var effectiveHtml = request.DraftHtmlContent ?? template?.HtmlContent ?? string.Empty;
            var effectiveLayoutId = request.DraftLayoutId ?? template?.LayoutId;

            var sample = request.SampleContext ?? new TemplatePreviewSampleContext();
            var layout = await layoutResolver.ResolveAsync(effectiveLayoutId, ct);
            var branding = await tenantBrandingResolver.ResolveAsync(
                string.IsNullOrWhiteSpace(request.TenantId) ? null : request.TenantId,
                ct);

            var context = new EmailTemplateContext
            {
                UserName = string.IsNullOrWhiteSpace(sample.UserName) ? "John Doe" : sample.UserName!,
                TicketRef = string.IsNullOrWhiteSpace(sample.TicketRef) ? "INC-12345" : sample.TicketRef!,
                TicketLink = string.IsNullOrWhiteSpace(sample.TicketLink)
                    ? "https://example.helpdesk.local/view-ticket/INC-12345"
                    : sample.TicketLink!,
                UpdateMessageHtml = string.IsNullOrWhiteSpace(sample.UpdateMessageHtml)
                    ? "<p>Example update with <strong>formatting</strong>.</p>"
                    : sample.UpdateMessageHtml!,
                UpdateMessageText = sample.UpdateMessageText ?? string.Empty,
                ServiceName = string.IsNullOrWhiteSpace(sample.ServiceName) ? "Disk capacity report" : sample.ServiceName!,
                RequestTitle = string.IsNullOrWhiteSpace(sample.RequestTitle) ? "Disk capacity report" : sample.RequestTitle!,
                RequestDescription = string.IsNullOrWhiteSpace(sample.RequestDescription) ? "Generate a disk capacity report." : sample.RequestDescription!,
                RequestApprovalTaskName = string.IsNullOrWhiteSpace(sample.RequestApprovalTaskName) ? "Technical Approval required" : sample.RequestApprovalTaskName!,
                RequestApprovalApprover = string.IsNullOrWhiteSpace(sample.RequestApprovalApprover) ? "Example Approver (approver@example.com)" : sample.RequestApprovalApprover!,
                RequestApprovalRejectionReason = string.IsNullOrWhiteSpace(sample.RequestApprovalRejectionReason) ? "The submitted request does not include enough implementation detail." : sample.RequestApprovalRejectionReason!,
                FailureReason = string.IsNullOrWhiteSpace(sample.FailureReason) ? "Automation reported a failed run." : sample.FailureReason!,
                IncidentRef = string.IsNullOrWhiteSpace(sample.IncidentRef) ? "INC-12346" : sample.IncidentRef!,
                IncidentLink = string.IsNullOrWhiteSpace(sample.IncidentLink)
                    ? "https://example.helpdesk.local/view-ticket/INC-12346"
                    : sample.IncidentLink!,
                CompletedAt = string.IsNullOrWhiteSpace(sample.CompletedAt) ? "2026-05-15 13:56 UTC" : sample.CompletedAt!,
                ResolvedAt = string.IsNullOrWhiteSpace(sample.ResolvedAt) ? "2026-05-15 14:10 UTC" : sample.ResolvedAt!,
                ChangeTitle = string.IsNullOrWhiteSpace(sample.ChangeTitle) ? "Core firewall firmware update" : sample.ChangeTitle!,
                ChangeDescription = string.IsNullOrWhiteSpace(sample.ChangeDescription) ? "Firmware update to resolve vendor security advisory." : sample.ChangeDescription!,
                ChangeType = string.IsNullOrWhiteSpace(sample.ChangeType) ? "Normal" : sample.ChangeType!,
                ChangePriority = string.IsNullOrWhiteSpace(sample.ChangePriority) ? "High" : sample.ChangePriority!,
                ChangeOrganization = string.IsNullOrWhiteSpace(sample.ChangeOrganization) ? "Example Organization" : sample.ChangeOrganization!,
                ChangeRequestedFor = string.IsNullOrWhiteSpace(sample.ChangeRequestedFor) ? "Jane Customer" : sample.ChangeRequestedFor!,
                ChangeImplementor = string.IsNullOrWhiteSpace(sample.ChangeImplementor) ? "Alex Implementor" : sample.ChangeImplementor!,
                ChangeApprovers = string.IsNullOrWhiteSpace(sample.ChangeApprovers) ? "Taylor Approver, Morgan Reviewer" : sample.ChangeApprovers!,
                ChangeImplementationStart = string.IsNullOrWhiteSpace(sample.ChangeImplementationStart) ? "2026-05-22 20:00 UTC" : sample.ChangeImplementationStart!,
                ChangeImplementationEnd = string.IsNullOrWhiteSpace(sample.ChangeImplementationEnd) ? "2026-05-22 22:00 UTC" : sample.ChangeImplementationEnd!,
                ChangeApprovalLink = string.IsNullOrWhiteSpace(sample.ChangeApprovalLink)
                    ? "https://example.helpdesk.local/change-approval/CHG-12345?email=jane@example.com&token=signed"
                    : sample.ChangeApprovalLink!,
                ChangeViewLink = string.IsNullOrWhiteSpace(sample.ChangeViewLink)
                    ? "https://example.helpdesk.local/change-approval/CHG-12345?email=requester@example.com&token=signed"
                    : sample.ChangeViewLink!,
                ChangeCompletionState = string.IsNullOrWhiteSpace(sample.ChangeCompletionState) ? "Success" : sample.ChangeCompletionState!,
                ChangeScopeOfChange = string.IsNullOrWhiteSpace(sample.ChangeScopeOfChange) ? "Upgrade HA firewall pair firmware." : sample.ChangeScopeOfChange!,
                ChangeAffectedSystems = string.IsNullOrWhiteSpace(sample.ChangeAffectedSystems) ? "<ul><li>Primary firewall</li><li>VPN services</li></ul>" : sample.ChangeAffectedSystems!,
                ChangeImplementationSteps = string.IsNullOrWhiteSpace(sample.ChangeImplementationSteps) ? "<ul><li>Fail over to secondary</li><li>Apply firmware</li><li>Validate HA sync</li></ul>" : sample.ChangeImplementationSteps!,
                ChangeValidationSteps = string.IsNullOrWhiteSpace(sample.ChangeValidationSteps) ? "<ul><li>Confirm VPN login</li><li>Confirm internet egress</li></ul>" : sample.ChangeValidationSteps!,
                ChangeRollbackPlan = string.IsNullOrWhiteSpace(sample.ChangeRollbackPlan) ? "Restore previous firmware image and fail back traffic." : sample.ChangeRollbackPlan!,
                ChangeRollbackReference = string.IsNullOrWhiteSpace(sample.ChangeRollbackReference) ? "KB-CHG-ROLLBACK-001" : sample.ChangeRollbackReference!,
                OrganizationName = string.IsNullOrWhiteSpace(sample.OrganizationName) ? "Example Organization" : sample.OrganizationName!,
                InviteLink = string.IsNullOrWhiteSpace(sample.InviteLink)
                    ? "https://auth.example.local/if/flow/default-recovery/?token=signed"
                    : sample.InviteLink!,
                InviteExpiresAt = string.IsNullOrWhiteSpace(sample.InviteExpiresAt) ? "2026-06-01 14:15 UTC" : sample.InviteExpiresAt!,
                HelpdeskUrl = string.IsNullOrWhiteSpace(sample.HelpdeskUrl) ? "https://helpdesk.example.com" : sample.HelpdeskUrl!,
                LayoutHtml = request.DraftLayoutHtml ?? layout?.HtmlContent ?? string.Empty,
                BrandName = branding.BrandName,
                Brand = branding.TemplateBrand,
                LogoHtml = branding.LogoHtml,
                FooterHtml = branding.FooterHtml,
                PrimaryColor = branding.PrimaryColor
            };

            var subjectModel = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["USER_NAME"] = context.UserName,
                ["TICKET_REF"] = context.TicketRef,
                ["TICKET_LINK"] = context.TicketLink,
                ["UPDATE_MESSAGE"] = !string.IsNullOrWhiteSpace(context.UpdateMessageText)
                    ? context.UpdateMessageText
                    : context.UpdateMessageHtml,
                ["SERVICE_NAME"] = context.ServiceName,
                ["REQUEST_TITLE"] = context.RequestTitle,
                ["REQUEST_DESCRIPTION"] = context.RequestDescription,
                ["REQUEST_APPROVAL_TASK_NAME"] = context.RequestApprovalTaskName,
                ["REQUEST_APPROVAL_APPROVER"] = context.RequestApprovalApprover,
                ["REQUEST_APPROVAL_REJECTION_REASON"] = context.RequestApprovalRejectionReason,
                ["FAILURE_REASON"] = context.FailureReason,
                ["INCIDENT_REF"] = context.IncidentRef,
                ["INCIDENT_LINK"] = context.IncidentLink,
                ["COMPLETED_AT"] = context.CompletedAt,
                ["RESOLVED_AT"] = context.ResolvedAt,
                ["CHANGE_TITLE"] = context.ChangeTitle,
                ["CHANGE_DESCRIPTION"] = context.ChangeDescription,
                ["CHANGE_TYPE"] = context.ChangeType,
                ["CHANGE_PRIORITY"] = context.ChangePriority,
                ["CHANGE_ORGANIZATION"] = context.ChangeOrganization,
                ["CHANGE_REQUESTED_FOR"] = context.ChangeRequestedFor,
                ["CHANGE_IMPLEMENTOR"] = context.ChangeImplementor,
                ["CHANGE_APPROVERS"] = context.ChangeApprovers,
                ["CHANGE_IMPLEMENTATION_START"] = context.ChangeImplementationStart,
                ["CHANGE_IMPLEMENTATION_END"] = context.ChangeImplementationEnd,
                ["CHANGE_APPROVAL_LINK"] = context.ChangeApprovalLink,
                ["CHANGE_VIEW_LINK"] = context.ChangeViewLink,
                ["CHANGE_COMPLETION_STATE"] = context.ChangeCompletionState,
                ["CHANGE_SCOPE_OF_CHANGE"] = context.ChangeScopeOfChange,
                ["CHANGE_AFFECTED_SYSTEMS"] = context.ChangeAffectedSystems,
                ["CHANGE_IMPLEMENTATION_STEPS"] = context.ChangeImplementationSteps,
                ["CHANGE_VALIDATION_STEPS"] = context.ChangeValidationSteps,
                ["CHANGE_ROLLBACK_PLAN"] = context.ChangeRollbackPlan,
                ["CHANGE_ROLLBACK_REFERENCE"] = context.ChangeRollbackReference,
                ["ORGANIZATION_NAME"] = context.OrganizationName,
                ["INVITE_LINK"] = context.InviteLink,
                ["INVITE_EXPIRES_AT"] = context.InviteExpiresAt,
                ["HELPDESK_URL"] = context.HelpdeskUrl,
                ["ACTION_COLOR"] = context.ActionColor,
                ["ACTION_BORDER_COLOR"] = context.ActionBorderColor,
                ["ACTION_TEXT_COLOR"] = context.ActionTextColor,
                ["UserName"] = context.UserName,
                ["TicketRef"] = context.TicketRef,
                ["TicketLink"] = context.TicketLink,
                ["UpdateMessage"] = !string.IsNullOrWhiteSpace(context.UpdateMessageText)
                    ? context.UpdateMessageText
                    : context.UpdateMessageHtml,
                ["ServiceName"] = context.ServiceName,
                ["RequestTitle"] = context.RequestTitle,
                ["RequestDescription"] = context.RequestDescription,
                ["RequestApprovalTaskName"] = context.RequestApprovalTaskName,
                ["RequestApprovalApprover"] = context.RequestApprovalApprover,
                ["RequestApprovalRejectionReason"] = context.RequestApprovalRejectionReason,
                ["FailureReason"] = context.FailureReason,
                ["IncidentRef"] = context.IncidentRef,
                ["IncidentLink"] = context.IncidentLink,
                ["CompletedAt"] = context.CompletedAt,
                ["ResolvedAt"] = context.ResolvedAt,
                ["ChangeTitle"] = context.ChangeTitle,
                ["ChangeDescription"] = context.ChangeDescription,
                ["ChangeType"] = context.ChangeType,
                ["ChangePriority"] = context.ChangePriority,
                ["ChangeOrganization"] = context.ChangeOrganization,
                ["ChangeRequestedFor"] = context.ChangeRequestedFor,
                ["ChangeImplementor"] = context.ChangeImplementor,
                ["ChangeApprovers"] = context.ChangeApprovers,
                ["ChangeImplementationStart"] = context.ChangeImplementationStart,
                ["ChangeImplementationEnd"] = context.ChangeImplementationEnd,
                ["ChangeApprovalLink"] = context.ChangeApprovalLink,
                ["ChangeViewLink"] = context.ChangeViewLink,
                ["ChangeCompletionState"] = context.ChangeCompletionState,
                ["ChangeScopeOfChange"] = context.ChangeScopeOfChange,
                ["ChangeAffectedSystems"] = context.ChangeAffectedSystems,
                ["ChangeImplementationSteps"] = context.ChangeImplementationSteps,
                ["ChangeValidationSteps"] = context.ChangeValidationSteps,
                ["ChangeRollbackPlan"] = context.ChangeRollbackPlan,
                ["ChangeRollbackReference"] = context.ChangeRollbackReference,
                ["OrganizationName"] = context.OrganizationName,
                ["InviteLink"] = context.InviteLink,
                ["InviteExpiresAt"] = context.InviteExpiresAt,
                ["HelpdeskUrl"] = context.HelpdeskUrl,
                ["ActionColor"] = context.ActionColor,
                ["ActionBorderColor"] = context.ActionBorderColor,
                ["ActionTextColor"] = context.ActionTextColor
            };

            var renderedSubject = templateEngine.Render(effectiveSubject, subjectModel);
            var renderedHtml = templateRenderer.Render(effectiveHtml, context);

            return Results.Ok(new TemplatePreviewResponse(renderedSubject, renderedHtml));
        });

        group.MapPost("/{name}/images", async (
            [FromRoute] string name,
            [FromForm] ImageUploadForm upload,
            [FromServices] IEmailTemplateImageStorageService imageStorage) =>
        {
            var file = upload.File;
            if (file is null || file.Length == 0)
                return Results.BadRequest("Image file is required.");

            if (file.Length > SafeInlineImage.MaxInputBytes ||
                !SafeInlineImage.MatchesDeclaredType(file.FileName, file.ContentType))
                return Results.BadRequest("A supported raster image is required.");

            await using var memory = new MemoryStream();
            await file.CopyToAsync(memory);
            string url;
            try
            {
                url = await imageStorage.SaveTemplateInlineImageAsync(name, file.FileName, memory.ToArray());
            }
            catch (InlineImageRejectedException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            return Results.Ok(new { url });
        })
        .Accepts<ImageUploadForm>("multipart/form-data")
        .DisableAntiforgery();

        app.MapGet("/api/email-templates/{templateName}/images/{filename}", (
            [FromRoute] string templateName,
            [FromRoute] string filename,
            IWebHostEnvironment env,
            HttpContext httpContext,
            IImageLinkSigner signer,
            ILoggerFactory loggerFactory,
            IOptions<StorageOptions> storageOptions) =>
        {
            var logger = loggerFactory.CreateLogger("EmailTemplateInlineImage");
            var safeTemplateName = SanitizePathSegment(templateName);
            var safeFilename = Path.GetFileName(filename);
            if (!string.Equals(filename, safeFilename, StringComparison.Ordinal))
                return Results.BadRequest("Invalid file name.");

            var token = httpContext.Request.Query["token"].ToString();
            if (!signer.ValidateToken(token, "email-template", safeTemplateName, safeFilename))
            {
                logger.LogWarning("Template inline image token denied. Template={Template} File={File}", safeTemplateName, safeFilename);
                return Results.Unauthorized();
            }

            var storageRoot = storageOptions.Value.ResolveRootPath(env.ContentRootPath);
            return SignedRasterAsset.Serve(httpContext, Path.Combine(storageRoot, "email-templates", safeTemplateName, "inline"), safeFilename);
        })
        .WithTags("Email Templates")
        .WithName("GetEmailTemplateImage")
        .WithSummary("Gets a stored email template inline image");
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var clean = global::System.Text.RegularExpressions.Regex.Replace(value, "[^a-zA-Z0-9_-]", "-");
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
    }

    private static string SanitizeTemplateHtml(IHtmlSanitizerService sanitizer, string html)
    {
        var sanitized = sanitizer.Sanitize(html ?? string.Empty);
        var imgRegex = new global::System.Text.RegularExpressions.Regex(
            "<img\\b[^>]*?\\bsrc\\s*=\\s*[\"'](?<src>[^\"']+)[\"'][^>]*>",
            global::System.Text.RegularExpressions.RegexOptions.IgnoreCase | global::System.Text.RegularExpressions.RegexOptions.Compiled);

        return imgRegex.Replace(sanitized, match =>
        {
            var src = match.Groups["src"].Value.Trim();
            if (src.StartsWith("/api/email-templates/", StringComparison.OrdinalIgnoreCase))
                return match.Value;
            if (!src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            return string.Empty;
        });
    }

    private static async Task<EmailTemplate?> ResolveTemplateAsync(
        IRepository<EmailTemplate> repository,
        int? templateId,
        string? templateName)
    {
        if (templateId.HasValue)
            return await repository.GetAsync(templateId.Value.ToString());

        if (string.IsNullOrWhiteSpace(templateName))
            return null;

        var all = await repository.GetAllAsync();
        return all.FirstOrDefault(x => string.Equals(x.Name, templateName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record TemplatePreviewRequest
    {
        public int? TemplateId { get; init; }
        public string? TemplateName { get; init; }
        public string? TenantId { get; init; }
        public string? DraftSubject { get; init; }
        public string? DraftHtmlContent { get; init; }
        public string? DraftLayoutHtml { get; init; }
        public int? DraftLayoutId { get; init; }
        public TemplatePreviewSampleContext? SampleContext { get; init; }
    }

    private sealed record TemplatePreviewSampleContext
    {
        public string? UserName { get; init; }
        public string? TicketRef { get; init; }
        public string? TicketLink { get; init; }
        public string? UpdateMessageHtml { get; init; }
        public string? UpdateMessageText { get; init; }
        public string? ServiceName { get; init; }
        public string? RequestTitle { get; init; }
        public string? RequestDescription { get; init; }
        public string? RequestApprovalTaskName { get; init; }
        public string? RequestApprovalApprover { get; init; }
        public string? RequestApprovalRejectionReason { get; init; }
        public string? FailureReason { get; init; }
        public string? IncidentRef { get; init; }
        public string? IncidentLink { get; init; }
        public string? CompletedAt { get; init; }
        public string? ResolvedAt { get; init; }
        public string? ChangeTitle { get; init; }
        public string? ChangeDescription { get; init; }
        public string? ChangeType { get; init; }
        public string? ChangePriority { get; init; }
        public string? ChangeOrganization { get; init; }
        public string? ChangeRequestedFor { get; init; }
        public string? ChangeImplementor { get; init; }
        public string? ChangeApprovers { get; init; }
        public string? ChangeImplementationStart { get; init; }
        public string? ChangeImplementationEnd { get; init; }
        public string? ChangeApprovalLink { get; init; }
        public string? ChangeViewLink { get; init; }
        public string? ChangeCompletionState { get; init; }
        public string? ChangeScopeOfChange { get; init; }
        public string? ChangeAffectedSystems { get; init; }
        public string? ChangeImplementationSteps { get; init; }
        public string? ChangeValidationSteps { get; init; }
        public string? ChangeRollbackPlan { get; init; }
        public string? ChangeRollbackReference { get; init; }
        public string? OrganizationName { get; init; }
        public string? InviteLink { get; init; }
        public string? InviteExpiresAt { get; init; }
        public string? HelpdeskUrl { get; init; }
    }

    private sealed record TemplatePreviewResponse(string Subject, string Html);

    public sealed class ImageUploadForm
    {
        public IFormFile? File { get; set; }
    }
}
