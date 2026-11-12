using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.User;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Users;

public static class UserDirectoryQuery
{
    public sealed class Parameters
    {
        public string? Search { get; set; }
        public string? OrganizationId { get; set; }
        public string? Role { get; set; }
        public string? State { get; set; }
        public bool? IsTestUser { get; set; }
        public string? SortBy { get; set; }
        public bool? Descending { get; set; }
        public int? Page { get; set; }
        public int? PageSize { get; set; }
    }

    public static Task<IResult> ReadAsync(Parameters parameters, HelpdeskDbContext db,
        ClaimsPrincipal principal, CancellationToken ct) => ReadAsync(parameters, db, principal, true, ct);

    public static async Task<IResult> ReadAsync(Parameters parameters,
        HelpdeskDbContext db, ClaimsPrincipal principal, bool supportsLocalAccounts, CancellationToken ct)
    {
        var requestedPage = parameters.Page ?? 1;
        var pageSize = parameters.PageSize ?? 25;
        if (!IsValid(parameters, requestedPage, pageSize))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = ["Use a positive page, page size 25, 50 or 100, and a valid account state. Search is limited to 200 characters."] });
        // The scalar identity projection keeps account filters/counts in SQL.
        var accounts = db.Database.SqlQueryRaw<AccountState>("""
            SELECT a."Id", a."IsEnabled", a."IsDeleted", a."IsInstanceAdministrator",
                CASE WHEN (a."PasswordHash" IS NULL OR a."PasswordHash" = '') AND EXISTS
                    (SELECT 1 FROM "AspNetUserLogins" l WHERE l."UserId" = a."Id") THEN false ELSE true END AS "CanManageCredentials"
            FROM "AspNetUsers" a
            """);
        var rows = ApplyFilters(BuildRows(db, accounts), parameters, db);
        var total = await rows.CountAsync(ct);
        var page = Math.Min(requestedPage, Math.Max(1, (total + pageSize - 1) / pageSize));
        var selected = await Sort(rows, parameters).ThenBy(row => row.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var metadata = await ReadMetadataAsync(selected, accounts, principal, db, ct);
        var items = selected.Select(row => ToDto(row, metadata, supportsLocalAccounts)).ToList();
        return Results.Ok(new PagedResult<UserDirectoryDto> { Page = page, PageSize = pageSize, Total = total, Items = items });
    }

    private static bool IsValid(Parameters parameters, int page, int size) => page > 0 && size is (25 or 50 or 100) &&
        !(parameters.Search?.Length > 200) && parameters.State is (null or "" or "Enabled" or "Disabled" or "External" or "Profile");

    private static IQueryable<DirectoryRow> BuildRows(HelpdeskDbContext db, IQueryable<AccountState> accounts)
    {
        return from user in db.Users.AsNoTracking()
                   where !user.IsDeleted
                   join organization in db.Organizations.AsNoTracking() on user.OrganizationId equals organization.Id into organizations
                   from organization in organizations.DefaultIfEmpty()
                   let linkedAccount = db.CustomerAuthLinks.Where(link => link.DomainUserId == user.Id && link.LocalAccountId != null)
                       .OrderBy(link => link.Id).Select(link => link.LocalAccountId).FirstOrDefault()
                   join account in accounts on (linkedAccount ?? user.Id) equals account.Id into userAccounts
                   from account in userAccounts.DefaultIfEmpty()
                   let disabledLink = db.CustomerAuthLinks.Any(link => (link.DomainUserId == user.Id || link.LocalAccountId == user.Id) &&
                       (link.DisabledAtUtc != null || link.InviteStatus == Helpdesk.Shared.Models.CustomerInviteStatus.Disabled))
                   select new DirectoryRow
                   {
                       Id = user.Id, Name = user.Name, Email = user.Email, Role = user.Role,
                       OrganizationId = user.OrganizationId, IsTestUser = user.IsTestUser,
                       OrganizationName = organization == null ? null : organization.Name,
                       AccountId = account.Id,
                       IsEnabled = account.Id != null && account.IsEnabled && !account.IsDeleted,
                       IsAdministrator = account.Id != null && account.IsInstanceAdministrator,
                       IsAccountDeleted = account.Id != null && account.IsDeleted,
                       CanManageCredentials = account.Id != null && account.CanManageCredentials,
                       HasDisabledLink = disabledLink,
                       IncompleteExternalIdentity = db.CustomerAuthLinks.Any(link => link.DomainUserId == user.Id && link.LocalAccountId == null && link.AuthentikUserId != null &&
                           (link.OidcIssuer == null || link.OidcIssuer == "" || link.OidcSubject == null || link.OidcSubject == "")),
                       State = disabledLink ? "Disabled" : account.Id != null ? (account.IsEnabled && !account.IsDeleted ? "Enabled" : "Disabled") :
                           db.CustomerAuthLinks.Any(link => link.DomainUserId == user.Id && (link.OidcSubject != null || link.AuthentikUserId != null)) ? "External" : "Profile"
                   };

    }

    private static IQueryable<DirectoryRow> ApplyFilters(IQueryable<DirectoryRow> rows, Parameters parameters, HelpdeskDbContext db)
    {
        if (!string.IsNullOrWhiteSpace(parameters.Search))
        {
            if (db.Database.IsSqlite()) SqliteUnicodeSearch.RegisterConnection(db);
            var search = parameters.Search.Trim();
            var pattern = Pattern(search);
            var sqlitePattern = Pattern(SqliteUnicodeSearch.Fold(search)!);
            rows = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? rows.Where(row => EF.Functions.ILike(row.Name, pattern, "\\") || EF.Functions.ILike(row.Email, pattern, "\\") ||
                    (row.OrganizationName != null && EF.Functions.ILike(row.OrganizationName, pattern, "\\")))
                : rows.Where(row => EF.Functions.Like(SqliteUnicodeSearch.Fold(row.Name)!, sqlitePattern, "\\") ||
                    EF.Functions.Like(SqliteUnicodeSearch.Fold(row.Email)!, sqlitePattern, "\\") ||
                    (row.OrganizationName != null && EF.Functions.Like(SqliteUnicodeSearch.Fold(row.OrganizationName)!, sqlitePattern, "\\")));
        }
        if (!string.IsNullOrWhiteSpace(parameters.OrganizationId)) rows = rows.Where(row => row.OrganizationId == parameters.OrganizationId);
        if (!string.IsNullOrWhiteSpace(parameters.Role)) rows = rows.Where(row => row.Role == parameters.Role ||
            db.ScopedRoleAssignments.Any(assignment => assignment.UserId == row.Id && assignment.RoleKey == parameters.Role));
        if (!string.IsNullOrWhiteSpace(parameters.State)) rows = rows.Where(row => row.State == parameters.State);
        if (parameters.IsTestUser is { } test) rows = rows.Where(row => row.IsTestUser == test);

        return rows;
    }

    private static IOrderedQueryable<DirectoryRow> Sort(IQueryable<DirectoryRow> rows, Parameters parameters)
    {
        return (parameters.SortBy?.ToLowerInvariant(), parameters.Descending == true) switch
        {
            ("email", false) => rows.OrderBy(row => row.Email),
            ("email", true) => rows.OrderByDescending(row => row.Email),
            ("organization", false) => rows.OrderBy(row => row.OrganizationName),
            ("organization", true) => rows.OrderByDescending(row => row.OrganizationName),
            ("state", false) => rows.OrderBy(row => row.State),
            ("state", true) => rows.OrderByDescending(row => row.State),
            (_, true) => rows.OrderByDescending(row => row.Name),
            _ => rows.OrderBy(row => row.Name)
        };
    }

    private static async Task<DirectoryMetadata> ReadMetadataAsync(List<DirectoryRow> selected, IQueryable<AccountState> accounts,
        ClaimsPrincipal principal, HelpdeskDbContext db, CancellationToken ct)
    {
        var enabledAdministrators = await accounts.CountAsync(account => account.IsEnabled && !account.IsDeleted && account.IsInstanceAdministrator, ct);
        var currentId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var issuer = principal.FindFirstValue("iss")?.TrimEnd('/');
        var subject = principal.FindFirstValue("sub");
        var selectedIds = selected.Select(row => row.Id).ToArray();
        var roles = await db.ScopedRoleAssignments.AsNoTracking().Where(assignment => selectedIds.Contains(assignment.UserId))
            .Select(assignment => new DirectoryRole { UserId = assignment.UserId, RoleKey = assignment.RoleKey }).Distinct().ToListAsync(ct);
        List<string> ownExternalProfiles = string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject) ? [] :
            await db.CustomerAuthLinks.Where(link => selectedIds.Contains(link.DomainUserId!) && link.OidcIssuer == issuer && link.OidcSubject == subject)
                .Select(link => link.DomainUserId!).ToListAsync(ct);
        return new(enabledAdministrators, currentId, ownExternalProfiles, roles);
    }

    private static UserDirectoryDto ToDto(DirectoryRow row, DirectoryMetadata metadata, bool supportsLocalAccounts)
    {
        var self = row.Id == metadata.CurrentId || row.AccountId == metadata.CurrentId || metadata.OwnExternalProfiles.Contains(row.Id);
        var lastAdministrator = row.IsAdministrator && row.IsEnabled && metadata.EnabledAdministrators <= 1;
        return new UserDirectoryDto
        {
            Id = row.Id, Name = row.Name, Email = row.Email, Role = row.Role, OrganizationId = row.OrganizationId,
            RoleKeys = metadata.Roles.Where(role => role.UserId == row.Id).Select(role => role.RoleKey).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            OrganizationName = row.OrganizationName, IsTestUser = row.IsTestUser, AccountState = row.State, AccountId = row.AccountId,
            AccountSignInEnabled = row.AccountId is null ? null : row.IsEnabled,
            IdentitySummary = row.HasDisabledLink ? "Identity link disabled; enabling the account does not restore the link." :
                row.AccountId is null ? row.State == "External" ? "External identity" : "No application sign-in account" :
                row.CanManageCredentials ? "Local application account" : "External application account",
            CanManageAccount = supportsLocalAccounts && row.AccountId != null && !row.IsAccountDeleted && row.CanManageCredentials,
            CanChangeSignIn = supportsLocalAccounts && row.AccountId != null && !row.IsAccountDeleted && !self && !lastAdministrator,
            CanDelete = !self && !lastAdministrator && !row.IncompleteExternalIdentity,
            DeleteBlockedReason = self ? "You cannot delete your own application access." :
                lastAdministrator ? "The last enabled instance administrator must remain active." :
                row.IncompleteExternalIdentity ? "The user must sign in once through the configured provider to establish their verified identity link before deletion." : null
        };
    }

    private static string Pattern(string value) => "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private sealed record DirectoryMetadata(int EnabledAdministrators, string? CurrentId,
        List<string> OwnExternalProfiles, List<DirectoryRole> Roles);

    private sealed class DirectoryRole
    {
        public string UserId { get; init; } = string.Empty;
        public string RoleKey { get; init; } = string.Empty;
    }

    private sealed class DirectoryRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string? OrganizationId { get; init; }
        public bool IsTestUser { get; init; }
        public string? OrganizationName { get; init; }
        public string? AccountId { get; init; }
        public bool IsEnabled { get; init; }
        public bool IsAdministrator { get; init; }
        public bool IsAccountDeleted { get; init; }
        public bool CanManageCredentials { get; init; }
        public bool HasDisabledLink { get; init; }
        public bool IncompleteExternalIdentity { get; init; }
        public string State { get; init; } = string.Empty;
    }

    private sealed class AccountState
    {
        public string Id { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public bool IsDeleted { get; set; }
        public bool IsInstanceAdministrator { get; set; }
        public bool CanManageCredentials { get; set; }
    }
}
