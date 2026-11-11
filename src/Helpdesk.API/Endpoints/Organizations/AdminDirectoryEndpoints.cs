using Helpdesk.Infrastructure.Auth.Authentik;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Category;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.Organization;

/// <summary>Explicit admin queries; legacy directory arrays and category pickers retain their contracts.</summary>
public static class AdminDirectoryEndpoints
{
    public static void MapAdminDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/directories")
            .WithTags("Admin directories").RequireAuthorization("HelpdeskAdmin");
        group.MapGet("/customers", GetCustomers);
        group.MapGet("/organizations", GetOrganizations);
        group.MapGet("/organizations/lookup", LookupOrganizations);
        group.MapGet("/categories", GetCategories);
    }

    private static async Task<IResult> GetCustomers(HelpdeskDbContext db,
        IOptions<AuthentikOptions> invitationOptions, [AsParameters] CustomerDirectoryQuery query, CancellationToken ct)
    {
        if (!ValidPage(query.Page, query.PageSize, query.Search) ||
            query.Access is not (null or "" or "linked" or "unlinked" or "not-invited" or "pending" or "active" or "disabled" or "failed" or "expired") ||
            query.Sort is not ("name" or "email" or "organization" or "state" or "access"))
            return InvalidQuery();

        // Both contexts share the configured database. This composable, read-only projection
        // joins account state in SQL without retrieving password hashes or loading account lists.
        var accounts = db.Database.SqlQueryRaw<DirectoryLocalAccount>("""
            SELECT account."Id", account."IsEnabled", account."EmailConfirmed",
                   CASE WHEN account."PasswordHash" IS NULL OR account."PasswordHash" = '' THEN false ELSE true END AS "HasPassword",
                   EXISTS (SELECT 1 FROM "AspNetUserLogins" login WHERE login."UserId" = account."Id") AS "HasExternalLogin"
            FROM "AspNetUsers" account
            """);
        var now = DateTimeOffset.UtcNow;
        var expiredLinks = db.Database.IsSqlite()
            ? db.Database.SqlQuery<string>($"SELECT \"Id\" AS \"Value\" FROM \"CustomerAuthLinks\" WHERE julianday(\"InviteLinkExpiresAtUtc\") <= julianday({now})")
            : db.Database.SqlQuery<string>($"SELECT \"Id\" AS \"Value\" FROM \"CustomerAuthLinks\" WHERE \"InviteLinkExpiresAtUtc\" <= {now}");
        var firstLinks = db.CustomerAuthLinks.AsNoTracking().Where(link =>
            link.Id == db.CustomerAuthLinks.Where(other => other.CustomerId == link.CustomerId).Min(other => other.Id));
        var rows = from customer in db.Customers.AsNoTracking()
                   join organization in db.Organizations.AsNoTracking() on customer.OrganizationId equals organization.Id into organizations
                   from organization in organizations.DefaultIfEmpty()
                   let linkCount = db.CustomerAuthLinks.Count(link => link.CustomerId == customer.Id)
                   join link in firstLinks on customer.Id equals link.CustomerId into customerLinks
                   from link in customerLinks.DefaultIfEmpty()
                   join account in accounts on link!.LocalAccountId equals account.Id into localAccounts
                   from account in localAccounts.DefaultIfEmpty()
                   select new CustomerDirectoryRow
                   {
                       Id = customer.Id, Name = customer.Name, Email = customer.Email,
                       OrganizationId = customer.OrganizationId,
                       OrganizationName = organization == null ? "Organization unavailable" : organization.Name,
                       IsEnabled = customer.State == Helpdesk.Shared.Models.EntityState.Enabled,
                       OrganizationEnabled = organization != null && organization.State == Helpdesk.Shared.Models.EntityState.Enabled,
                       LinkCount = linkCount, ProviderType = link == null ? null : link.AuthProviderType,
                       IsLocal = link != null && link.AuthProviderType.ToLower() == "local",
                       HasApplicationAccount = link != null && link.LocalAccountId != null && link.LocalAccountId != "",
                       Access = linkCount == 0 ? CustomerInviteStatus.NotInvited : linkCount > 1 ? CustomerInviteStatus.Failed :
                           link!.InviteStatus == CustomerInviteStatus.Disabled || link.DisabledAtUtc != null ? CustomerInviteStatus.Disabled :
                           link.LocalAccountId != null && link.LocalAccountId != "" && account.Id != null && !account.IsEnabled ? CustomerInviteStatus.Disabled :
                           link.AuthProviderType.ToLower() == "local"
                               ? account.Id == null ? CustomerInviteStatus.Failed : !account.IsEnabled ? CustomerInviteStatus.Disabled :
                                   account.HasExternalLogin ? CustomerInviteStatus.Active : !account.EmailConfirmed || !account.HasPassword ? CustomerInviteStatus.Pending : CustomerInviteStatus.Active
                               : link.LocalAccountId != null && link.LocalAccountId != "" && account.Id == null ? CustomerInviteStatus.Failed :
                                 link.InviteStatus == CustomerInviteStatus.Pending && expiredLinks.Contains(link.Id)
                                   ? CustomerInviteStatus.Expired : link.InviteStatus
                   };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            if (db.Database.IsSqlite())
            {
                SqliteUnicodeSearch.RegisterConnection(db);
                rows = rows.Where(row => SqliteUnicodeSearch.Fold(row.Name)!.Contains(term) || SqliteUnicodeSearch.Fold(row.Email)!.Contains(term) || SqliteUnicodeSearch.Fold(row.OrganizationName)!.Contains(term));
            }
            else rows = rows.Where(row => row.Name.ToUpper().Contains(term) || row.Email.ToUpper().Contains(term) || row.OrganizationName.ToUpper().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(query.OrganizationId)) rows = rows.Where(row => row.OrganizationId == query.OrganizationId);
        if (query.Enabled.HasValue) rows = rows.Where(row => row.IsEnabled == query.Enabled.Value);
        if (query.Access is "linked") rows = rows.Where(row => row.LinkCount > 0);
        else if (query.Access is "unlinked") rows = rows.Where(row => row.LinkCount == 0);
        else if (!string.IsNullOrEmpty(query.Access))
        {
            var state = query.Access switch
            {
                "not-invited" => CustomerInviteStatus.NotInvited, "pending" => CustomerInviteStatus.Pending,
                "active" => CustomerInviteStatus.Active, "disabled" => CustomerInviteStatus.Disabled,
                "failed" => CustomerInviteStatus.Failed, _ => CustomerInviteStatus.Expired
            };
            rows = rows.Where(row => row.Access == state);
        }
        var total = await rows.CountAsync(ct);
        var page = ClampPage(query.Page, query.PageSize, total);
        var ordered = query.Sort switch
        {
            "email" => query.Descending ? rows.OrderByDescending(row => row.Email) : rows.OrderBy(row => row.Email),
            "organization" => query.Descending ? rows.OrderByDescending(row => row.OrganizationName) : rows.OrderBy(row => row.OrganizationName),
            "state" => query.Descending ? rows.OrderByDescending(row => row.IsEnabled) : rows.OrderBy(row => row.IsEnabled),
            "access" => query.Descending ? rows.OrderByDescending(row => row.Access) : rows.OrderBy(row => row.Access),
            _ => query.Descending ? rows.OrderByDescending(row => row.Name) : rows.OrderBy(row => row.Name)
        };
        var items = await ordered.ThenBy(row => row.Id).Skip(page * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return Results.Ok(new PagedResponse<CustomerDto>
        {
            Page = page, PageSize = query.PageSize, TotalCount = total,
            Items = items.Select(row => ToCustomerDto(row, invitationOptions.Value.IsConfigured)).ToList()
        });
    }

    private static CustomerDto ToCustomerDto(CustomerDirectoryRow row, bool invitationsConfigured)
    {
        var canInvite = invitationsConfigured && row.IsEnabled && row.OrganizationEnabled && !row.IsLocal && !row.HasApplicationAccount && row.LinkCount <= 1;
        return new CustomerDto
        {
            Id = row.Id, Name = row.Name, Email = row.Email, OrganizationId = row.OrganizationId,
            OrganizationName = row.OrganizationName, IsEnabled = row.IsEnabled,
            AuthStatus = new CustomerAuthStatusDto
            {
                CustomerId = row.Id, InviteStatus = row.Access, IsLinkedLogin = row.LinkCount > 0,
                AuthProviderType = row.IsLocal ? "Local" : row.ProviderType,
                IdentitySummary = row.HasApplicationAccount ? "Manage linked application sign-in in Team. External provider access is managed separately."
                    : !row.OrganizationEnabled ? "Enable the customer organization before inviting."
                    : !invitationsConfigured ? "External invitations are not configured." : null,
                HasMultipleIdentityLinks = row.LinkCount > 1,
                StatusText = row.LinkCount > 1 ? "Multiple linked identities" : row.Access switch
                {
                    CustomerInviteStatus.NotInvited => "No linked login", CustomerInviteStatus.Pending => row.IsLocal ? "Activation pending" : "Invitation pending",
                    CustomerInviteStatus.Active => "Login active", CustomerInviteStatus.Disabled => "Login disabled",
                    CustomerInviteStatus.Failed => row.IsLocal ? "Linked account unavailable" : "Invite failed", _ => "Invite expired"
                },
                CanInvite = canInvite && row.Access is CustomerInviteStatus.NotInvited or CustomerInviteStatus.Failed or CustomerInviteStatus.Expired,
                CanResend = canInvite && row.Access is CustomerInviteStatus.Pending or CustomerInviteStatus.Failed or CustomerInviteStatus.Expired
            }
        };
    }

    private static async Task<IResult> GetOrganizations(HelpdeskDbContext db, [AsParameters] OrganizationDirectoryQuery query, CancellationToken ct)
    {
        if (!ValidPage(query.Page, query.PageSize, query.Search) || query.Sort is not ("name" or "dns" or "contact" or "support" or "state" or "orchestration"))
            return InvalidQuery();
        var rows = from organization in db.Organizations.AsNoTracking()
                   join support in db.Organizations.AsNoTracking() on organization.ItSupportOrganizationId equals support.Id into supportOrganizations
                   from support in supportOrganizations.DefaultIfEmpty()
                   select new OrganizationDto
                   {
                       Id = organization.Id, Name = organization.Name, DnsName = organization.DnsName, ContactInfo = organization.ContactInfo,
                       ItSupportOrganizationId = organization.ItSupportOrganizationId,
                       SupportOrganizationName = support == null ? null : support.Name,
                       IsEnabled = organization.State == Helpdesk.Shared.Models.EntityState.Enabled,
                       OrchestrationTenantId = organization.OrchestrationTenantId,
                       OrchestrationTenantName = organization.OrchestrationTenantName,
                       OrchestrationTenantLinkedAtUtc = organization.OrchestrationTenantLinkedAtUtc
                   };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            if (db.Database.IsSqlite())
            {
                SqliteUnicodeSearch.RegisterConnection(db);
                rows = rows.Where(row => SqliteUnicodeSearch.Fold(row.Name)!.Contains(term) || row.DnsName != null && SqliteUnicodeSearch.Fold(row.DnsName)!.Contains(term) || row.ContactInfo != null && SqliteUnicodeSearch.Fold(row.ContactInfo)!.Contains(term));
            }
            else rows = rows.Where(row => row.Name.ToUpper().Contains(term) || row.DnsName != null && row.DnsName.ToUpper().Contains(term) || row.ContactInfo != null && row.ContactInfo.ToUpper().Contains(term));
        }
        if (query.Enabled.HasValue) rows = rows.Where(row => row.IsEnabled == query.Enabled.Value);
        if (query.Linked.HasValue) rows = rows.Where(row => row.OrchestrationTenantId.HasValue == query.Linked.Value);
        if (query.SupportOrganizationId is "self") rows = rows.Where(row => row.ItSupportOrganizationId == null || row.ItSupportOrganizationId == "");
        else if (!string.IsNullOrWhiteSpace(query.SupportOrganizationId)) rows = rows.Where(row => row.ItSupportOrganizationId == query.SupportOrganizationId);
        var total = await rows.CountAsync(ct);
        var page = ClampPage(query.Page, query.PageSize, total);
        var ordered = query.Sort switch
        {
            "dns" => query.Descending ? rows.OrderByDescending(row => row.DnsName) : rows.OrderBy(row => row.DnsName),
            "contact" => query.Descending ? rows.OrderByDescending(row => row.ContactInfo) : rows.OrderBy(row => row.ContactInfo),
            "support" => query.Descending ? rows.OrderByDescending(row => row.SupportOrganizationName) : rows.OrderBy(row => row.SupportOrganizationName),
            "state" => query.Descending ? rows.OrderByDescending(row => row.IsEnabled) : rows.OrderBy(row => row.IsEnabled),
            "orchestration" => query.Descending ? rows.OrderByDescending(row => row.OrchestrationTenantName) : rows.OrderBy(row => row.OrchestrationTenantName),
            _ => query.Descending ? rows.OrderByDescending(row => row.Name) : rows.OrderBy(row => row.Name)
        };
        return Results.Ok(new PagedResponse<OrganizationDto>
        {
            Page = page, PageSize = query.PageSize, TotalCount = total,
            Items = await ordered.ThenBy(row => row.Id).Skip(page * query.PageSize).Take(query.PageSize).ToListAsync(ct)
        });
    }

    private static async Task<IResult> LookupOrganizations(HelpdeskDbContext db, [FromQuery] string? search, [FromQuery] string? selectedId, CancellationToken ct)
    {
        if (search?.Length > 120) return InvalidQuery();
        var rows = db.Organizations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(selectedId)) rows = rows.Where(row => row.Id == selectedId);
        else if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            if (db.Database.IsSqlite())
            {
                SqliteUnicodeSearch.RegisterConnection(db);
                rows = rows.Where(row => SqliteUnicodeSearch.Fold(row.Name)!.Contains(term) || row.DnsName != null && SqliteUnicodeSearch.Fold(row.DnsName)!.Contains(term));
            }
            else rows = rows.Where(row => row.Name.ToUpper().Contains(term) || row.DnsName != null && row.DnsName.ToUpper().Contains(term));
        }
        return Results.Ok(await rows.OrderBy(row => row.Name).ThenBy(row => row.Id).Take(50)
            .Select(row => new TenantLookupDto { Id = row.Id, Name = row.Name }).ToListAsync(ct));
    }

    private static async Task<IResult> GetCategories(HelpdeskDbContext db, [AsParameters] CategoryDirectoryQuery query, CancellationToken ct)
    {
        if (!ValidPage(query.Page, query.PageSize, query.Search) || query.Type.HasValue && !Enum.IsDefined(query.Type.Value) ||
            query.Scope is not (null or "" or "global" or "organization") ||
            query.Sort is not ("name" or "type" or "order" or "scope" or "active")) return InvalidQuery();
        var rows = db.TicketCategories.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            if (db.Database.IsSqlite())
            {
                SqliteUnicodeSearch.RegisterConnection(db);
                rows = rows.Where(row => SqliteUnicodeSearch.Fold(row.Name)!.Contains(term) || row.Description != null && SqliteUnicodeSearch.Fold(row.Description)!.Contains(term));
            }
            else rows = rows.Where(row => row.Name.ToUpper().Contains(term) || row.Description != null && row.Description.ToUpper().Contains(term));
        }
        // Exact admin type selection intentionally does not inherit picker Service inclusion.
        if (query.Type.HasValue) rows = rows.Where(row => row.Type == query.Type.Value);
        if (query.Active.HasValue) rows = rows.Where(row => row.IsActive == query.Active.Value);
        if (query.System.HasValue) rows = rows.Where(row => row.IsSystem == query.System.Value);
        if (query.Scope is "global") rows = rows.Where(row => row.TenantId == null);
        if (query.Scope is "organization") rows = rows.Where(row => row.TenantId != null);
        if (query.OrganizationId.HasValue) rows = rows.Where(row => row.TenantId == query.OrganizationId);
        var total = await rows.CountAsync(ct);
        var page = ClampPage(query.Page, query.PageSize, total);
        var ordered = query.Sort switch
        {
            "type" => query.Descending ? rows.OrderByDescending(row => row.Type) : rows.OrderBy(row => row.Type),
            "order" => query.Descending ? rows.OrderByDescending(row => row.SortOrder) : rows.OrderBy(row => row.SortOrder),
            "scope" => query.Descending ? rows.OrderByDescending(row => row.TenantId.HasValue).ThenByDescending(row => row.TenantId) : rows.OrderBy(row => row.TenantId.HasValue).ThenBy(row => row.TenantId),
            "active" => query.Descending ? rows.OrderByDescending(row => row.IsActive) : rows.OrderBy(row => row.IsActive),
            _ => query.Descending ? rows.OrderByDescending(row => row.Name) : rows.OrderBy(row => row.Name)
        };
        var pageRows = await ordered.ThenBy(row => row.Id).Skip(page * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        // Only page scope IDs are enriched; this query is bounded by the maximum page size.
        var tenantIds = pageRows.Where(row => row.TenantId.HasValue).Select(row => row.TenantId!.Value).Distinct().ToArray();
        var compactIds = tenantIds.Select(id => id.ToString("N")).ToArray();
        var dashedIds = tenantIds.Select(id => id.ToString("D")).ToArray();
        var names = await db.Organizations.AsNoTracking().Where(row => compactIds.Contains(row.Id) || dashedIds.Contains(row.Id))
            .Select(row => new { row.Id, row.Name }).ToListAsync(ct);
        var tenantNames = names.Where(row => Guid.TryParse(row.Id, out _)).ToDictionary(row => Guid.Parse(row.Id), row => row.Name);
        return Results.Ok(new PagedResponse<TicketCategoryDto>
        {
            Page = page, PageSize = query.PageSize, TotalCount = total,
            Items = pageRows.Select(row => new TicketCategoryDto
            {
                Id = row.Id, Name = row.Name, Description = row.Description, Type = row.Type, TenantId = row.TenantId,
                TenantName = row.TenantId.HasValue ? tenantNames.GetValueOrDefault(row.TenantId.Value) : null,
                ParentCategoryId = row.ParentCategoryId, SortOrder = row.SortOrder, IsActive = row.IsActive, IsSystem = row.IsSystem,
                CreatedUtc = row.CreatedUtc, UpdatedUtc = row.UpdatedUtc
            }).ToList()
        });
    }

    private static bool ValidPage(int page, int pageSize, string? search) => page >= 0 && pageSize is 25 or 50 or 100 && search?.Length is not > 120;
    private static int ClampPage(int page, int pageSize, int total) => Math.Min(page, Math.Max(0, (total - 1) / pageSize));
    private static IResult InvalidQuery() => Results.Problem(statusCode: 400, title: "Invalid directory query", detail: "Use a supported filter, a search of at most 120 characters, a nonnegative page and a page size of 25, 50 or 100.");

    public sealed class DirectoryLocalAccount
    {
        public string Id { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public bool EmailConfirmed { get; set; }
        public bool HasPassword { get; set; }
        public bool HasExternalLogin { get; set; }
    }
    private sealed class CustomerDirectoryRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string OrganizationId { get; set; } = string.Empty;
        public string OrganizationName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public bool OrganizationEnabled { get; set; }
        public bool IsLocal { get; set; }
        public bool HasApplicationAccount { get; set; }
        public string? ProviderType { get; set; }
        public int LinkCount { get; set; }
        public CustomerInviteStatus Access { get; set; }
    }

    public sealed record CustomerDirectoryQuery(int Page = 0, int PageSize = 25, string? Search = null,
        string? OrganizationId = null, bool? Enabled = null, string? Access = null, string Sort = "name", bool Descending = false);
    public sealed record OrganizationDirectoryQuery(int Page = 0, int PageSize = 25, string? Search = null,
        bool? Enabled = null, bool? Linked = null, string? SupportOrganizationId = null, string Sort = "name", bool Descending = false);
    public sealed record CategoryDirectoryQuery(int Page = 0, int PageSize = 25, string? Search = null,
        TicketCategoryType? Type = null, bool? Active = null, string? Scope = null, Guid? OrganizationId = null,
        bool? System = null, string Sort = "name", bool Descending = false);
}
