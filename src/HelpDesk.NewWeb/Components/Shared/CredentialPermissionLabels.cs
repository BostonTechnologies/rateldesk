namespace HelpDesk.NewWeb.Components.Shared;

public static class CredentialPermissionLabels
{
    public static string Name(string permission) => permission switch
    {
        "SelfService.User" => "Use self service",
        "Incident.User" => "Use incidents",
        "Incident.Manager" => "Manage incidents",
        "Incident.Read" => "Read incidents",
        "Incident.Write" => "Create and update incidents",
        "Incident.Delete" => "Delete incidents",
        "Request.User" => "Use requests",
        "Request.Manager" => "Manage requests",
        "Request.Read" => "Read requests",
        "Request.Write" => "Create and update requests",
        "Request.Delete" => "Delete requests",
        "Request.Execute" => "Execute request tasks",
        "Change.User" => "Use changes",
        "Change.Manager" => "Manage changes",
        "Change.Read" => "Read changes",
        "Change.Write" => "Create and update changes",
        "Change.Delete" => "Delete changes",
        "Change.Approve" => "Approve changes",
        "DataManagement.Admin" => "Administer data",
        "Tenant.Users.Manage" => "Manage organization users",
        "Tenant.Roles.Assign" => "Assign organization roles",
        "Tenant.Settings.Manage" => "Manage organization settings",
        "Tenant.Customers.Manage" => "Manage organization customers",
        "Tenant.Sla.Manage" => "Manage organization service levels",
        "HelpdeskAdmin" => "Administer the instance",
        _ => permission.Replace('.', ' ')
    };

    public static string Group(string group) => group switch
    {
        "SelfService" => "Self service",
        "Incident" => "Incidents",
        "Request" => "Requests",
        "Change" => "Changes",
        "DataManagement" => "Data management",
        "Tenant" => "Organization administration",
        _ => group
    };
}
