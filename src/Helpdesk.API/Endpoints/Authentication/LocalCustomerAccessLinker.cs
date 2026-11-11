using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

internal static class LocalCustomerAccessLinker
{
    public static async Task<string?> LinkAsync(
        HelpdeskDbContext db,
        string localAccountId,
        string displayName,
        string email,
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var customer = await db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.OrganizationId == organizationId && candidate.IdentityUserId == null &&
                candidate.Email.ToLower() == normalizedEmail, cancellationToken);
        if (customer is null)
        {
            customer = new Customer
            {
                Name = displayName,
                Email = normalizedEmail,
                OrganizationId = organizationId,
                State = Helpdesk.Shared.Models.EntityState.Enabled
            };
            db.Customers.Add(customer);
        }
        else if (!customer.IsEnabled || !string.Equals(customer.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase))
        {
            return "The email is already associated with an unavailable or different organization customer record.";
        }

        var existingLink = await db.CustomerAuthLinks
            .IgnoreQueryFilters()
            .AnyAsync(link => link.LocalAccountId == localAccountId, cancellationToken);
        if (!existingLink)
        {
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = customer.Id,
                LocalAccountId = localAccountId,
                AuthProviderType = "Local",
                InviteStatus = CustomerInviteStatus.Active
            });
        }

        return null;
    }
}
