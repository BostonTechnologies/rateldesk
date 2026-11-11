using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services;

internal sealed record SelfServiceRequester(
    Customer? Customer, User? DomainUser, string? OrganizationId, string? OrganizationName,
    string? Name, string? ErrorCode, string? Message)
{
    public bool IsEligible => ErrorCode is null;
}

internal static class SelfServiceRequesterResolver
{
    public static async Task<SelfServiceRequester> ResolveAsync(
        HelpdeskDbContext db, RatelDeskIdentityDbContext? identityDb,
        CurrentUserAccessProfile access, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!access.IsAuthenticated)
            return Denied("requester_unavailable", "Your sign-in is no longer available. Sign in again before submitting.");

        Customer? customer = null;
        User? domainUser = null;
        if (!string.IsNullOrWhiteSpace(access.CustomerId))
        {
            customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == access.CustomerId, ct);
            if (customer?.IsEnabled != true)
                return Denied("requester_unavailable", "Your requester profile is unavailable. Ask an administrator to review your account.");
            if (customer.IdentityUserId is not null && !await db.Users.AsNoTracking().AnyAsync(x => x.Id == customer.IdentityUserId && !x.IsDeleted, ct))
                return Denied("requester_unavailable", "Your requester identity is retired or unavailable. Ask an administrator to review it.");
            var localId = principal.FindFirstValue("auth_mode") is "local" or "integration"
                ? principal.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            var issuer = principal.FindFirstValue("iss")?.TrimEnd('/');
            var subject = principal.FindFirstValue("sub");
            if (await db.CustomerAuthLinks.AsNoTracking().AnyAsync(link => link.CustomerId == customer.Id &&
                ((localId != null && link.LocalAccountId == localId) ||
                 (issuer != null && subject != null && link.OidcIssuer == issuer && link.OidcSubject == subject)) &&
                (link.DisabledAtUtc != null || link.InviteStatus == CustomerInviteStatus.Disabled), ct))
                return Denied("requester_unavailable", "Your linked requester profile is disabled. Ask an administrator to review it.");
        }
        else
        {
            // Profile data, including an email match, never establishes identity.
            // Only a persisted enabled local account and its durable domain ID
            // can establish a new self-service customer link.
            var accountId = principal.FindFirstValue("auth_mode") is "local" or "integration"
                ? principal.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            if (identityDb is null || string.IsNullOrWhiteSpace(accountId))
                return Denied("requester_unavailable", "Your account has no eligible requester profile. Ask an administrator to review its organization membership.");
            var account = await identityDb.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, ct);
            if (account?.IsEnabled != true || account.IsDeleted)
                return Denied("requester_unavailable", "Your account is unavailable. Ask an administrator to review your access.");
            var link = await db.CustomerAuthLinks.AsNoTracking().SingleOrDefaultAsync(x => x.LocalAccountId == accountId || x.DomainUserId == accountId, ct);
            if (link is not null)
                return Denied("requester_unavailable", "Your linked requester profile is unavailable. Ask an administrator to review it before retrying.");
            domainUser = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, ct);
            if (domainUser is null || domainUser.IsDeleted || string.IsNullOrWhiteSpace(domainUser.Email))
                return Denied("requester_unavailable", "Your account has no eligible requester profile. Ask an administrator to review its organization membership.");
            customer = await db.Customers.SingleOrDefaultAsync(x => x.IdentityUserId == domainUser.Id, ct);
            if (customer is not null && !customer.IsEnabled)
                return Denied("requester_unavailable", "Your requester profile is unavailable. Ask an administrator to review it.");
        }

        var organizationId = customer?.OrganizationId ?? domainUser?.OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId) ||
            !string.Equals(organizationId, access.PrimaryOrganizationId, StringComparison.OrdinalIgnoreCase))
            return Denied("request_organization_required", "Your account needs an explicit eligible organization. Ask an administrator to review its membership.");
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationId, ct);
        if (organization?.IsEnabled != true || !access.HasPermission(HelpdeskPermissions.SelfServiceUser, organizationId))
            return Denied("request_organization_denied", "You cannot submit requests in this organization. Ask an administrator to review your self-service access.");
        return new(customer, domainUser, organization.Id, organization.Name,
            customer?.Name ?? domainUser!.Name, null, null);
    }

    public static async Task<Customer> EnsureCustomerAsync(
        HelpdeskDbContext db, SelfServiceRequester requester, CancellationToken ct, bool localAccount = true)
    {
        if (requester.Customer is not null && requester.DomainUser is null) return requester.Customer;
        var user = requester.DomainUser ?? throw new InvalidOperationException("An eligible requester is required.");
        var customer = requester.Customer ?? new Customer
        {
            Name = user.Name, Email = user.Email.Trim(), OrganizationId = requester.OrganizationId!,
            State = Helpdesk.Shared.Models.EntityState.Enabled, IdentityUserId = user.Id
        };
        if (requester.Customer is null) db.Customers.Add(customer);
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = customer.Id, DomainUserId = user.Id, LocalAccountId = localAccount ? user.Id : null,
            AuthProviderType = localAccount ? "Local" : "Domain", InviteStatus = localAccount ? CustomerInviteStatus.Active : CustomerInviteStatus.NotInvited
        });
        await db.SaveChangesAsync(ct);
        return customer;
    }

    private static SelfServiceRequester Denied(string code, string message) => new(null, null, null, null, null, code, message);
}
