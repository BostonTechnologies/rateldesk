using System.Data;
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
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(db.Database.IsNpgsql()
                ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, ct)
            : null;
        try
        {
            var customer = await customers.GetAsync(id);
            if (customer is null) return Results.Problem("Resource not found", statusCode: 404);

            if (db.Database.IsNpgsql())
            {
                // The row lock also excludes concurrent auth-link inserts through their
                // customer foreign key. Read again after locking to discard a stale snapshot.
                customer = await db.Customers.FromSqlInterpolated(
                        $"SELECT * FROM \"Customers\" WHERE \"Id\" = {customer.Id} FOR UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(ct);
                if (customer is null) return Results.Problem("Resource not found", statusCode: 404);
            }
            else if (db.Database.IsRelational() && db.Entry(customer).State != Microsoft.EntityFrameworkCore.EntityState.Detached)
            {
                // A prior query in this request may already have tracked an older identity key.
                await db.Entry(customer).ReloadAsync(ct);
            }

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
            if (transaction is not null) await transaction.CommitAsync(ct);
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
