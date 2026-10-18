using System.Data.Common;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Helpdesk.Tests.Mcp;

[Collection("Incident receiver")]
public sealed class NotificationSummaryPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Summary_profiles_scoped_population_and_actor_receipts(bool postgres)
    {
        var capture = new SummaryCommands();
        await using var fixture = await NotificationSummaryFixture.CreateAsync(postgres,
            services => services.AddDbContext<HelpdeskDbContext>(options => options.AddInterceptors(capture)));
        await fixture.AddSummaryWorkloadAsync();
        var elapsed = new List<double>();
        for (var iteration = 0; iteration < 7; iteration++)
        {
            capture.Commands.Clear();
            var watch = Stopwatch.StartNew();
            await fixture.AssertSummaryAsync(607, 455, 153);
            watch.Stop();
            elapsed.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Single(capture.Commands);
        }
        elapsed.Sort();
        output.WriteLine("Provider={0}; seededNotifications=812; authorized=607; requests=7; summaryCommandsPerRequest={1}; medianMs={2:F3}; maxMs={3:F3}",
            postgres ? "PostgreSQL" : "SQLite", capture.Commands.Count, elapsed[3], elapsed[^1]);

        capture.Commands.Clear();
        using var other = await fixture.GetAsync("/api/v1/notifications/summary", fixture.OtherToken);
        other.EnsureSuccessStatusCode();
        NotificationSummaryHttpTests.AssertCounters(JsonNode.Parse(await other.Content.ReadAsStringAsync())!, 605, 484, 162);
        Assert.Single(capture.Commands);

        await fixture.ClearSummaryWorkloadAsync();
        capture.Commands.Clear();
        await fixture.AssertSummaryAsync(0, 0, 0);
        Assert.Single(capture.Commands);
    }

    private sealed class SummaryCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"Notifications\"", StringComparison.Ordinal))
                Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
