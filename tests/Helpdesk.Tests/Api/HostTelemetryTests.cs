using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

public sealed class HostTelemetryTests
{
    [Fact]
    public async Task Api_provider_collects_real_PostgreSql_query_while_shared_provider_does_not()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var postgres = new PostgreSqlBuilder("postgres:16").Build();
        await postgres.StartAsync(timeout.Token);

        var apiSpans = new CapturingProcessor();
        await using var factory = new WebApplicationFactory<Program>();
        await using var api = factory.WithWebHostBuilder(builder =>
        {
            builder.UseIsolatedTestStorage();
            builder.UseEnvironment(Environments.Production);
            // Program snapshots telemetry settings while registering ServiceDefaults.
            // Host settings reach CreateBuilder before that code; the later
            // ConfigureAppConfiguration callback is too late for this snapshot.
            foreach (var setting in TelemetrySettings("api-telemetry-test"))
                builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>();
                settings["Jwt:Issuer"] = "test";
                settings["Jwt:Audience"] = "test";
                settings["Jwt:Key"] = "test-key-123456789012345678901234";
                settings["Helpdesk:SkipDatabaseStartup"] = "true";
                settings["ConnectionStrings:HelpdeskDb"] = postgres.GetConnectionString();
                settings["Database:Provider"] = "PostgreSql";
                configuration.AddInMemoryCollection(settings);
            });
            // Add only a collector and deterministic sampler to Program's provider.
            // Adding an ActivityListener or AddNpgsql here would hide missing host wiring.
            builder.ConfigureServices(services => services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.SetSampler(new AlwaysOnSampler()).AddProcessor(apiSpans)));
        });
        var apiProvider = api.Services.GetRequiredService<TracerProvider>();
        AssertResource(apiProvider, "api-telemetry-test");

        var sharedSpans = new CapturingProcessor();
        var sharedBuilder = Host.CreateApplicationBuilder();
        sharedBuilder.Configuration.AddInMemoryCollection(TelemetrySettings("shared-telemetry-test"));
        sharedBuilder.ConfigureOpenTelemetry();
        sharedBuilder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.SetSampler(new AlwaysOnSampler()).AddProcessor(sharedSpans));
        using var shared = sharedBuilder.Build();
        await shared.StartAsync(timeout.Token);
        var sharedProvider = shared.Services.GetRequiredService<TracerProvider>();

        // The standard API test host skips database startup. The command itself
        // uses a real driver/connection; no synthetic Npgsql activity is emitted.
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(timeout.Token);
        using var source = new ActivitySource("Helpdesk");
        ActivityTraceId traceId;
        ActivitySpanId parentId;
        using (var parent = source.StartActivity("api-postgresql-query"))
        {
            Assert.NotNull(parent);
            traceId = parent.TraceId;
            parentId = parent.SpanId;
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            var result = await command.ExecuteScalarAsync(timeout.Token);
            Assert.Equal(1, Assert.IsType<int>(result));
        }

        Assert.True(apiProvider.ForceFlush(5_000));
        Assert.True(sharedProvider.ForceFlush(5_000));
        var query = Assert.Single(apiSpans.Spans, activity =>
            activity.TraceId == traceId && activity.Source.Name == "Npgsql" &&
            activity.GetTagItem("db.query.text") is "SELECT 1");
        Assert.Equal(ActivityKind.Client, query.Kind);
        Assert.Equal(parentId, query.ParentSpanId);
        Assert.Equal("postgresql", query.GetTagItem("db.system.name"));
        Assert.Equal(connection.Database, query.GetTagItem("db.namespace"));
        Assert.NotEqual(ActivityStatusCode.Error, query.Status);
        Assert.Contains(apiSpans.Spans, activity => activity.TraceId == traceId && activity.SpanId == parentId);
        Assert.Contains(sharedSpans.Spans, activity => activity.TraceId == traceId && activity.SpanId == parentId);
        Assert.DoesNotContain(sharedSpans.Spans, activity => activity.TraceId == traceId && activity.Source.Name == "Npgsql");
        await shared.StopAsync(timeout.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shared_host_paths_preserve_Http_traces_health_filter_and_resources(bool useServiceDefaults)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var spans = new CapturingProcessor();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(HostTelemetryTests).Assembly.GetName().Name
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(TelemetrySettings("http-telemetry-test"));
        // Web uses AddServiceDefaults; HTTP MCP uses ConfigureOpenTelemetry.
        if (useServiceDefaults)
            builder.AddServiceDefaults();
        else
            builder.ConfigureOpenTelemetry();
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.SetSampler(new AlwaysOnSampler()).AddProcessor(spans));

        await using var app = builder.Build();
        app.MapGet("/probe", () => "telemetry");
        app.MapGet("/health", () => "healthy");
        app.MapGet("/alive", () => "alive");
        await app.StartAsync(timeout.Token);
        var provider = app.Services.GetRequiredService<TracerProvider>();
        AssertResource(provider, "http-telemetry-test");
        AssertResource(app.Services.GetRequiredService<MeterProvider>(), "http-telemetry-test");
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        Assert.NotNull(addresses);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(Assert.Single(addresses.Addresses)),
            Timeout = TimeSpan.FromSeconds(15)
        };
        using var source = new ActivitySource("Helpdesk");
        ActivityTraceId traceId;
        ActivitySpanId parentId;
        using (var parent = source.StartActivity("shared-http-requests"))
        {
            Assert.NotNull(parent);
            traceId = parent.TraceId;
            parentId = parent.SpanId;
            Assert.Equal("telemetry", await client.GetStringAsync("/probe", timeout.Token));
            Assert.Equal("healthy", await client.GetStringAsync("/health", timeout.Token));
            Assert.Equal("alive", await client.GetStringAsync("/alive", timeout.Token));
        }
        // Graceful stop completes server request activities before inspecting them.
        await app.StopAsync(timeout.Token);
        Assert.True(provider.ForceFlush(5_000));

        var trace = spans.Spans.Where(activity => activity.TraceId == traceId).ToArray();
        Assert.Contains(trace, activity => activity.Source.Name == "Helpdesk" && activity.SpanId == parentId);
        var requests = trace.Where(activity => activity.Kind == ActivityKind.Client && activity.Source.Name == "System.Net.Http").ToArray();
        Assert.Equal(3, requests.Length);
        Assert.All(requests, activity => Assert.Equal(parentId, activity.ParentSpanId));
        var server = Assert.Single(trace, activity => activity.Kind == ActivityKind.Server);
        Assert.Equal("/probe", server.GetTagItem("http.route"));
        var outgoing = Assert.Single(requests, activity =>
            activity.GetTagItem("url.full") is string url && new Uri(url).AbsolutePath == "/probe");
        Assert.Equal(outgoing.SpanId, server.ParentSpanId);
    }

    private static Dictionary<string, string?> TelemetrySettings(string serviceName) => new()
    {
        ["OTEL_SERVICE_NAME"] = serviceName,
        ["OTEL_SERVICE_VERSION"] = "0.0.0-telemetry-test",
        ["OTEL_RESOURCE_ATTRIBUTES"] = "service.namespace=rateldesk-test,deployment.environment=test,host.name=telemetry-test,client.name=rateldesk",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        ["Observability:ConsoleLogsEnabled"] = "false"
    };

    private static void AssertResource(BaseProvider provider, string serviceName)
    {
        var attributes = provider.GetResource().Attributes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Assert.Equal(serviceName, attributes["service.name"]);
        Assert.Equal("0.0.0-telemetry-test", attributes["service.version"]);
        Assert.Equal("rateldesk-test", attributes["service.namespace"]);
        Assert.Equal("test", attributes["deployment.environment"]);
        Assert.Equal("telemetry-test", attributes["host.name"]);
        Assert.Equal("rateldesk", attributes["client.name"]);
    }

    private sealed class CapturingProcessor : BaseProcessor<Activity>
    {
        private readonly ConcurrentQueue<Activity> _spans = new();

        public Activity[] Spans => _spans.ToArray();

        public override void OnEnd(Activity activity) => _spans.Enqueue(activity);
    }
}
