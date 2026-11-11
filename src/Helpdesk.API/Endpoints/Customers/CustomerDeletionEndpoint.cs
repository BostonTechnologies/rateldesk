using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Customers;

/// <summary>Preserves retained sign-in identities when deleting an unlinked customer contact.</summary>
public static class CustomerDeletionEndpoint
{
    public static async Task<IResult> DeleteAsync(string id, IRepository<Customer> customers,
        HelpdeskDbContext db, CancellationToken ct)
    {
        var customer = await customers.GetAsync(id);
        if (customer is null) return Results.Problem("Resource not found", statusCode: 404);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (customer.IdentityUserId is not null ||
                await db.CustomerAuthLinks.AsNoTracking().AnyAsync(link => link.CustomerId == customer.Id, ct))
            {
                return Results.Conflict(new
                {
                    code = "customer_identity_retained",
                    message = "This customer retains a sign-in identity or invitation. Manage or delete application access in Team; the customer and identity history must be retained."
                });
            }

            if (!await customers.DeleteAsync(customer.Id))
                return Results.Problem("Resource not found", statusCode: 404);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new
            {
                code = "customer_dependency_conflict",
                message = "Customer has retained dependent records; resolve them through their supported workflow before deletion."
            });
        }
    }
}
