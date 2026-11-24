using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Services.Notifications;

public interface ITicketNotificationService
{
    Task<bool> SendNewTicketConfirmationAsync(
        Ticket ticket,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendSelfServiceRequestCreatedAsync(
        Request request,
        RequestForm requestForm,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendSelfServiceRequestCompletedAsync(
        Request request,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendSelfServiceRequestFailedAsync(
        Request request,
        Incident incident,
        string failureReason,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendRequestApprovalRequiredAsync(
        Request request,
        RequestTask task,
        RequestTaskApproval approval,
        string approvers,
        string payloadHtml,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendRequestApprovalDeclinedAsync(
        Request request,
        RequestTask task,
        RequestTaskApproval approval,
        string rejectionReason,
        string payloadHtml,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendTicketResolvedAsync(
        Ticket ticket,
        string recipientEmail,
        string recipientName,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendChangeSubmittedAsync(
        Change change,
        string recipientEmail,
        string recipientName,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendChangeApprovalRequiredAsync(
        Change change,
        ChangeApproval approval,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendChangeApprovedAsync(
        Change change,
        string recipientEmail,
        string recipientName,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendChangeImplementationInProgressAsync(
        Change change,
        string recipientEmail,
        string recipientName,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendChangeImplementedAsync(
        Change change,
        string recipientEmail,
        string recipientName,
        string organizationName,
        string requestedForName,
        string implementorName,
        string approvers,
        string completionState,
        IEnumerable<string>? cc = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendChangeCancelledAsync(
        Change change, string recipientEmail, string recipientName,
        string organizationName, string requestedForName, string implementorName, string approvers,
        CancellationToken cancellationToken);
}
