using System.Security.Claims;
using Helpdesk.Application.RequestTasks;
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
        var users = db.Users.AsNoTracking();
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
        var regularIds = candidates.Select(user => user.Id);
        return users.Where(user => (regularIds.Contains(user.Id) || (organizations.Any() && administrators.Contains(user.Id))) &&
            !user.IsDeleted && !user.Id.StartsWith("service:") &&
            !accounts.Any(account => account.DomainUserId == user.Id && (!account.IsEnabled || account.IsDeleted)) &&
            !links.Any(link => link.DomainUserId == user.Id && link.LocalAccountId != null &&
                !accounts.Any(account => account.AccountId == link.LocalAccountId && account.DomainUserId == user.Id)) &&
            !links.Any(link => (link.DomainUserId == user.Id || (link.DomainUserId == null && link.LocalAccountId == user.Id)) &&
                (link.DisabledAtUtc != null || link.InviteStatus == CustomerInviteStatus.Disabled)));
    }

    public static async Task<string?> ResolveCallerUserAsync(HelpdeskDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var mode = principal.FindFirstValue("auth_mode");
        if (principal.Identity?.IsAuthenticated != true || mode is "service" or "machine" or "ai_agent" ||
            principal.HasClaim("token_use", "rateldesk_service")) return null;
        var accountId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (mode is "local" or "integration")
            return accountId is null ? null : await ResolveAccountUserAsync(db, accountId, ct);

        var issuer = principal.FindFirstValue("iss")?.TrimEnd('/');
        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)) return null;
        var linkedUser = await (from link in db.CustomerAuthLinks.AsNoTracking()
                                join user in db.Users.AsNoTracking() on (link.DomainUserId ?? link.LocalAccountId) equals user.Id
                                where link.OidcIssuer == issuer && link.OidcSubject == subject && link.DisabledAtUtc == null &&
                                    link.InviteStatus != CustomerInviteStatus.Disabled && !user.IsDeleted
                                select user.Id).SingleOrDefaultAsync(ct);
        if (linkedUser is not null) return linkedUser;
        var provider = OidcApplicationAccount.Provider(issuer);
        accountId = await db.Database.SqlQuery<string>(
            $"""SELECT "UserId" AS "Value" FROM "AspNetUserLogins" WHERE "LoginProvider" = {provider} AND "ProviderKey" = {subject}""")
            .SingleOrDefaultAsync(ct);
        return accountId is null ? null : await ResolveAccountUserAsync(db, accountId, ct);
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
        var providerKey = """('oidc:' || encode(sha256(convert_to(rtrim(c."OidcIssuer", '/'), 'UTF8')), 'hex'))""";
        if (db.Database.GetDbConnection() is SqliteConnection sqlite)
        {
            sqlite.CreateFunction<string?, string?>("rateldesk_oidc_provider", issuer => issuer is null ? null : OidcApplicationAccount.Provider(issuer), isDeterministic: true);
            providerKey = """rateldesk_oidc_provider(c."OidcIssuer")""";
        }
        // Both fragments are constant SQL. Provider keys use the identity store's
        // normalized issuer hash and subject, independently of profile/display data.
        var mappings = """
            SELECT mapped.* FROM (
                WITH external_links AS (
                    SELECT l."UserId" AS "AccountId", c."DomainUserId"
                    FROM "CustomerAuthLinks" c JOIN "AspNetUserLogins" l ON c."OidcSubject" = l."ProviderKey"
                    WHERE c."LocalAccountId" IS NULL AND c."DomainUserId" IS NOT NULL
                        AND c."OidcSubject" <> '' AND c."OidcIssuer" <> '' AND c."OidcSubject" NOT LIKE 'service:%'
                        AND l."LoginProvider" =
            """ + providerKey + """
                )
                SELECT a."Id" AS "AccountId", COALESCE(c."DomainUserId", e."DomainUserId", a."Id") AS "DomainUserId",
                    a."IsEnabled", a."IsDeleted", a."IsInstanceAdministrator"
                FROM "AspNetUsers" a
                LEFT JOIN "CustomerAuthLinks" c ON c."LocalAccountId" = a."Id"
                LEFT JOIN external_links e ON e."AccountId" = a."Id"
                WHERE a."Id" NOT LIKE 'service:%' AND NOT EXISTS (SELECT 1 FROM "AspNetUserLogins" machine
                    WHERE machine."UserId" = a."Id" AND machine."ProviderKey" LIKE 'service:%')
                    AND (c."Id" IS NOT NULL OR e."DomainUserId" IS NOT NULL OR
                    NOT EXISTS (SELECT 1 FROM "CustomerAuthLinks" other WHERE other."DomainUserId" = a."Id"
                        AND (other."LocalAccountId" IS NOT NULL OR other."OidcSubject" IS NOT NULL)))
            ) mapped
            """;
        return db.Database.SqlQueryRaw<AccountMapping>(mappings);
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


public sealed class TicketSupportAccessService(HelpdeskDbContext db) : ISupportAccessService, IRequestTaskAssignees
{
    public Task<bool> CanUserSupportOrganizationAsync(string userId, string customerOrganizationId, CancellationToken ct = default) =>
        AssigneeQueries.CanAssignAsync(db, "incident", customerOrganizationId, userId, ct);

    public Task<HashSet<string>> EligibleDefaultsAsync(string? organizationId, string[] defaultIds, CancellationToken ct) =>
        AssigneeQueries.Eligible(db, "task", db.Organizations.Where(org => org.Id == organizationId))
            .Where(user => defaultIds.Contains(user.Id)).Select(user => user.Id).ToHashSetAsync(ct);
}
