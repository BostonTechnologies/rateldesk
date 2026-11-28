namespace Helpdesk.API.Email;

public static class EmailSeedDefaults
{
    public const int BrandedBaselineVersion = 10;
    public const string DefaultLayoutName = "Default";

    public static readonly IReadOnlyDictionary<string, string> TemplateSubjects = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["NewTicketConfirmation"] = "Your {{{TICKET_TYPE}}} Has Been Created {{{TICKET_REF}}}",
        ["SupportTicketCreatedUnassigned"] = "New Unassigned Ticket {{{TICKET_REF}}}: {{{REQUEST_TITLE}}}",
        ["SupportTicketAssigned"] = "Ticket Assigned To You {{{TICKET_REF}}}: {{{REQUEST_TITLE}}}",
        ["SupportTicketAssignedToSupportGroup"] = "{{{TICKET_REF}}} assigned to your support group",
        ["TicketUpdated"] = "Update from Helpdesk for {{{TICKET_REF}}}",
        ["SelfServiceRequestCreated"] = "Self-Service Request Created {{{TICKET_REF}}}: {{{SERVICE_NAME}}}",
        ["SelfServiceRequestCompleted"] = "Self-Service Request Completed {{{TICKET_REF}}}: {{{SERVICE_NAME}}}",
        ["SelfServiceRequestFailed"] = "Self-Service Request Failed {{{TICKET_REF}}}: {{{SERVICE_NAME}}}",
        ["RequestApprovalRequired"] = "Approval Required {{{TICKET_REF}}}: {{{REQUEST_TITLE}}}",
        ["RequestApprovalDeclined"] = "Request Approval Declined {{{TICKET_REF}}}: {{{REQUEST_TITLE}}}",
        ["RequestResolved"] = "Request Resolved {{{TICKET_REF}}}",
        ["IncidentResolved"] = "Incident Resolved {{{TICKET_REF}}}",
        ["CustomerInvitation"] = "Your RatelDesk invitation",
        ["AccessDenied"] = "Your Email Could Not Be Processed",
        ["WelcomeNewUser"] = "Welcome to RatelDesk",
        ["ChangeSubmitted"] = "Change Submitted {{{TICKET_REF}}}: {{{CHANGE_TITLE}}}",
        ["ChangeReminder"] = "Change Reminder {{{TICKET_REF}}}: Starts Tomorrow",
        ["ChangeApprovalRequired"] = "Change Approval Required {{{TICKET_REF}}}: {{{CHANGE_TITLE}}}",
        ["ChangeApproved"] = "Change Approved {{{TICKET_REF}}}: {{{CHANGE_TITLE}}}",
        ["ChangeRejected"] = "Change Rejected {{{TICKET_REF}}}: {{{CHANGE_TITLE}}}",
        ["ChangeImplementationInProgress"] = "Change Implementation In Progress {{{TICKET_REF}}}: {{{CHANGE_TITLE}}}",
        ["ChangeImplemented"] = "Change Implemented - {{{CHANGE_COMPLETION_STATE}}} {{{TICKET_REF}}}: {{{CHANGE_TITLE}}}",
        ["ChangeUpdate"] = "Change Update {{{TICKET_REF}}}: {{{CHANGE_TITLE}}}"
    };

    public static IReadOnlyCollection<string> TemplateNames => TemplateSubjects.Keys.ToArray();

    public static string GenerateSubjectFromName(string name) =>
        TemplateSubjects.TryGetValue(name, out var subject)
            ? subject
            : "Helpdesk Notification";

    public static bool ShouldUpgradeDefaultTemplate(bool isSystem, int version, string? htmlContent) =>
        (isSystem && version < BrandedBaselineVersion) || IsLegacyDefaultTemplateHtml(htmlContent);

    public static bool ShouldUpgradeDefaultLayout(bool isSystem, int version, string? htmlContent) =>
        (isSystem && version < BrandedBaselineVersion) || IsLegacyDefaultLayoutHtml(htmlContent);

    private static bool IsLegacyDefaultTemplateHtml(string? htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
            return false;

        return htmlContent.Contains("(Your Company Logo Here)", StringComparison.OrdinalIgnoreCase) ||
               htmlContent.Contains("Thank you for contacting our helpdesk. Your ticket has been successfully created.", StringComparison.OrdinalIgnoreCase) ||
               htmlContent.Contains("There has been an update to your support ticket.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyDefaultLayoutHtml(string? htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
            return false;

        return htmlContent.Contains("font-family: Arial, sans-serif; color: #222", StringComparison.OrdinalIgnoreCase) ||
               htmlContent.Contains("<h2 style=\"margin:0;\">{{BRAND_NAME}}</h2>", StringComparison.OrdinalIgnoreCase);
    }

    public static string DefaultLayoutHtml => """
        <div style="margin:0; padding:0; background:#f3f7fb; color:#152033; font-family:Arial, Helvetica, sans-serif;">
          <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="width:100%; margin:0; padding:0; background:#f3f7fb; border-collapse:collapse;">
            <tr>
              <td align="center" style="padding:28px 12px;">
                <table role="presentation" cellpadding="0" cellspacing="0" width="640" style="width:100%; max-width:640px; border-collapse:collapse;">
                  <tr>
                    <td style="padding:0;">
                      <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="width:100%; border-collapse:collapse; background:#f8fbff; border-top:4px solid {{PRIMARY_COLOR}}; border-radius:8px 8px 0 0;">
                        <tr>
                          <td style="padding:16px 24px;">
                            {{{LOGO_HTML}}}
                            <div style="font-size:12px; line-height:18px; letter-spacing:0; color:#25607a; margin-top:8px;">{{BRAND_NAME}} Helpdesk</div>
                          </td>
                        </tr>
                      </table>
                    </td>
                  </tr>
                  <tr>
                    <td style="background:#ffffff; padding:30px 28px 28px 28px; border-left:1px solid #dbe5ee; border-right:1px solid #dbe5ee; color:#152033; font-size:16px; line-height:24px;">
                      {{{BODY}}}
                    </td>
                  </tr>
                  <tr>
                    <td style="background:#eef5fa; border:1px solid #dbe5ee; border-top:0; border-radius:0 0 8px 8px; padding:18px 28px; color:#526173; font-size:12px; line-height:18px;">
                      {{{FOOTER_HTML}}}
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </div>
        """;

    public static string DefaultFooterHtml => """
        <div style="margin:0;">
          <strong style="color:#152033;">RatelDesk</strong><br />
          This message was sent by the RatelDesk support platform. You can reply to ticket emails to add an update.
        </div>
        """;
}
