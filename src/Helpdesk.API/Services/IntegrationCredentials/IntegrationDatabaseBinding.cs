using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Helpdesk.API.Services.IntegrationCredentials;

internal static class IntegrationDatabaseBinding
{
    public static bool IsShared(DbContext application, DbContext identity)
    {
        if (application.Database.ProviderName != identity.Database.ProviderName) return false;
        if (ReferenceEquals(application.Database.GetDbConnection(), identity.Database.GetDbConnection())) return true;
        // Providers may redact passwords from an opened connection. Compare the
        // original configured binding, including credentials, before enlisting.
        var first = ConfiguredConnection(application);
        return !string.IsNullOrEmpty(first) && first == ConfiguredConnection(identity);
    }

    private static string? ConfiguredConnection(DbContext context) => context.GetService<IDbContextOptions>().Extensions
        .OfType<RelationalOptionsExtension>().SingleOrDefault()?.ConnectionString ?? context.Database.GetConnectionString();
}
