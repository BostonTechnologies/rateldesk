using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Persistence;

/// <summary>Opt-in Unicode case folding for bounded SQLite search queries.</summary>
public static class SqliteUnicodeSearch
{
    private const string FunctionName = "rateldesk_unicode_fold";

    public static string? Fold(string? value) => value?.ToUpperInvariant();

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder
        .HasDbFunction(() => Fold(string.Empty)).HasName(FunctionName);

    public static void RegisterConnection(HelpdeskDbContext db)
    {
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Unicode SQLite search requires a SQLite connection.");
        // Register on the EF-owned connection, including externally opened test connections.
        // This leaves the built-in LIKE/NOCASE behavior of existing endpoints unchanged.
        connection.CreateFunction<string?, string?>(FunctionName, Fold, isDeterministic: true);
    }
}
