using System.Net;
using System.Text.Json;
using Dodo.Primitives;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.RequestTasks;

public sealed partial class RequestTaskApprovalService(
    HelpdeskDbContext db,
    IRequestFormSchemaParser schemaParser,
    ITicketNotificationService notificationService,
    IPublicTicketLinkSigner signer,
    ILogger<RequestTaskApprovalService> logger) : IRequestTaskApprovalService, IAuthenticatedRequestTaskApprovalService
{
    // Preserve the existing construction contract. Publications are now durable
    // outbox effects, dispatched only after their business transaction commits.
    public RequestTaskApprovalService(HelpdeskDbContext db, IRequestFormSchemaParser schemaParser,
        ITicketNotificationService notificationService, IPublicTicketLinkSigner signer,
        ITimelineEventBus timelineEventBus, ILogger<RequestTaskApprovalService> logger)
        : this(db, schemaParser, notificationService, signer, logger) { }

    private const int DefaultApprovalDays = 7;
    private const int MaxRejectionReasonLength = 1000;

    public async Task<RequestTask> StartApprovalAsync(RequestTask task, CancellationToken ct)
    {
        if (task.Type != RequestTaskType.Approval)
        {
            throw new InvalidOperationException("Task is not an approval task.");
        }

        if (task.Status is not (RequestTaskStatus.Pending or RequestTaskStatus.PendingApproval))
        {
            throw new InvalidOperationException("Approval task must be pending to start.");
        }

        var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == task.RequestId, ct)
            ?? throw new KeyNotFoundException("Request not found.");
        var template = await ResolveTemplateAsync(request, task, ct)
            ?? throw new InvalidOperationException("Approval task template was not found.");
        var approvers = NormalizeApprovers(template.ApprovalApprovers);
        if (approvers.Count == 0)
        {
            throw new InvalidOperationException("Approval task requires at least one approver.");
        }

        var now = DateTimeOffset.UtcNow;
        var allowedDays = Math.Clamp(template.ApprovalAllowedDays ?? DefaultApprovalDays, 1, 14);
        var wasAlreadyPendingApproval = task.Status == RequestTaskStatus.PendingApproval;
        task.Status = RequestTaskStatus.PendingApproval;
        task.State = TicketState.PendingApproval;
        task.StartedAt ??= now;
        task.DueAt ??= now.AddDays(allowedDays);
        task.SlaStartedAt = task.StartedAt;
        task.UpdatedAt = DateTime.UtcNow;

        request.State = TicketState.PendingApproval;
        request.WorkflowStatus = "PendingApproval";
        request.WorkflowBlockReason = $"Waiting for approval for task '{task.Name}'.";
        request.WorkflowUpdatedAt = now;
        request.UpdatedAt = DateTime.UtcNow;

        db.RequestTasks.Update(task);
        db.Requests.Update(request);

        var existingEmails = await db.RequestTaskApprovals
            .Where(x => x.RequestTaskId == task.Id)
            .Select(x => x.ApproverEmail)
            .ToListAsync(ct);
        var existing = existingEmails.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var createdRows = 0;
        foreach (var approver in approvers.Where(x => !existing.Contains(x.Email)))
        {
            db.RequestTaskApprovals.Add(new RequestTaskApproval
            {
                Id = Uuid.CreateVersion7().ToString(),
                RequestId = request.Id,
                RequestTaskId = task.Id,
                ApproverSource = approver.Source,
                ApproverId = approver.Id,
                ApproverName = string.IsNullOrWhiteSpace(approver.Name) ? approver.Email : approver.Name,
                ApproverEmail = approver.Email,
                OrganizationId = approver.OrganizationId,
                OrganizationName = approver.OrganizationName,
                Status = RequestTaskApprovalStatus.Pending,
                CreatedAtUtc = now
            });
            createdRows++;
        }

        if (wasAlreadyPendingApproval && createdRows > 0)
        {
            logger.LogWarning(
                "Recovered missing request approval row(s). RequestId={RequestId} RequestTaskId={RequestTaskId} CreatedRows={CreatedRows}",
                request.Id,
                task.Id,
                createdRows);
        }

        await db.SaveChangesAsync(ct);

        var approvalRows = await db.RequestTaskApprovals
            .Where(x => x.RequestTaskId == task.Id && x.Status == RequestTaskApprovalStatus.Pending)
            .ToListAsync(ct);
        foreach (var approval in approvalRows.Where(x => x.TokenSentAtUtc is null))
        {
            await SendApprovalEmailAsync(request, task, approval, approvalRows, ct);
        }

        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<PublicRequestTaskApprovalDto?> GetPublicApprovalAsync(
        string trackingId,
        string taskId,
        string email,
        string token,
        CancellationToken ct)
    {
        var loaded = await LoadSignedApprovalAsync(trackingId, taskId, email, token, ct);
        if (loaded is null)
        {
            return null;
        }

        var (request, task, approval) = loaded.Value;
        if (approval.ViewedAtUtc is null)
        {
            approval.ViewedAtUtc = DateTimeOffset.UtcNow;
            await AddApprovalTimelineEventAsync(
                request,
                approval,
                $"Approval link viewed by {approval.ApproverName} ({approval.ApproverEmail}) for task '{task.Name}'.",
                ct);
            await db.SaveChangesAsync(ct);
        }

        return new PublicRequestTaskApprovalDto
        {
            RequestId = request.Id,
            RequestTaskId = task.Id,
            TrackingId = request.TrackingId,
            RequestTitle = request.Title,
            RequestDescription = request.Description,
            TaskName = task.Name,
            ApproverName = approval.ApproverName,
            ApproverEmail = approval.ApproverEmail,
            ApprovalStatus = approval.Status,
            DueAt = task.DueAt,
            ReviewedAtUtc = approval.ReviewedAtUtc,
            ViewedAtUtc = approval.ViewedAtUtc,
            CanReview = task.Status == RequestTaskStatus.PendingApproval && approval.Status == RequestTaskApprovalStatus.Pending &&
                        (task.DueAt is null || task.DueAt > DateTimeOffset.UtcNow),
            PayloadFields = BuildPayloadFields(request.PayloadJson)
        };
    }

    public async Task<RequestTaskApprovalReviewResult> ApproveAsync(string trackingId, string taskId, string email, string token, CancellationToken ct)
    {
        return await ReviewDecisionAsync(taskId, () => LoadSignedApprovalAsync(trackingId, taskId, email, token, ct),
            true, null, null, ct);
    }

    private async Task<RequestTaskApprovalReviewResult> ApproveLoadedAsync(Request request, RequestTask task, RequestTaskApproval approval, CancellationToken ct)
    {
        if (approval.Status == RequestTaskApprovalStatus.Approved || task.Status == RequestTaskStatus.Completed)
        {
            return RequestTaskApprovalReviewResult.Succeeded(request.Id, task.Id, RequestTaskApprovalStatus.Approved);
        }

        if (approval.Status != RequestTaskApprovalStatus.Pending || task.Status != RequestTaskStatus.PendingApproval)
        {
            return RequestTaskApprovalReviewResult.Failed("Approval is no longer pending.");
        }

        var now = DateTimeOffset.UtcNow;
        approval.Status = RequestTaskApprovalStatus.Approved;
        approval.ReviewedAtUtc = now;
        approval.ViewedAtUtc ??= now;
        var otherApprovals = await db.RequestTaskApprovals
            .Where(x => x.RequestTaskId == task.Id && x.Id != approval.Id && x.Status == RequestTaskApprovalStatus.Pending)
            .ToListAsync(ct);
        foreach (var other in otherApprovals)
        {
            other.Status = RequestTaskApprovalStatus.Superseded;
            other.ReviewedAtUtc = now;
        }

        task.Status = RequestTaskStatus.Completed;
        task.State = TicketState.Resolved;
        task.CompletedAt = now;
        task.ResultJson = $"Approved by {approval.ApproverName} ({approval.ApproverEmail}).";
        task.UpdatedAt = DateTime.UtcNow;

        request.WorkflowStatus = null;
        request.WorkflowBlockReason = null;
        request.WorkflowUpdatedAt = now;
        request.UpdatedAt = DateTime.UtcNow;

        await AddApprovalTimelineEventAsync(
            request,
            approval,
            $"Approval granted by {approval.ApproverName} ({approval.ApproverEmail}) for task '{task.Name}'.",
            ct);

        await db.SaveChangesAsync(ct);
        return RequestTaskApprovalReviewResult.Succeeded(request.Id, task.Id, approval.Status);
    }

    public async Task<RequestTaskApprovalReviewResult> RejectAsync(
        string trackingId,
        string taskId,
        string email,
        string token,
        string? reason,
        CancellationToken ct)
    {
        return await ReviewDecisionAsync(taskId, () => LoadSignedApprovalAsync(trackingId, taskId, email, token, ct),
            false, reason, null, ct);
    }

    private async Task<RequestTaskApprovalReviewResult> RejectLoadedAsync(Request request, RequestTask task, RequestTaskApproval approval, string? reason, CancellationToken ct)
    {
        if (approval.Status == RequestTaskApprovalStatus.Rejected || request.WorkflowStatus == "Cancelled")
        {
            return RequestTaskApprovalReviewResult.Succeeded(request.Id, task.Id, RequestTaskApprovalStatus.Rejected);
        }

        if (approval.Status != RequestTaskApprovalStatus.Pending || task.Status != RequestTaskStatus.PendingApproval)
        {
            return RequestTaskApprovalReviewResult.Failed("Approval is no longer pending.");
        }

        var rejectionReason = NormalizeRejectionReason(reason);
        if (rejectionReason is null)
        {
            return RequestTaskApprovalReviewResult.Failed("A rejection reason is required.");
        }

        var now = DateTimeOffset.UtcNow;
        approval.Status = RequestTaskApprovalStatus.Rejected;
        approval.ReviewedAtUtc = now;
        approval.ViewedAtUtc ??= now;
        await SupersedePendingApprovalsAsync(task.Id, approval.Id, now, ct);
        var workflowReason = $"Approval rejected by {approval.ApproverName} ({approval.ApproverEmail}). Reason: {rejectionReason}";
        await CancelRequestAsync(request, task, workflowReason, now, ct);
        await AddApprovalTimelineEventAsync(
            request,
            approval,
            $"Approval rejected by {approval.ApproverName} ({approval.ApproverEmail}) for task '{task.Name}'. Reason: {rejectionReason}",
            ct);
        await SendDeclinedEmailToRequesterAsync(request, task, approval, rejectionReason, ct);
        await db.SaveChangesAsync(ct);
        return RequestTaskApprovalReviewResult.Succeeded(request.Id, task.Id, approval.Status);
    }

    public async Task<int> RetryUnsentApprovalEmailsAsync(CancellationToken ct)
    {
        await RecoverMissingPendingApprovalRowsAsync(ct);

        var unsent = await db.RequestTaskApprovals
            .Where(x => x.Status == RequestTaskApprovalStatus.Pending && x.TokenSentAtUtc == null)
            .Join(
                db.RequestTasks.Where(x => x.Type == RequestTaskType.Approval && x.Status == RequestTaskStatus.PendingApproval),
                approval => approval.RequestTaskId,
                task => task.Id,
                (approval, task) => new { Approval = approval, Task = task })
            .ToListAsync(ct);

        var sentCount = 0;
        foreach (var row in unsent)
        {
            var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == row.Approval.RequestId, ct);
            if (request is null)
            {
                continue;
            }

            var approvalRows = await db.RequestTaskApprovals
                .Where(x => x.RequestTaskId == row.Task.Id && x.Status == RequestTaskApprovalStatus.Pending)
                .ToListAsync(ct);

            if (await SendApprovalEmailAsync(request, row.Task, row.Approval, approvalRows, ct))
            {
                sentCount++;
            }
        }

        if (sentCount > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return sentCount;
    }

    private async Task RecoverMissingPendingApprovalRowsAsync(CancellationToken ct)
    {
        var tasksMissingRows = await db.RequestTasks
            .Where(x => x.Type == RequestTaskType.Approval && x.Status == RequestTaskStatus.PendingApproval)
            .Where(task => !db.RequestTaskApprovals.Any(approval => approval.RequestTaskId == task.Id))
            .ToListAsync(ct);

        foreach (var task in tasksMissingRows)
        {
            await StartApprovalAsync(task, ct);
        }
    }

    public async Task<int> ExpireOverdueApprovalsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = db.RequestTasks.Where(x =>
            x.Type == RequestTaskType.Approval && x.Status == RequestTaskStatus.PendingApproval);
        var overdue = db.Database.IsSqlite()
            ? pending.Where(x => EF.Property<long?>(x, "DueAtUtcTicks") <= now.UtcTicks)
            : pending.Where(x => x.DueAt.HasValue && x.DueAt <= now);
        var taskIds = await overdue.Select(task => task.Id).ToListAsync(ct);
        var expired = 0;
        foreach (var taskId in taskIds)
        {
            if (await ExpireTaskAsync(taskId, now, ct)) expired++;
        }

        if (expired > 0)
        {
            logger.LogInformation("Expired {ExpiredCount} request approval task(s).", expired);
        }

        return expired;
    }

    private async Task<(Request Request, RequestTask Task, RequestTaskApproval Approval)?> LoadSignedApprovalAsync(
        string trackingId,
        string taskId,
        string email,
        string token,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackingId)
            || string.IsNullOrWhiteSpace(taskId)
            || string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(token)
            || !signer.ValidateToken(token, $"{trackingId}:{taskId}", email))
        {
            return null;
        }

        var request = await db.Requests.FirstOrDefaultAsync(x => x.TrackingId == trackingId, ct);
        if (request is null)
        {
            return null;
        }

        var task = await db.RequestTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.RequestId == request.Id, ct);
        if (task is null)
        {
            return null;
        }

        var normalizedEmail = email.Trim().ToLower();
        var approval = await db.RequestTaskApprovals
            .FirstOrDefaultAsync(x => x.RequestTaskId == task.Id && x.ApproverEmail.ToLower() == normalizedEmail, ct);
        return approval is null ? null : (request, task, approval);
    }

    private async Task<RequestTaskTemplateModel?> ResolveTemplateAsync(Request request, RequestTask task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RequestFormId) || !Guid.TryParse(task.TemplateId, out var templateId))
        {
            return null;
        }

        var form = await db.RequestForms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.RequestFormId, ct);
        if (form is null)
        {
            return null;
        }

        return schemaParser.Parse(form.JsonSchema).Tasks.FirstOrDefault(x => x.Id == templateId);
    }

    private static List<RequestTaskApprovalApproverModel> NormalizeApprovers(IEnumerable<RequestTaskApprovalApproverModel>? approvers)
        => (approvers ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .Select(x => new RequestTaskApprovalApproverModel
            {
                Source = string.Equals(x.Source, "User", StringComparison.OrdinalIgnoreCase) ? "User" : "Customer",
                Id = x.Id?.Trim() ?? string.Empty,
                Name = string.IsNullOrWhiteSpace(x.Name) ? x.Email.Trim() : x.Name.Trim(),
                Email = x.Email.Trim(),
                OrganizationId = string.IsNullOrWhiteSpace(x.OrganizationId) ? null : x.OrganizationId.Trim(),
                OrganizationName = string.IsNullOrWhiteSpace(x.OrganizationName) ? null : x.OrganizationName.Trim()
            })
            .DistinctBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task SupersedePendingApprovalsAsync(string taskId, string exceptApprovalId, DateTimeOffset now, CancellationToken ct)
    {
        var others = await db.RequestTaskApprovals
            .Where(x => x.RequestTaskId == taskId && x.Id != exceptApprovalId && x.Status == RequestTaskApprovalStatus.Pending)
            .ToListAsync(ct);
        foreach (var other in others)
        {
            other.Status = RequestTaskApprovalStatus.Superseded;
            other.ReviewedAtUtc = now;
        }
    }

    private async Task CancelRequestAsync(Request request, RequestTask task, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var tasks = await db.RequestTasks
            .Where(x => x.RequestId == request.Id)
            .ToListAsync(ct);
        if (!tasks.Any(x => x.Id == task.Id))
        {
            tasks.Add(task);
        }

        foreach (var requestTask in tasks.Where(x => !IsTerminalTaskStatus(x.Status)))
        {
            requestTask.Status = RequestTaskStatus.Cancelled;
            requestTask.State = TicketState.OnHold;
            requestTask.FailureReason = reason;
            requestTask.ResultJson = reason;
            requestTask.UpdatedAt = DateTime.UtcNow;
        }

        request.State = TicketState.OnHold;
        request.WorkflowStatus = "Cancelled";
        request.WorkflowBlockReason = reason;
        request.WorkflowUpdatedAt = now;
        request.UpdatedAt = DateTime.UtcNow;
        request.ClosedAt = now;
    }

    private static bool IsTerminalTaskStatus(RequestTaskStatus status) =>
        status is RequestTaskStatus.Completed or RequestTaskStatus.Skipped or RequestTaskStatus.Cancelled;

    private static string? NormalizeRejectionReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var normalized = WebUtility.HtmlDecode(reason).Trim();
        return normalized.Length > MaxRejectionReasonLength
            ? normalized[..MaxRejectionReasonLength]
            : normalized;
    }

    private static string FormatApprovers(IEnumerable<RequestTaskApproval> approvals)
        => string.Join(", ", approvals.Select(x => string.IsNullOrWhiteSpace(x.ApproverName)
            ? x.ApproverEmail
            : $"{x.ApproverName} <{x.ApproverEmail}>"));

    private Task AddApprovalTimelineEventAsync(
        Request request,
        RequestTaskApproval approval,
        string message,
        CancellationToken ct)
    {
        var timelineEvent = new TicketTimelineEvent
        {
            TicketId = request.Id,
            CreatedUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = "system",
            CreatedByUserName = "Approval Workflow",
            EventType = TimelineEventType.SystemNotification,
            MessageText = message
        };

        db.TicketTimelineEvents.Add(timelineEvent);
        var publication = new TicketTimelineEventDto
        {
            Id = timelineEvent.Id,
            TicketId = timelineEvent.TicketId,
            CreatedUtc = timelineEvent.CreatedUtc,
            CreatedByUserId = timelineEvent.CreatedByUserId,
            CreatedByUserName = timelineEvent.CreatedByUserName,
            EventType = timelineEvent.EventType,
            MessageText = timelineEvent.MessageText,
            PrimaryRecipient = approval.ApproverEmail,
            Recipients = [approval.ApproverEmail]
        };
        db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect
        {
            EffectKey = $"request-approval:{timelineEvent.Id:N}",
            DispatchGroup = $"request-approval:{request.Id}",
            Kind = MailboxEffectKind.Timeline,
            AvailableUnixMilliseconds = timelineEvent.CreatedUtc.ToUnixTimeMilliseconds(),
            Payload = JsonSerializer.Serialize(publication)
        });
        return Task.CompletedTask;
    }

    private async Task<bool> SendApprovalEmailAsync(
        Request request,
        RequestTask task,
        RequestTaskApproval approval,
        IEnumerable<RequestTaskApproval> approvalRows,
        CancellationToken ct)
    {
        var sent = await notificationService.SendRequestApprovalRequiredAsync(
            request,
            task,
            approval,
            FormatApprovers(approvalRows),
            BuildPayloadHtml(request.PayloadJson),
            request.CcRecipients.Where(x => !string.Equals(x, approval.ApproverEmail, StringComparison.OrdinalIgnoreCase)),
            ct);
        if (sent)
        {
            approval.TokenSentAtUtc = DateTimeOffset.UtcNow;
            return true;
        }

        logger.LogWarning(
            "Request approval email send failed. RequestId={RequestId} RequestTaskId={RequestTaskId} ApprovalId={ApprovalId} ApproverEmail={ApproverEmail}",
            request.Id,
            task.Id,
            approval.Id,
            approval.ApproverEmail);
        return false;
    }

    private async Task SendDeclinedEmailToRequesterAsync(
        Request request,
        RequestTask task,
        RequestTaskApproval approval,
        string rejectionReason,
        CancellationToken ct)
    {
        var recipientEmail = request.RequesterEmail;
        var recipientName = request.RequesterEmail;
        if (string.IsNullOrWhiteSpace(recipientEmail) && !string.IsNullOrWhiteSpace(request.CustomerId))
        {
            var customer = await db.Customers.FirstOrDefaultAsync(x => x.Id == request.CustomerId, ct);
            recipientEmail = customer?.Email;
            recipientName = string.IsNullOrWhiteSpace(customer?.Name) ? customer?.Email : customer.Name;
        }

        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            logger.LogWarning(
                "Skipping request approval declined email because requester email is missing. RequestId={RequestId} RequestTaskId={RequestTaskId} ApprovalId={ApprovalId}",
                request.Id,
                task.Id,
                approval.Id);
            return;
        }

        var sent = await notificationService.SendRequestApprovalDeclinedAsync(
            request,
            task,
            approval,
            rejectionReason,
            BuildPayloadHtml(request.PayloadJson),
            recipientEmail,
            recipientName ?? recipientEmail,
            request.CcRecipients.Where(x => !string.Equals(x, recipientEmail, StringComparison.OrdinalIgnoreCase)),
            ct);

        if (!sent)
        {
            logger.LogWarning(
                "Request approval declined email send failed. RequestId={RequestId} RequestTaskId={RequestTaskId} ApprovalId={ApprovalId} RecipientEmail={RecipientEmail}",
                request.Id,
                task.Id,
                approval.Id,
                recipientEmail);
        }
    }

    private static string BuildPayloadHtml(string? payloadJson)
    {
        var fields = BuildPayloadFields(payloadJson);
        if (fields.Count == 0)
        {
            return "<p style=\"margin:0; color:#526173;\">No submitted fields.</p>";
        }

        var rows = fields.Select(field =>
            $"<tr><td style=\"padding:10px 12px; background:#f8fbff; color:#526173; border-bottom:1px solid #dbe5ee; width:36%;\">{WebUtility.HtmlEncode(field.Key)}</td><td style=\"padding:10px 12px; color:#152033; border-bottom:1px solid #dbe5ee;\">{WebUtility.HtmlEncode(field.Value)}</td></tr>");
        return "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"width:100%; border-collapse:collapse; border:1px solid #dbe5ee;\">" +
            string.Join(string.Empty, rows) +
            "</table>";
    }

    private static List<RequestApprovalPayloadFieldDto> BuildPayloadFields(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new List<RequestApprovalPayloadFieldDto>();
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [new RequestApprovalPayloadFieldDto { Key = "payload", Value = doc.RootElement.ToString() }];
            }

            return doc.RootElement.EnumerateObject()
                .Select(x => new RequestApprovalPayloadFieldDto
                {
                    Key = x.Name,
                    Value = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? string.Empty : x.Value.ToString()
                })
                .ToList();
        }
        catch
        {
            return [new RequestApprovalPayloadFieldDto { Key = "payload", Value = payloadJson }];
        }
    }
}

public sealed class RequestTaskApprovalTimeoutProcessor(IRequestTaskApprovalService approvalService) : IRequestTaskApprovalTimeoutProcessor
{
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        await approvalService.RetryUnsentApprovalEmailsAsync(ct);
        return await approvalService.ExpireOverdueApprovalsAsync(ct);
    }
}
