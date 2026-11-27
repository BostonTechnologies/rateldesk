using HelpDesk.NewWeb.Components.Shared;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.TicketQueue;
using Helpdesk.Shared.Services;
using MudBlazor;

namespace HelpDesk.NewWeb.Services;

public static class TicketQueueActions
{
    public static bool CanWrite(CurrentUserAccessProfile? access, string module, string? organizationId, string? groupId)
    {
        var (write, manager) = module switch
        {
            "incident" => (HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager),
            "request" => (HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager),
            "change" => (HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeManager),
            _ => throw new ArgumentOutOfRangeException(nameof(module))
        };
        return access != null && (access.HasPermission(write, organizationId, groupId) ||
            access.HasPermission(manager, organizationId, groupId));
    }

    public static bool CanTransfer(CurrentUserAccessProfile? access, string module, string? organizationId, string? groupId) =>
        organizationId != null && CanWrite(access, module, organizationId, groupId) && access != null &&
        (access.HasPermission(HelpdeskPermissions.TicketQueueTransfer, organizationId, groupId) ||
         access.HasPermission(HelpdeskPermissions.TenantRolesAssign, organizationId));

    public static bool CanChooseDestination(CurrentUserAccessProfile? access, string module, string? organizationId) =>
        CanTransfer(access, module, organizationId, null) || access?.TicketQueuePermissionGrants.Any(grant =>
            grant.OrganizationId == organizationId && CanTransfer(access, module, organizationId, grant.SupportGroupId)) == true;

    public static bool CanApproveChange(CurrentUserAccessProfile? access, string? organizationId, string? groupId) =>
        access != null && (access.HasPermission(HelpdeskPermissions.ChangeApprove, organizationId, groupId) ||
                           access.HasPermission(HelpdeskPermissions.ChangeManager, organizationId, groupId));

    public static async Task<bool> MoveAsync(IDialogService dialogs, ISnackbar snackbar, string module,
        IEnumerable<(string Id, string? TenantId, long Version)> selected)
    {
        var rows = selected.ToArray();
        if (rows.Length == 0) return false;
        if (rows[0].TenantId is not { } tenantId || rows.Any(row => row.TenantId != tenantId))
        {
            snackbar.Add("Select tickets from one tenant to choose a destination queue.", Severity.Warning);
            return false;
        }
        var dialog = await dialogs.ShowAsync<MoveTicketQueueDialog>("Move to queue", new DialogParameters<MoveTicketQueueDialog>
        {
            { component => component.Module, module },
            { component => component.OrganizationId, tenantId },
            { component => component.Tickets, rows.Select(row => new TicketQueueSelectionDto(row.Id, row.Version)).ToArray() }
        });
        return (await dialog.Result) is { Canceled: false };
    }
}
