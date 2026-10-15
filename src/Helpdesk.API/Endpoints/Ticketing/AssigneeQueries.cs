using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Ticketing;

internal static class AssigneeQueries
{
    // These predicates mirror mutation validation. Filter before searching/paging.
    internal static IQueryable<User> Eligible(HelpdeskDbContext db, string module, Helpdesk.Shared.Models.Organization organization) =>
        Eligible(db, module, db.Organizations.Where(candidate => candidate.Id == organization.Id));

    internal static IQueryable<User> Eligible(HelpdeskDbContext db, string module, IQueryable<Helpdesk.Shared.Models.Organization> organizations)
    {
        var organizationIds = organizations.Select(organization => organization.Id);
        var supportIds = organizations.Select(organization => organization.ItSupportOrganizationId == null || organization.ItSupportOrganizationId.Trim() == ""
            ? organization.Id : organization.ItSupportOrganizationId);
        var users = db.Users.AsNoTracking();
        return module switch
        {
            "incident" => users.Where(user => user.Email != null && user.Email.Trim() != "" &&
                (from member in db.SupportGroupMembers
                 join supportGroup in db.SupportGroups on member.SupportGroupId equals supportGroup.Id
                 join coverage in db.OrganizationSupportCoverages on supportGroup.Id equals coverage.SupportGroupId
                 where member.UserId == user.Id && member.IsEnabled && supportGroup.IsEnabled &&
                       coverage.IsEnabled && organizationIds.Contains(coverage.CustomerOrganizationId)
                 select member.Id).Any()),
            "request" => users.Where(user => organizationIds.Contains(user.OrganizationId!)),
            "change" => users.Where(user => supportIds.Contains(user.OrganizationId) ||
                (organizations.Any() && (user.Role == "HelpdeskAdmin" || user.Role == "Technician"))),
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
