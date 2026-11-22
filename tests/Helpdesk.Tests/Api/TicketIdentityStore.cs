using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Helpdesk.Tests.Api;

internal static class TicketIdentityStore
{
    internal static RatelDeskIdentityDbContext Open(HelpdeskDbContext db)
    {
        var options = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>();
        var connection = db.Database.GetDbConnection();
        if (db.Database.IsSqlite()) options.UseSqlite(connection);
        else options.UseNpgsql(connection);
        return new RatelDeskIdentityDbContext(options.Options);
    }

    internal static async Task CreateAsync(HelpdeskDbContext db)
    {
        await using var identity = Open(db);
        await identity.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }
}
