using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Infrastructure.ServiceLink;

/// <summary>Database errors that prove PostgreSQL aborted this transaction.</summary>
public static class ServiceLinkDatabaseConflict
{
    public static bool IsAbortedTransaction(Exception error)
    {
        // EF's nonretrying Npgsql strategy can wrap a SaveChanges serialization
        // failure in InvalidOperationException -> DbUpdateException. Recognize
        // only those known wrappers and the two explicit transaction-abort codes.
        for (var depth = 0; depth < 4; depth++)
        {
            if (error is PostgresException postgres)
                return postgres.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
            if (error is not (InvalidOperationException or DbUpdateException) || error.InnerException is null)
                return false;
            error = error.InnerException;
        }
        return false;
    }
}
