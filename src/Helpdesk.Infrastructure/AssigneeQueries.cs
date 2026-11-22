using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Persistence;

public static class AssigneeQueries
{
    public static Task<bool> CanAssignAsync(HelpdeskDbContext db, string module, string? organizationId, string userId, CancellationToken ct) =>
        Eligible(db, module, db.Organizations.Where(org => org.Id == organizationId)).AnyAsync(user => user.Id == userId, ct);
    public static IQueryable<User> Eligible(HelpdeskDbContext db, string module, Helpdesk.Shared.Models.Organization organization) =>
        Eligible(db, module, db.Organizations.Where(candidate => candidate.Id == organization.Id));

    public static IQueryable<User> Eligible(HelpdeskDbContext db, string module, IQueryable<Helpdesk.Shared.Models.Organization> organizations)
    {
        var organizationIds = organizations.Select(organization => organization.Id);
        var supportIds = organizations.Select(organization => organization.ItSupportOrganizationId == null || organization.ItSupportOrganizationId.Trim() == ""
            ? organization.Id : organization.ItSupportOrganizationId);
        var accounts = AccountMappings(db);
        var links = db.CustomerAuthLinks;
        var users = db.Users.AsNoTracking().Where(user => !user.IsDeleted && !user.Id.StartsWith("service:") &&
            !accounts.Any(account => account.DomainUserId == user.Id && (!account.IsEnabled || account.IsDeleted)) &&
            !links.Any(link => (link.DomainUserId == user.Id || (link.DomainUserId == null && link.LocalAccountId == user.Id)) &&
                (link.DisabledAtUtc != null || link.InviteStatus == CustomerInviteStatus.Disabled)));
        var administrators = accounts.Where(account => account.IsInstanceAdministrator && account.IsEnabled && !account.IsDeleted)
            .Select(account => account.DomainUserId);

        var candidates = module switch
        {
            "incident" => users.Where(user => user.Email != null && user.Email.Trim() != "" &&
                (from member in db.SupportGroupMembers
                 join supportGroup in db.SupportGroups on member.SupportGroupId equals supportGroup.Id
                 join coverage in db.OrganizationSupportCoverages on supportGroup.Id equals coverage.SupportGroupId
                 where member.UserId == user.Id && member.IsEnabled && supportGroup.IsEnabled &&
                       coverage.IsEnabled && organizationIds.Contains(coverage.CustomerOrganizationId)
                 select member.Id).Any()),
            "request" or "task" => users.Where(user => organizationIds.Contains(user.OrganizationId!)),
            "change" => users.Where(user => supportIds.Contains(user.OrganizationId) ||
                (organizations.Any() && user.Role == "Technician")),
            _ => throw new ArgumentOutOfRangeException(nameof(module))
        };
        return candidates.Union(users.Where(user => organizations.Any() && administrators.Contains(user.Id)));
    }

    public static async Task<string?> ResolveAccountUserAsync(HelpdeskDbContext db, string accountId, CancellationToken ct)
    {
        var ids = AccountMappings(db).Where(account => account.AccountId == accountId && account.IsEnabled && !account.IsDeleted)
            .Select(account => account.DomainUserId);
        var matches = await db.Users.AsNoTracking().Where(user => !user.IsDeleted && !user.Id.StartsWith("service:") &&
                ids.Contains(user.Id) && !db.CustomerAuthLinks.Any(link =>
                    (link.DomainUserId == user.Id || link.LocalAccountId == accountId) &&
                    (link.DisabledAtUtc != null || link.InviteStatus == CustomerInviteStatus.Disabled)))
            .Select(user => user.Id).Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    // Identity and domain tables share a physical database, with migrations owned by
    // separate contexts. Keep correlation, active-state filtering and paging in SQL.
    private static IQueryable<AccountMapping> AccountMappings(HelpdeskDbContext db)
    {
        const string localMappings = """
            SELECT a."Id" AS "AccountId", COALESCE(c."DomainUserId", a."Id") AS "DomainUserId", a."IsEnabled", a."IsDeleted", a."IsInstanceAdministrator"
            FROM "AspNetUsers" a LEFT JOIN "CustomerAuthLinks" c ON c."LocalAccountId" = a."Id"
            WHERE c."Id" IS NOT NULL OR NOT EXISTS (SELECT 1 FROM "AspNetUserLogins" l WHERE l."UserId" = a."Id")
            UNION ALL
            SELECT a."Id", c."DomainUserId", a."IsEnabled", a."IsDeleted", a."IsInstanceAdministrator"
            FROM "CustomerAuthLinks" c
            JOIN "AspNetUserLogins" l ON l."ProviderKey" = c."OidcSubject"
            JOIN "AspNetUsers" a ON a."Id" = l."UserId"
            WHERE c."LocalAccountId" IS NULL AND c."DomainUserId" IS NOT NULL AND c."OidcIssuer" IS NOT NULL
                AND l."LoginProvider" =
            """;
        if (db.Database.GetDbConnection() is SqliteConnection sqlite)
        {
            sqlite.CreateFunction("rateldesk_oidc_provider", (string issuer) => OidcApplicationAccount.Provider(issuer), isDeterministic: true);
            return db.Database.SqlQueryRaw<AccountMapping>(localMappings + """ rateldesk_oidc_provider(c."OidcIssuer")""");
        }
        return db.Database.SqlQueryRaw<AccountMapping>(localMappings +
            """ ('oidc:' || encode(sha256(convert_to(rtrim(c."OidcIssuer", '/'), 'UTF8')), 'hex'))""");
    }

    private sealed class AccountMapping
    {
        public string AccountId { get; init; } = "";
        public string DomainUserId { get; init; } = "";
        public bool IsEnabled { get; init; }
        public bool IsDeleted { get; init; }
        public bool IsInstanceAdministrator { get; init; }
    }

    public static IQueryable<User> Search(HelpdeskDbContext db, IQueryable<User> users, string? query)
    {
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term)) return users;
        var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
            ? users.Where(user => EF.Functions.ILike(user.Name, pattern, "\\") || EF.Functions.ILike(user.Email, pattern, "\\"))
            : users.Where(user => EF.Functions.Like(user.Name, pattern, "\\") || EF.Functions.Like(user.Email, pattern, "\\"));
    }
}


public sealed class TicketSupportAccessService(HelpdeskDbContext db) : ISupportAccessService
{
    public Task<bool> CanUserSupportOrganizationAsync(string userId, string customerOrganizationId, CancellationToken ct = default) =>
        AssigneeQueries.CanAssignAsync(db, "incident", customerOrganizationId, userId, ct);
}
