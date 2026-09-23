using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Helpdesk.Application.Services.Notifications;

public class TicketNotificationService(
    IRepository<EmailTemplate> templateRepository,
    IEmailLayoutResolver layoutResolver,
    ITenantBrandingResolver tenantBrandingResolver,
    IEmailService emailService,
    IEmailTemplateRenderer templateRenderer,
    IPublicTicketLinkSigner publicTicketLinkSigner,
    IConfiguration configuration,
    ILogger<TicketNotificationService> logger)
    : ITicketNotificationService
{
    private const string NewTicketConfirmationTemplate = "NewTicketConfirmation";
    private const string SelfServiceRequestCreatedTemplate = "SelfServiceRequestCreated";
    private const string SelfServiceRequestCompletedTemplate = "SelfServiceRequestCompleted";
    private const string SelfServiceRequestFailedTemplate = "SelfServiceRequestFailed";
    private const string RequestApprovalRequiredTemplate = "RequestApprovalRequired";
    private const string RequestApprovalDeclinedTemplate = "RequestApprovalDeclined";
    private const string RequestResolvedTemplate = "RequestResolved";
    private const string IncidentResolvedTemplate = "IncidentResolved";
    private const string ChangeSubmittedTemplate = "ChangeSubmitted";
    private const string ChangeApprovalRequiredTemplate = "ChangeApprovalRequired";
    private const string ChangeApprovedTemplate = "ChangeApproved";
    private const string ChangeImplementationInProgressTemplate = "ChangeImplementationInProgress";
    private const string ChangeImplementedTemplate = "ChangeImplemented";

    private readonly string _publicWebAppUrl = configuration["PublicWebAppUrl"] ?? string.Empty;
    private readonly IConfiguration _configuration = configuration;

    public async Task<bool> SendNewTicketConfirmationAsync(
        Ticket ticket,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default)
    {
        var ticketType = ResolveTicketType(ticket);
        return await SendTicketTemplateAsync(
            NewTicketConfirmationTemplate,
            ticket,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                context.TicketType = ticketType;
                context.TicketTypeLower = ticketType.ToLowerInvariant();
            },
            cancellationToken);
    }

    public async Task<bool> SendSelfServiceRequestCreatedAsync(
        Request request,
        RequestForm requestForm,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default) =>
        await SendTicketTemplateAsync(
            SelfServiceRequestCreatedTemplate,
            request,
            recipientEmail,
            recipientName,
            cc,
            context => ApplySelfServiceContext(context, request, requestForm),
            cancellationToken);

    public async Task<bool> SendSelfServiceRequestCompletedAsync(
        Request request,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default) =>
        await SendTicketTemplateAsync(
            SelfServiceRequestCompletedTemplate,
            request,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                ApplyRequestContext(context, request);
                context.CompletedAt = FormatTimestamp(request.ClosedAt ?? DateTimeOffset.UtcNow);
            },
            cancellationToken);

    public async Task<bool> SendSelfServiceRequestFailedAsync(
        Request request,
        Incident incident,
        string failureReason,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default) =>
        await SendTicketTemplateAsync(
            SelfServiceRequestFailedTemplate,
            request,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                ApplyRequestContext(context, request);
                context.FailureReason = string.IsNullOrWhiteSpace(failureReason)
                    ? "The automation workflow reported a failure."
                    : failureReason;
                context.IncidentRef = incident.TrackingId ?? string.Empty;
                context.IncidentLink = BuildSignedPublicTicketLink(incident.TrackingId ?? string.Empty, recipientEmail);
            },
            cancellationToken);

    public async Task<bool> SendTicketResolvedAsync(
        Ticket ticket,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default)
    {
        var templateName = ticket is Incident ? IncidentResolvedTemplate : RequestResolvedTemplate;
        return await SendTicketTemplateAsync(
            templateName,
            ticket,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                ApplyTicketContext(context, ticket);
                context.ResolvedAt = FormatTimestamp(ticket.ClosedAt ?? DateTimeOffset.UtcNow);
            },
            cancellationToken);
    }

    public async Task<bool> SendRequestApprovalRequiredAsync(
        Request request,
        RequestTask task,
        RequestTaskApproval approval,
        string approvers,
        string payloadHtml,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default)
    {
        return await SendTicketTemplateAsync(
            RequestApprovalRequiredTemplate,
            request,
            approval.ApproverEmail,
            approval.ApproverName,
            cc,
            context =>
            {
                ApplyRequestContext(context, request);
                context.RequestRequestedBy = FirstNonEmpty(request.RequesterEmail, request.CustomerId) ?? string.Empty;
                context.RequestRequestedFor = FirstNonEmpty(request.RequesterEmail, request.CustomerId) ?? string.Empty;
                context.RequestApprovers = approvers;
                context.RequestPayloadHtml = payloadHtml;
                context.RequestApprovalDueAt = task.DueAt.HasValue ? FormatTimestamp(task.DueAt.Value) : string.Empty;
                context.RequestApprovalLink = BuildSignedRequestApprovalLink(request.TrackingId, task.Id, approval.ApproverEmail, "approve");
                context.RequestRejectLink = BuildSignedRequestApprovalLink(request.TrackingId, task.Id, approval.ApproverEmail, "reject");
                context.TicketLink = context.RequestApprovalLink;
            },
            cancellationToken);
    }

    public async Task<bool> SendRequestApprovalDeclinedAsync(
        Request request,
        RequestTask task,
        RequestTaskApproval approval,
        string rejectionReason,
        string payloadHtml,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default)
    {
        return await SendTicketTemplateAsync(
            RequestApprovalDeclinedTemplate,
            request,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                ApplyRequestContext(context, request);
                context.RequestRequestedBy = FirstNonEmpty(request.RequesterEmail, request.CustomerId) ?? string.Empty;
                context.RequestRequestedFor = FirstNonEmpty(request.RequesterEmail, request.CustomerId) ?? string.Empty;
                context.RequestPayloadHtml = payloadHtml;
                context.RequestApprovalTaskName = task.Name;
                context.RequestApprovalApprover = string.IsNullOrWhiteSpace(approval.ApproverName)
                    ? approval.ApproverEmail
                    : $"{approval.ApproverName} ({approval.ApproverEmail})";
                context.RequestApprovalRejectionReason = rejectionReason;
                context.FailureReason = rejectionReason;
            },
            cancellationToken);
    }

    public async Task<bool> SendChangeSubmittedAsync(
        Change change,
        string recipientEmail,
        string recipientName,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default) =>
        await SendTicketTemplateAsync(
            ChangeSubmittedTemplate,
            change,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                ApplyChangeContext(context, change, organizationName, requestedForName, implementorName, approvers);
                context.ChangeViewLink = BuildSignedChangeApprovalLink(change.TrackingId, recipientEmail);
                context.TicketLink = context.ChangeViewLink;
            },
            cancellationToken);

    public async Task<bool> SendChangeApprovalRequiredAsync(
        Change change,
        ChangeApproval approval,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default) =>
        await SendTicketTemplateAsync(
            ChangeApprovalRequiredTemplate,
            change,
            approval.ApproverEmail,
            approval.ApproverName,
            cc,
            context =>
            {
                ApplyChangeContext(context, change, organizationName, requestedForName, implementorName, approvers);
                context.ChangeApprovalStatus = approval.Status.ToString();
                context.ChangeApprovalLink = BuildSignedChangeApprovalLink(change.TrackingId, approval.ApproverEmail);
                context.ChangeViewLink = context.ChangeApprovalLink;
                context.TicketLink = context.ChangeApprovalLink;
            },
            cancellationToken);

    public async Task<bool> SendChangeApprovedAsync(
        Change change,
        string recipientEmail,
        string recipientName,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default) =>
        await SendTicketTemplateAsync(
            ChangeApprovedTemplate,
            change,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                ApplyChangeContext(context, change, organizationName, requestedForName, implementorName, approvers);
                context.ChangeApprovalStatus = "Approved for implementation";
                context.ChangeViewLink = BuildSignedChangeApprovalLink(change.TrackingId, recipientEmail);
                context.TicketLink = context.ChangeViewLink;
            },
            cancellationToken);

    public async Task<bool> SendChangeImplementationInProgressAsync(
        Change change,
        string recipientEmail,
        string recipientName,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default) =>
        await SendTicketTemplateAsync(
            ChangeImplementationInProgressTemplate,
            change,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                ApplyChangeContext(context, change, organizationName, requestedForName, implementorName, approvers);
                context.ChangeApprovalStatus = "Implementation in progress";
                context.ChangeViewLink = BuildSignedChangeApprovalLink(change.TrackingId, recipientEmail);
                context.TicketLink = context.ChangeViewLink;
            },
            cancellationToken);

    public async Task<bool> SendChangeImplementedAsync(
        Change change,
        string recipientEmail,
        string recipientName,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        string completionState,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default) =>
        await SendTicketTemplateAsync(
            ChangeImplementedTemplate,
            change,
            recipientEmail,
            recipientName,
            cc,
            context =>
            {
                ApplyChangeContext(context, change, organizationName, requestedForName, implementorName, approvers);
                context.ChangeApprovalStatus = "Implemented";
                context.ChangeCompletionState = completionState;
                context.ChangeViewLink = BuildSignedChangeApprovalLink(change.TrackingId, recipientEmail);
                context.TicketLink = context.ChangeViewLink;
            },
            cancellationToken);

    private async Task<bool> SendTicketTemplateAsync(
        string templateName,
        Ticket ticket,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc,
        Action<EmailTemplateContext>? configureContext,
        CancellationToken cancellationToken)
    {
        var safeRecipientEmail = recipientEmail?.Trim() ?? string.Empty;
        var safeRecipientName = string.IsNullOrWhiteSpace(recipientName)
            ? safeRecipientEmail
            : recipientName.Trim();
        var safeTicketRef = ticket.TrackingId ?? string.Empty;
        if (ticket.EmailExclusionReason != TicketEmailExclusionReason.None)
        {
            logger.LogInformation(
                "Skipping {TemplateName} notification for {TrackingId} because ticket email is excluded. Reason={Reason}",
                templateName,
                ticket.TrackingId,
                ticket.EmailExclusionReason);
            return false;
        }

        if (string.IsNullOrWhiteSpace(safeTicketRef) || string.IsNullOrWhiteSpace(safeRecipientEmail))
        {
            logger.LogWarning(
                "Skipping {TemplateName} email because tracking ID or recipient email is missing. TicketId={TicketId} TrackingId={TrackingId} Recipient={Recipient}",
                templateName,
                ticket.Id,
                ticket.TrackingId,
                safeRecipientEmail);
            return false;
        }

        var template = (await templateRepository.GetAllAsync())
            .FirstOrDefault(t => string.Equals(t.Name, templateName, StringComparison.Ordinal));
        if (template is null)
        {
            logger.LogWarning("Email template '{TemplateName}' not found. Cannot send ticket notification.", templateName);
            return false;
        }

        var ticketType = ResolveTicketType(ticket);
        var layout = await layoutResolver.ResolveAsync(template.LayoutId, cancellationToken);
        var branding = await tenantBrandingResolver.ResolveAsync(ticket.OrganizationId, cancellationToken);
        var context = new EmailTemplateContext
        {
            UserName = safeRecipientName,
            TicketRef = safeTicketRef,
            TicketType = ticketType,
            TicketTypeLower = ticketType.ToLowerInvariant(),
            TicketLink = BuildSignedPublicTicketLink(safeTicketRef, safeRecipientEmail, branding.TemplateBrand.ApplicationUrl),
            LayoutHtml = layout?.HtmlContent ?? string.Empty,
            BrandName = branding.BrandName,
            Brand = branding.TemplateBrand,
            LogoHtml = branding.LogoHtml,
            FooterHtml = branding.FooterHtml,
            PrimaryColor = branding.PrimaryColor
        };
        ApplyTicketContext(context, ticket);
        configureContext?.Invoke(context);

        var subject = RenderSubject(template.Subject ?? string.Empty, context);
        var body = templateRenderer.Render(template.HtmlContent ?? string.Empty, context);

        var sent = await emailService.SendEmailAsync(
            new[] { safeRecipientEmail },
            subject,
            body,
            cc,
            cancellationToken,
            ticket.Id,
            fromName: branding.FromName,
            replyTo: branding.ReplyTo);
        if (sent)
        {
            logger.LogInformation(
                "Accepted {TemplateName} notification for {TrackingId} to {Recipient}; queued when durable delivery is enabled.",
                templateName,
                ticket.TrackingId,
                safeRecipientEmail);
        }

        return sent;
    }

    private static string ResolveTicketType(Ticket ticket) => ticket switch
    {
        Incident => "Incident",
        Request => "Request",
        Change => "Change",
        _ => "Ticket"
    };

    private static void ApplySelfServiceContext(EmailTemplateContext context, Request request, RequestForm requestForm)
    {
        ApplyRequestContext(context, request);
        context.ServiceName = FirstNonEmpty(requestForm.Title, context.ServiceName, request.Title) ?? string.Empty;
        context.RequestDescription = FirstNonEmpty(
            request.Description,
            requestForm.Description,
            ExtractSchemaDescription(requestForm.JsonSchema)) ?? string.Empty;
    }

    private static void ApplyRequestContext(EmailTemplateContext context, Request request)
    {
        context.ServiceName = FirstNonEmpty(context.ServiceName, request.Title) ?? string.Empty;
        context.RequestTitle = request.Title ?? string.Empty;
        context.RequestDescription = request.Description ?? string.Empty;
    }

    private static void ApplyTicketContext(EmailTemplateContext context, Ticket ticket)
    {
        context.RequestTitle = ticket.Title ?? string.Empty;
        context.RequestDescription = ticket.Description ?? string.Empty;
        if (ticket is Change change)
        {
            context.ChangeTitle = change.Title ?? string.Empty;
            context.ChangeDescription = change.Description ?? string.Empty;
            context.ChangeType = change.ChangeType ?? string.Empty;
            context.ChangePriority = change.Priority.ToString();
            context.ChangeImplementationStart = FormatTimestamp(change.ImplementationStartAt);
            context.ChangeImplementationEnd = FormatTimestamp(change.ImplementationEndAt);
            context.ChangeApprovalStatus = FormatLifecycleState(change.LifecycleState ?? ChangeLifecycleState.Draft);
            context.ChangeCompletionState = FormatCompletionState(change.LifecycleState ?? ChangeLifecycleState.Draft);
        }
        if (ticket is Request request)
        {
            ApplyRequestContext(context, request);
        }
    }

    private static void ApplyChangeContext(
        EmailTemplateContext context,
        Change change,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers)
    {
        ApplyTicketContext(context, change);
        context.TicketType = "Change";
        context.TicketTypeLower = "change";
        context.ChangeOrganization = organizationName;
        context.ChangeRequestedFor = requestedForName;
        context.ChangeImplementor = implementorName;
        context.ChangeApprovers = approvers;
        ApplyChangeTemplateContext(context, change);
    }

    private static void ApplyChangeTemplateContext(EmailTemplateContext context, Change change)
    {
        ChangeTemplateSnapshot? template = null;
        if (!string.IsNullOrWhiteSpace(change.ChangeTemplateJson))
        {
            try
            {
                template = JsonSerializer.Deserialize<ChangeTemplateSnapshot>(
                    change.ChangeTemplateJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch
            {
                template = null;
            }
        }

        context.ChangeScopeOfChange = template?.ScopeOfChange ?? string.Empty;
        context.ChangeAffectedSystems = FormatHtmlList(template?.AffectedSystems);
        context.ChangeImplementationSteps = FormatHtmlList(template?.ImplementationSteps);
        context.ChangeValidationSteps = FormatHtmlList(template?.ValidationSteps);
        context.ChangeRollbackPlan = template?.RollbackPlan ?? string.Empty;
        context.ChangeRollbackReference = template?.RollbackReference ?? string.Empty;
    }

    private static string FormatHtmlList(IEnumerable<string>? values)
    {
        var items = values?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => $"<li>{WebUtility.HtmlEncode(x.Trim())}</li>")
            .ToList() ?? [];

        return items.Count == 0
            ? string.Empty
            : $"<ul style=\"margin:6px 0 0 18px; padding:0;\">{string.Join(string.Empty, items)}</ul>";
    }

    private static string? ExtractSchemaDescription(System.Text.Json.JsonDocument? schema)
    {
        if (schema?.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
            schema.RootElement.TryGetProperty("description", out var descriptionNode) &&
            descriptionNode.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return descriptionNode.GetString();
        }

        return null;
    }

    private static string RenderSubject(string subject, EmailTemplateContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USER_NAME"] = context.UserName,
            ["TICKET_REF"] = context.TicketRef,
            ["TICKET_TYPE"] = context.TicketType,
            ["TICKET_TYPE_LOWER"] = context.TicketTypeLower,
            ["SERVICE_NAME"] = context.ServiceName,
            ["REQUEST_TITLE"] = context.RequestTitle,
            ["REQUEST_DESCRIPTION"] = context.RequestDescription,
            ["REQUEST_REQUESTED_BY"] = context.RequestRequestedBy,
            ["REQUEST_REQUESTED_FOR"] = context.RequestRequestedFor,
            ["REQUEST_APPROVERS"] = context.RequestApprovers,
            ["REQUEST_PAYLOAD_HTML"] = context.RequestPayloadHtml,
            ["REQUEST_APPROVAL_DUE_AT"] = context.RequestApprovalDueAt,
            ["REQUEST_APPROVAL_LINK"] = context.RequestApprovalLink,
            ["REQUEST_REJECT_LINK"] = context.RequestRejectLink,
            ["REQUEST_APPROVAL_TASK_NAME"] = context.RequestApprovalTaskName,
            ["REQUEST_APPROVAL_APPROVER"] = context.RequestApprovalApprover,
            ["REQUEST_APPROVAL_REJECTION_REASON"] = context.RequestApprovalRejectionReason,
            ["FAILURE_REASON"] = context.FailureReason,
            ["INCIDENT_REF"] = context.IncidentRef,
            ["COMPLETED_AT"] = context.CompletedAt,
            ["RESOLVED_AT"] = context.ResolvedAt,
            ["CHANGE_TITLE"] = context.ChangeTitle,
            ["CHANGE_TYPE"] = context.ChangeType,
            ["CHANGE_PRIORITY"] = context.ChangePriority,
            ["CHANGE_ORGANIZATION"] = context.ChangeOrganization,
            ["CHANGE_REQUESTED_FOR"] = context.ChangeRequestedFor,
            ["CHANGE_IMPLEMENTOR"] = context.ChangeImplementor,
            ["CHANGE_APPROVERS"] = context.ChangeApprovers,
            ["CHANGE_IMPLEMENTATION_START"] = context.ChangeImplementationStart,
            ["CHANGE_IMPLEMENTATION_END"] = context.ChangeImplementationEnd,
            ["CHANGE_APPROVAL_STATUS"] = context.ChangeApprovalStatus,
            ["CHANGE_COMPLETION_STATE"] = context.ChangeCompletionState,
            ["CHANGE_APPROVAL_LINK"] = context.ChangeApprovalLink,
            ["CHANGE_VIEW_LINK"] = context.ChangeViewLink,
            ["CHANGE_SCOPE_OF_CHANGE"] = context.ChangeScopeOfChange,
            ["CHANGE_AFFECTED_SYSTEMS"] = context.ChangeAffectedSystems,
            ["CHANGE_IMPLEMENTATION_STEPS"] = context.ChangeImplementationSteps,
            ["CHANGE_VALIDATION_STEPS"] = context.ChangeValidationSteps,
            ["CHANGE_ROLLBACK_PLAN"] = context.ChangeRollbackPlan,
            ["CHANGE_ROLLBACK_REFERENCE"] = context.ChangeRollbackReference,
            ["UserName"] = context.UserName,
            ["TicketRef"] = context.TicketRef,
            ["TicketType"] = context.TicketType,
            ["TicketTypeLower"] = context.TicketTypeLower,
            ["ServiceName"] = context.ServiceName,
            ["RequestTitle"] = context.RequestTitle,
            ["RequestDescription"] = context.RequestDescription,
            ["RequestRequestedBy"] = context.RequestRequestedBy,
            ["RequestRequestedFor"] = context.RequestRequestedFor,
            ["RequestApprovers"] = context.RequestApprovers,
            ["RequestPayloadHtml"] = context.RequestPayloadHtml,
            ["RequestApprovalDueAt"] = context.RequestApprovalDueAt,
            ["RequestApprovalLink"] = context.RequestApprovalLink,
            ["RequestRejectLink"] = context.RequestRejectLink,
            ["RequestApprovalTaskName"] = context.RequestApprovalTaskName,
            ["RequestApprovalApprover"] = context.RequestApprovalApprover,
            ["RequestApprovalRejectionReason"] = context.RequestApprovalRejectionReason,
            ["FailureReason"] = context.FailureReason,
            ["IncidentRef"] = context.IncidentRef,
            ["CompletedAt"] = context.CompletedAt,
            ["ResolvedAt"] = context.ResolvedAt,
            ["ChangeTitle"] = context.ChangeTitle,
            ["ChangeType"] = context.ChangeType,
            ["ChangePriority"] = context.ChangePriority,
            ["ChangeOrganization"] = context.ChangeOrganization,
            ["ChangeRequestedFor"] = context.ChangeRequestedFor,
            ["ChangeImplementor"] = context.ChangeImplementor,
            ["ChangeApprovers"] = context.ChangeApprovers,
            ["ChangeImplementationStart"] = context.ChangeImplementationStart,
            ["ChangeImplementationEnd"] = context.ChangeImplementationEnd,
            ["ChangeApprovalStatus"] = context.ChangeApprovalStatus,
            ["ChangeCompletionState"] = context.ChangeCompletionState,
            ["ChangeApprovalLink"] = context.ChangeApprovalLink,
            ["ChangeViewLink"] = context.ChangeViewLink,
            ["ChangeScopeOfChange"] = context.ChangeScopeOfChange,
            ["ChangeAffectedSystems"] = context.ChangeAffectedSystems,
            ["ChangeImplementationSteps"] = context.ChangeImplementationSteps,
            ["ChangeValidationSteps"] = context.ChangeValidationSteps,
            ["ChangeRollbackPlan"] = context.ChangeRollbackPlan,
            ["ChangeRollbackReference"] = context.ChangeRollbackReference
        };

        var rendered = subject ?? string.Empty;
        foreach (var (key, value) in values)
        {
            rendered = rendered
                .Replace("{{{" + key + "}}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{{" + key + "}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return rendered;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");

    private static string FormatTimestamp(DateTime? value) =>
        value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'")
            : string.Empty;

    private static string FormatLifecycleState(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.ImplementedSuccess => "Implemented - Success",
        ChangeLifecycleState.ImplementedBackedOut => "Implemented - Backed Out",
        ChangeLifecycleState.ImplementationInProgress => "Implementation In Progress",
        ChangeLifecycleState.ApprovedForImplementation => "Approved For Implementation",
        ChangeLifecycleState.PendingApproval => "Pending Approval",
        _ => state.ToString()
    };

    private static string FormatCompletionState(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.ImplementedSuccess => "Success",
        ChangeLifecycleState.ImplementedBackedOut => "Backed Out",
        _ => string.Empty
    };

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private string BuildSignedPublicTicketLink(string trackingId, string email, string? applicationUrl = null)
    {
        var configuredUrl = string.IsNullOrWhiteSpace(applicationUrl) ? _publicWebAppUrl : applicationUrl;
        var baseUrl = string.IsNullOrWhiteSpace(configuredUrl)
            ? "[YOUR_PUBLIC_URL]"
            : configuredUrl.TrimEnd('/');
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var token = publicTicketLinkSigner.GenerateToken(trackingId, email, expires);
        return $"{baseUrl}/view-ticket/{trackingId}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    private string BuildSignedChangeApprovalLink(string trackingId, string email)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_publicWebAppUrl)
            ? "[YOUR_PUBLIC_URL]"
            : _publicWebAppUrl.TrimEnd('/');
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var token = publicTicketLinkSigner.GenerateToken(trackingId, email, expires);
        return $"{baseUrl}/change-approval/{trackingId}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    private string BuildSignedRequestApprovalLink(string trackingId, string taskId, string email, string action)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_publicWebAppUrl)
            ? "[YOUR_PUBLIC_URL]"
            : _publicWebAppUrl.TrimEnd('/');
        var expires = DateTimeOffset.UtcNow.AddDays(14);
        var token = publicTicketLinkSigner.GenerateToken($"{trackingId}:{taskId}", email, expires);
        return $"{baseUrl}/request-approval/{trackingId}/{taskId}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}&action={Uri.EscapeDataString(action)}";
    }

    private sealed class ChangeTemplateSnapshot
    {
        public string? ScopeOfChange { get; set; }
        public List<string> AffectedSystems { get; set; } = new();
        public List<string> ImplementationSteps { get; set; } = new();
        public List<string> ValidationSteps { get; set; } = new();
        public string? RollbackPlan { get; set; }
        public string? RollbackReference { get; set; }
    }
}
