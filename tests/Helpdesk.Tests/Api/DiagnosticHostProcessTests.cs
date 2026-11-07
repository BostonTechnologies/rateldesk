using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Helpdesk.Tests.Api;

[Collection("Diagnostic process streams")]
public sealed class DiagnosticHostProcessTests
{
    [Theory]
    [InlineData(true, "Warning", false)]
    [InlineData(true, "Information", true)]
    [InlineData(false, "Warning", false)]
    [InlineData(false, "Information", true)]
    public async Task Actual_api_bootstrap_and_normal_host_use_the_same_startup_policy(bool bootstrap, string level, bool expectRoutineOutput)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-log-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var origin = new Uri($"http://127.0.0.1:{port}");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.Combine(TestEnvironment.RepositoryRoot, "src", "Helpdesk.API")
        };
        start.ArgumentList.Add(TestEnvironment.ApplicationAssembly("Helpdesk.API", "Helpdesk.API.dll"));
        foreach (var key in start.Environment.Keys.Where(key =>
                     key.StartsWith("RatelDesk__Logging", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["ASPNETCORE_URLS"] = origin.AbsoluteUri;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["Helpdesk__SkipDatabaseStartup"] = (!bootstrap).ToString();
        start.Environment["Bootstrap__StateDirectory"] = Path.Combine(directory, "state");
        start.Environment["Bootstrap__DataDirectory"] = Path.Combine(directory, "data");
        start.Environment["Database__Provider"] = "Sqlite";
        start.Environment["Database__Sqlite__Path"] = Path.Combine(directory, "rateldesk.db");
        start.Environment["ConnectionStrings__HelpdeskDb"] = "";
        start.Environment["DataProtection__KeyRingPath"] = Path.Combine(directory, "keys");
        start.Environment["RatelDesk__Logging__MinimumLevel"] = level;
        start.Environment["Observability__ConsoleLogsEnabled"] = "true";
        start.Environment["RUN_MIGRATIONS"] = "false";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = origin };
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            var ready = false;
            while (!ready && !process.HasExited && await timer.WaitForNextTickAsync(timeout.Token))
            {
                try
                {
                    using var response = await client.GetAsync("/health/ready", timeout.Token);
                    ready = response.IsSuccessStatusCode;
                }
                catch (HttpRequestException)
                {
                    // Poll the actual local host's readiness, within the shared deadline.
                    ready = false;
                }
            }
            if (!ready)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                Assert.True(ready, $"Synthetic API did not become ready: {await stderr}");
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(directory, recursive: true);
        }

        var output = await stdout;
        var routineMarker = bootstrap ? "RatelDesk setup is required" : "Skipping Hangfire startup";
        Assert.Equal(expectRoutineOutput, output.Contains(routineMarker, StringComparison.Ordinal));
        Assert.DoesNotContain("NotificationLoggerProvider.CreateLogger", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Request starting", output, StringComparison.Ordinal);
        if (!expectRoutineOutput)
            Assert.DoesNotContain("\"@l\":\"Debug\"", output, StringComparison.Ordinal);
    }
}
