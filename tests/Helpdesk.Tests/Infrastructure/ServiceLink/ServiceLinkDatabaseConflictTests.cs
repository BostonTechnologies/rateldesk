using Helpdesk.Infrastructure.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.ServiceLink;

public sealed class ServiceLinkDatabaseConflictTests
{
    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    public void Only_known_Postgres_transaction_aborts_and_EF_wrappers_are_recoverable(string sqlState)
    {
        var postgres = new PostgresException("Synthetic transaction abort", "ERROR", "ERROR", sqlState);
        Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(postgres));
        Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(new DbUpdateException("Synthetic save", postgres)));
        Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(new InvalidOperationException("Synthetic EF strategy",
            new DbUpdateException("Synthetic save", postgres))));
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new HttpRequestException("Synthetic transport", postgres)));
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new Exception("Unknown application wrapper", postgres)));
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new AggregateException(postgres)));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.ConnectionFailure)]
    [InlineData(PostgresErrorCodes.QueryCanceled)]
    [InlineData(PostgresErrorCodes.AdminShutdown)]
    public void Other_provider_errors_and_unknown_commit_failures_are_not_transaction_abort_retries(string sqlState)
    {
        var postgres = new PostgresException("Synthetic unrecoverable error", "ERROR", "ERROR", sqlState);
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(postgres));
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new InvalidOperationException("Synthetic EF strategy",
            new DbUpdateException("Synthetic save", postgres))));
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new InvalidOperationException("Unrelated programming failure")));
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new DbUpdateException("Unknown commit", new IOException("Synthetic connection loss"))));
    }
}
