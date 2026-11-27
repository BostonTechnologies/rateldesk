using Helpdesk.Shared.Enums;

namespace HelpDesk.NewWeb.Components.Pages.Admin.SupportNotifications;

internal static class SupportNotificationLabels
{
    public static string Event(SupportNotificationEventType value) => value switch
    {
        SupportNotificationEventType.TicketCreatedUnassigned => "New unassigned ticket",
        SupportNotificationEventType.TicketAssigned => "Ticket assigned",
        SupportNotificationEventType.TicketAssignedToSupportGroup => "Ticket assigned to support group",
        _ => value.ToString()
    };

    public static string RecipientType(SupportNotificationRecipientType value) => value switch
    {
        SupportNotificationRecipientType.SupportGroup => "Support group",
        SupportNotificationRecipientType.User => "User",
        _ => value.ToString()
    };

    public static string Channel(SupportNotificationChannel value) => value switch
    {
        SupportNotificationChannel.Email => "Email",
        _ => value.ToString()
    };

    public static string CoverageRole(SupportCoverageRole value) => value switch
    {
        SupportCoverageRole.Primary => "Primary",
        SupportCoverageRole.Escalation => "Escalation",
        SupportCoverageRole.Backup => "Backup",
        _ => value.ToString()
    };

    public static string MemberRole(SupportGroupMemberRole value) => value switch
    {
        SupportGroupMemberRole.Member => "Member",
        SupportGroupMemberRole.Lead => "Lead",
        SupportGroupMemberRole.Manager => "Manager",
        _ => value.ToString()
    };

    public static string MemberSource(SupportGroupMemberSource value) => value switch
    {
        SupportGroupMemberSource.Manual => "Manual",
        SupportGroupMemberSource.Bootstrap => "Bootstrap",
        SupportGroupMemberSource.External => "External",
        _ => value.ToString()
    };
}
