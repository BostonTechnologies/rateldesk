using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Pairing;

public static class PairingDatabaseConflict
{
    public static bool IsConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException || current is Npgsql.PostgresException { SqlState: "40001" or "40P01" or "23505" } ||
                current is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 }) return true;
        }
        return false;
    }
}
