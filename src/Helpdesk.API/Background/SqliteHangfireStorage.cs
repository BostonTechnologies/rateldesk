using Hangfire.Storage.SQLite;

namespace Helpdesk.API.Background;

internal static class SqliteHangfireStorage
{
    internal static SQLiteDbConnectionFactory CreateConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // The storage provider's distributed-lock heartbeat can access its
        // owner's connection from another thread. Its path overload selects
        // NoMutex, which is unsafe for that shared native connection.
        return new SQLiteDbConnectionFactory(() => new SQLite.SQLiteConnection(
            databasePath,
            SQLite.SQLiteOpenFlags.ReadWrite | SQLite.SQLiteOpenFlags.Create | SQLite.SQLiteOpenFlags.FullMutex,
            storeDateTimeAsTicks: true)
        {
            BusyTimeout = TimeSpan.FromSeconds(10)
        });
    }
}
