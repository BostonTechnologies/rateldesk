using System.Collections.Concurrent;
using Helpdesk.AgentClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Tests.Mcp;

public sealed class ConfigurationDoctorHttpTests
{
    [Theory]
    [InlineData(false, "cli")]
    [InlineData(true, "cli")]
    [InlineData(false, "stdio")]
    [InlineData(true, "stdio")]
    public async Task Native_doctor_checks_actual_API_routes_with_and_without_process_health(bool publicWebOrigin, string transport)
    {
        await using var server = await Server.StartAsync(publicWebOrigin);
        var report = await ConfigurationDoctor.CheckAsync(Resolution(server.Origin, transport), network: true, authenticate: true);

        Assert.True(report.ConfigurationValid, string.Join("; ", report.Checks.Select(check => check.Message)));
        Assert.All(report.Checks, check => Assert.True(check.Ok, check.Message));
        var reachability = Assert.Single(report.Checks, check => check.Name == "reachability");
        Assert.Contains("API version endpoint", reachability.Message);
        Assert.Contains("HTTP 200", reachability.Message);
        Assert.Single(report.Checks, check => check.Name == "authentication");
        Assert.Equal(new[] { "/api/v1/system/version", "/api/v1/auth/me" }, server.Requests.Select(request => request.Path));
        Assert.Equal("", server.Requests.First().Authorization);
        Assert.Equal("Bearer rdk_native_doctor", server.Requests.Last().Authorization);
    }

    [Fact]
    public async Task Network_only_does_not_send_an_existing_credential_or_call_authenticated_status()
    {
        await using var server = await Server.StartAsync(publicWebOrigin: true);
        var report = await ConfigurationDoctor.CheckAsync(Resolution(server.Origin, "cli"), network: true);

        Assert.True(report.ConfigurationValid, string.Join("; ", report.Checks.Select(check => check.Message)));
        Assert.All(report.Checks, check => Assert.True(check.Ok, check.Message));
        Assert.DoesNotContain(report.Checks, check => check.Name == "authentication");
        var request = Assert.Single(server.Requests);
        Assert.Equal("/api/v1/system/version", request.Path);
        Assert.Equal("", request.Authorization);
    }

    [Fact]
    public async Task Failed_anonymous_API_route_is_a_reachability_failure_even_when_process_health_is_healthy()
    {
        await using var server = await Server.StartAsync(publicWebOrigin: false, versionStatus: StatusCodes.Status503ServiceUnavailable);
        var report = await ConfigurationDoctor.CheckAsync(Resolution(server.Origin, "cli"), network: true);

        Assert.True(report.ConfigurationValid, string.Join("; ", report.Checks.Select(check => check.Message)));
        var failure = Assert.Single(report.Checks, check => check.Name == "reachability");
        Assert.False(failure.Ok);
        Assert.Contains("API version endpoint responded with HTTP 503", failure.Message);
        Assert.Equal("/api/v1/system/version", Assert.Single(server.Requests).Path);
    }

    [Fact]
    public async Task Version_route_keeps_the_callers_cancellation_instead_of_reporting_a_diagnostic_failure()
    {
        await using var server = await Server.StartAsync(publicWebOrigin: true, waitForCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var resolution = Resolution(server.Origin, "stdio");
        resolution.Validate();
        var check = ConfigurationDoctor.CheckAsync(resolution, network: true, ct: cancellation.Token);
        await server.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal("/api/v1/system/version", Assert.Single(server.Requests).Path);
    }

    private static ConfigurationResolution Resolution(string origin, string transport) => AgentClientConfigurationResolver.ResolveEffective(
        transport == "cli" ? ConfigurationSurface.Cli : ConfigurationSurface.Mcp,
        commandOverrides: AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = origin,
            ClientTransport = transport, CredentialPurpose = "api"
        }, environment: key => key == (transport == "cli" ? "RATELDESK_INTEGRATION_CREDENTIAL" : "RATELDESK_MCP_INTEGRATION_CREDENTIAL") ? "rdk_native_doctor"
            : key == (transport == "cli" ? "RATELDESK_CREDENTIAL_MODE" : "RATELDESK_MCP_CREDENTIAL_MODE") ? "integration" : null);

    private sealed class Server(WebApplication application) : IAsyncDisposable
    {
        public string Origin => application.Urls.Single();
        public ConcurrentQueue<(string Path, string Authorization)> Requests { get; } = new();
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Server> StartAsync(bool publicWebOrigin, int versionStatus = StatusCodes.Status200OK, bool waitForCancellation = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var application = builder.Build();
            var server = new Server(application);
            application.Use(async (context, next) =>
            {
                server.Requests.Enqueue((context.Request.Path.Value ?? "", context.Request.Headers.Authorization.ToString()));
                server.RequestStarted.TrySetResult();
                await next(context);
            });
            // Production Web forwards /api/v1/... and has no process-health route.
            // A direct API also exposes health, but both serve the version/status API.
            if (!publicWebOrigin) application.MapGet("/health/live", () => Results.Ok("Healthy"));
            application.MapGet("/api/v1/system/version", async (HttpContext context) =>
            {
                if (waitForCancellation) await Task.Delay(Timeout.Infinite, context.RequestAborted);
                return Results.Json(new { version = "disposable-fixture" }, statusCode: versionStatus);
            });
            application.MapGet("/api/v1/auth/me", (HttpContext context) => context.Request.Headers.Authorization == "Bearer rdk_native_doctor"
                ? Results.Json(new { id = "disposable-account", primaryOrganizationId = "disposable-organization" })
                : Results.Unauthorized());
            await application.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync() => await application.DisposeAsync();
    }
}
