namespace Helpdesk.Shared.Enums;

public enum SupportGroupMemberRole
{
    Member = 0,
    Lead = 1,
    Manager = 2
}

public enum SupportGroupMemberSource
{
    Manual = 0,
    Bootstrap = 1,
    External = 2
}

public enum SupportCoverageRole
{
    Primary = 0,
    Escalation = 1,
    Backup = 2
}

public enum SupportNotificationEventType
{
    TicketCreatedUnassigned = 0,
    TicketAssigned = 1,
    TicketAssignedToSupportGroup = 2
}

public enum SupportNotificationRecipientType
{
    SupportGroup = 0,
    User = 1
}

public enum SupportNotificationChannel
{
    Email = 0,
    InApp = 1
}

public enum SupportNotificationDeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    Skipped = 3
}
