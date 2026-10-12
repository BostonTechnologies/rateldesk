using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Ticketing;

internal static class AssigneeQueries
{
    // These predicates mirror mutation validation. Filter before searching/paging.
    internal static IQueryable<User> Eligible(HelpdeskDbContext db, string module, Helpdesk.Shared.Models.Organization organization)
    {
        var users = db.Users.AsNoTracking();
        return module switch
        {
            "incident" => users.Where(user => user.Email != null && user.Email.Trim() != "" &&
                (from member in db.SupportGroupMembers
                 join supportGroup in db.SupportGroups on member.SupportGroupId equals supportGroup.Id
                 join coverage in db.OrganizationSupportCoverages on supportGroup.Id equals coverage.SupportGroupId
                 where member.UserId == user.Id && member.IsEnabled && supportGroup.IsEnabled &&
                       coverage.IsEnabled && coverage.CustomerOrganizationId == organization.Id
                 select member.Id).Any()),
            "request" => users.Where(user => user.OrganizationId == organization.Id),
            "change" => users.Where(user =>
                user.OrganizationId == (string.IsNullOrWhiteSpace(organization.ItSupportOrganizationId)
                    ? organization.Id : organization.ItSupportOrganizationId) ||
                user.Role == "HelpdeskAdmin" || user.Role == "Technician"),
            _ => throw new ArgumentOutOfRangeException(nameof(module))
        };
    }

    internal static IQueryable<User> Search(HelpdeskDbContext db, IQueryable<User> users, string? query)
    {
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term)) return users;
        var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
            ? users.Where(user => EF.Functions.ILike(user.Name, pattern, "\\") || EF.Functions.ILike(user.Email, pattern, "\\"))
            : users.Where(user => EF.Functions.Like(user.Name, pattern, "\\") || EF.Functions.Like(user.Email, pattern, "\\"));
    }
}
