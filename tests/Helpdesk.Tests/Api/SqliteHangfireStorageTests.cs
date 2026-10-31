using System.Diagnostics;
using System.Runtime.InteropServices;
using Hangfire.Storage.SQLite;
using Helpdesk.API.Background;
using SQLite;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class SqliteHangfireStorageTests
{
    [Fact]
    public void NativeConnectionIsSerializedAndRetainsTimeoutAndTickStorage()
    {
        WithDatabase(path =>
        {
            using var connection = SqliteHangfireStorage.CreateConnectionFactory(path).Create();
            Assert.NotEqual(IntPtr.Zero, sqlite3_db_mutex(connection.Handle.DangerousGetHandle()));
            Assert.Equal(10_000, connection.ExecuteScalar<int>("PRAGMA busy_timeout"));

            connection.CreateTable<TimestampProbe>();
            var timestamp = new DateTime(2026, 10, 31, 7, 19, 26, DateTimeKind.Utc).AddTicks(2_589_912);
            connection.Insert(new TimestampProbe { Value = timestamp });
            Assert.Equal("integer", connection.ExecuteScalar<string>("SELECT typeof(Value) FROM TimestampProbe"));
            Assert.Equal(timestamp.Ticks, connection.Table<TimestampProbe>().Single().Value.Ticks);
        });
    }

    [Fact]
    public void LockHeartbeatCanShareBusyConnectionWithoutLosingJobsOrCorruptingDatabase()
    {
        WithDatabase(path =>
        {
            var factory = SqliteHangfireStorage.CreateConnectionFactory(path);
            using (var storage = new SQLiteStorage(factory, new SQLiteStorageOptions
            {
                DistributedLockLifetime = TimeSpan.FromSeconds(1)
            }))
            using (var connection = storage.GetConnection())
            using (connection.AcquireDistributedLock("native-mutex-regression", TimeSpan.FromSeconds(10)))
            {
                var job = Hangfire.Common.Job.FromExpression(() => Console.WriteLine("native storage regression"));
                var created = new HashSet<string>();
                var watch = Stopwatch.StartNew();
                var iteration = 0;
                do
                {
                    var value = (++iteration).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var id = connection.CreateExpiredJob(job, new Dictionary<string, string> { ["iteration"] = value },
                        DateTime.UtcNow, TimeSpan.FromDays(1));
                    Assert.True(created.Add(id), "Every actual job must receive its own persisted ID.");
                    Assert.Equal(value, connection.GetJobParameter(id, "iteration"));
                    connection.SetRangeInHash("native-mutex-progress", new Dictionary<string, string> { ["iteration"] = value });
                }
                while (watch.Elapsed < TimeSpan.FromSeconds(2));

                using var verification = factory.Create();
                Assert.Equal(iteration, verification.ExecuteScalar<int>("SELECT count(*) FROM Job"));
                Assert.Equal("ok", verification.ExecuteScalar<string>("PRAGMA integrity_check"));
            }

            using var final = factory.Create();
            Assert.Equal("ok", final.ExecuteScalar<string>("PRAGMA integrity_check"));
        });
    }

    private static void WithDatabase(Action<string> action)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.Combine(tempRoot, "rateldesk-hangfire-mutex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(Path.Combine(directory, "jobs.db"));
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(tempRoot), comparison))
                throw new InvalidOperationException("The test cleanup path escaped its temporary directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_db_mutex(IntPtr database);

    public sealed class TimestampProbe
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public DateTime Value { get; set; }
    }
}
