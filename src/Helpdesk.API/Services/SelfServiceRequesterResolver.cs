using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services;

internal sealed record SelfServiceIdentity(Customer? HomeCustomer, User? DomainUser, bool NeedsLocalLink)
{
    public string? Name => HomeCustomer?.Name ?? DomainUser?.Name;
    public string? HomeOrganizationId => HomeCustomer?.OrganizationId ?? DomainUser?.OrganizationId;

    public bool CanCreateCustomer => DomainUser is { IsDeleted: false } user && !string.IsNullOrWhiteSpace(user.Email);
}

internal static class SelfServiceRequesterResolver
{
    public const string UnavailableMessage =
        "Your requester profile is unavailable. Sign in again or ask an administrator to review your account.";

    public static async Task<SelfServiceIdentity?> ResolveAsync(
        HelpdeskDbContext db, RatelDeskIdentityDbContext? identityDb,
        CurrentUserAccessProfile access, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!access.IsAuthenticated) return null;

        var localId = principal.FindFirstValue("auth_mode") is "local" or "integration"
            ? principal.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        if (localId is not null && identityDb is not null && !await identityDb.Users.AsNoTracking()
            .AnyAsync(account => account.Id == localId && account.IsEnabled && !account.IsDeleted, ct))
            return null;

        var links = db.CustomerAuthLinks.AsNoTracking();
        var link = await FindLoginLinkAsync(links, principal, localId, ct);
        if (link is { DisabledAtUtc: not null } || link?.InviteStatus == CustomerInviteStatus.Disabled)
            return null;
        if (link is not null && access.CustomerId is not null && link.CustomerId != access.CustomerId)
            return null;

        var homeId = link?.CustomerId ?? access.CustomerId;
        var home = homeId is null ? null : await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == homeId, ct);
        if (homeId is not null && home?.IsEnabled != true) return null;

        var domainId = link?.DomainUserId ?? home?.IdentityUserId ?? localId;
        var domain = domainId is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == domainId, ct);
        if (domain?.IsDeleted == true || (link?.DomainUserId is not null || home?.IdentityUserId is not null) && domain is null)
            return null;
        if (home?.IdentityUserId is not null && domain?.Id != home.IdentityUserId)
            return null;

        if (home is not null) return new(home, domain, false);

        // Provisioning from an unlinked account requires the durable, enabled local identity.
        // Neither the sign-in address nor another contact sharing it proves ownership.
        if (localId is null || identityDb is null || domain is null || string.IsNullOrWhiteSpace(domain.OrganizationId) ||
            await links.AnyAsync(candidate => candidate.DomainUserId == localId, ct))
            return null;

        home = await db.Customers.AsNoTracking().SingleOrDefaultAsync(
            c => c.IdentityUserId == domain.Id && c.OrganizationId == domain.OrganizationId, ct);
        return home is { IsEnabled: false } ? null : new(home, domain, true);
    }

    private static async Task<CustomerAuthLink?> FindLoginLinkAsync(
        IQueryable<CustomerAuthLink> links, ClaimsPrincipal principal, string? localId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(localId))
        {
            var local = await links.SingleOrDefaultAsync(link => link.LocalAccountId == localId, ct);
            if (local is not null) return local;
        }

        var issuer = principal.FindFirstValue("iss")?.TrimEnd('/');
        var subject = principal.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject))
        {
            var oidc = await links.SingleOrDefaultAsync(link => link.OidcIssuer == issuer && link.OidcSubject == subject, ct);
            if (oidc is not null) return oidc;
        }

        var providerId = principal.FindFirstValue("authentik_user_id");
        if (string.IsNullOrWhiteSpace(providerId)) providerId = principal.FindFirstValue("ak_user_id");
        return string.IsNullOrWhiteSpace(providerId)
            ? null : await links.SingleOrDefaultAsync(link => link.AuthentikUserId == providerId, ct);
    }

    public static async Task<IReadOnlyDictionary<string, Customer>> LoadCustomersAsync(
        HelpdeskDbContext db, SelfServiceIdentity? identity, IReadOnlyCollection<string> organizationIds, CancellationToken ct)
    {
        var customers = new Dictionary<string, Customer>(StringComparer.Ordinal);
        if (identity?.DomainUser is { } user)
        {
            customers = await db.Customers.AsNoTracking()
                .Where(c => c.IdentityUserId == user.Id && organizationIds.Contains(c.OrganizationId))
                .ToDictionaryAsync(c => c.OrganizationId, StringComparer.Ordinal, ct);
        }
        if (identity?.HomeCustomer is { } home)
            customers[home.OrganizationId] = home;

        return customers;
    }

    // The caller holds the submission transaction. PostgreSQL serializes profile creation
    // across different submitting actors, and SQLite serializes the write transaction.
    public static async Task<Customer?> EnsureCustomerAsync(
        HelpdeskDbContext db, SelfServiceIdentity identity, string organizationId, CancellationToken ct,
        bool localAccount = true, bool createHomeLink = false)
    {
        if (identity.HomeCustomer is { } linked && linked.OrganizationId == organizationId &&
            !identity.NeedsLocalLink && !createHomeLink)
            return linked;

        if (!identity.CanCreateCustomer || identity.DomainUser is not { } user) return null;
        var profileLock = "self-service-requester:" + user.Id;
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({profileLock}, 0))", ct);

        var customer = await db.Customers.SingleOrDefaultAsync(
            c => c.IdentityUserId == user.Id && c.OrganizationId == organizationId, ct);
        if (customer is { IsEnabled: false }) return null;
        if (customer is null)
        {
            customer = new Customer
            {
                Name = user.Name,
                Email = user.Email.Trim(),
                OrganizationId = organizationId,
                IdentityUserId = user.Id
            };
            db.Customers.Add(customer);
        }

        if (organizationId == identity.HomeOrganizationId && (identity.NeedsLocalLink || createHomeLink))
        {
            var link = await db.CustomerAuthLinks.AsNoTracking()
                .SingleOrDefaultAsync(link => link.DomainUserId == user.Id || link.LocalAccountId == user.Id, ct);
            if (link is not null)
            {
                if (link.CustomerId != customer.Id || link.DisabledAtUtc is not null || link.InviteStatus == CustomerInviteStatus.Disabled)
                    return null;
            }
            else
            {
                db.CustomerAuthLinks.Add(new CustomerAuthLink
                {
                    CustomerId = customer.Id,
                    DomainUserId = user.Id,
                    LocalAccountId = localAccount ? user.Id : null,
                    AuthProviderType = localAccount ? "Local" : "Domain",
                    InviteStatus = localAccount ? CustomerInviteStatus.Active : CustomerInviteStatus.NotInvited
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return customer;
    }
}
