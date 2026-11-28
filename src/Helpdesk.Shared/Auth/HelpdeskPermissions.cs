namespace Helpdesk.Shared.Auth;

public static class HelpdeskPermissions
{
    public const string SelfServiceUser = "SelfService.User";
    public const string IncidentUser = "Incident.User";
    public const string IncidentManager = "Incident.Manager";
    public const string IncidentRead = "Incident.Read";
    public const string IncidentWrite = "Incident.Write";
    public const string IncidentDelete = "Incident.Delete";
    public const string RequestUser = "Request.User";
    public const string RequestManager = "Request.Manager";
    public const string RequestRead = "Request.Read";
    public const string RequestWrite = "Request.Write";
    public const string RequestDelete = "Request.Delete";
    public const string RequestExecute = "Request.Execute";
    public const string ChangeUser = "Change.User";
    public const string ChangeManager = "Change.Manager";
    public const string ChangeRead = "Change.Read";
    public const string ChangeWrite = "Change.Write";
    public const string ChangeDelete = "Change.Delete";
    public const string ChangeApprove = "Change.Approve";
    public const string TicketQueueTransfer = "Ticket.Queue.Transfer";
    public const string DataManagementAdmin = "DataManagement.Admin";
    public const string HelpdeskAdmin = "HelpdeskAdmin";
    public const string TenantUsersManage = "Tenant.Users.Manage";
    public const string TenantRolesAssign = "Tenant.Roles.Assign";
    public const string TenantSettingsManage = "Tenant.Settings.Manage";
    public const string TenantCustomersManage = "Tenant.Customers.Manage";
    public const string TenantSlaManage = "Tenant.Sla.Manage";

    public static readonly string[] AssignablePermissions =
    [
        SelfServiceUser,
        IncidentUser,
        IncidentManager,
        IncidentRead,
        IncidentWrite,
        IncidentDelete,
        RequestUser,
        RequestManager,
        RequestRead,
        RequestWrite,
        RequestDelete,
        RequestExecute,
        ChangeUser,
        ChangeManager,
        ChangeRead,
        ChangeWrite,
        ChangeDelete,
        ChangeApprove,
        TicketQueueTransfer,
        DataManagementAdmin,
        TenantUsersManage,
        TenantRolesAssign,
        TenantSettingsManage,
        TenantCustomersManage,
        TenantSlaManage
    ];

    public static readonly string[] UserBundle =
    [
        SelfServiceUser,
        IncidentUser,
        RequestUser
    ];

    // Compatibility mapping for explicit roles issued by existing OIDC providers.
    public static readonly string[] TechnicalBundle =
    [
        SelfServiceUser,
        IncidentUser,
        IncidentManager,
        RequestUser,
        RequestManager,
        ChangeUser,
        ChangeManager
    ];
    // New application-managed operators do not acquire destructive permissions.
    public static readonly string[] OperatorBundle =
    [
        SelfServiceUser, IncidentUser, RequestUser,
        IncidentRead, IncidentWrite,
        RequestRead, RequestWrite,
        ChangeRead, ChangeWrite, ChangeApprove
    ];
}
