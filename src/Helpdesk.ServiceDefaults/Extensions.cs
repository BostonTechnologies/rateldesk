using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Formatting.Compact;

namespace Microsoft.Extensions.Hosting;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string HelpdeskMeterName = "Helpdesk";
    private const string HelpdeskActivitySourceName = "Helpdesk";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var serviceAttributes = ResolveServiceAttributes(builder);
        var resourceBuilder = CreateResourceBuilder(serviceAttributes);
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var verboseDependencyLogs = bool.TryParse(
            builder.Configuration["Observability:VerboseDependencyLogs"],
            out var parsedVerboseDependencyLogs) && parsedVerboseDependencyLogs;
        var consoleLogsEnabled = bool.TryParse(
            builder.Configuration["Observability:ConsoleLogsEnabled"],
            out var parsedConsoleLogsEnabled)
                ? parsedConsoleLogsEnabled
                : !useOtlpExporter;

        builder.Services.AddSerilog((_, loggerConfiguration) =>
        {
            loggerConfiguration
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", verboseDependencyLogs ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", verboseDependencyLogs ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Polly", verboseDependencyLogs ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Error)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithEnvironmentName()
                .Enrich.WithThreadId()
                .Enrich.WithSpan()
                .Enrich.WithProperty("service.name", serviceAttributes.ServiceName)
                .Enrich.WithProperty("service.version", serviceAttributes.ServiceVersion)
                .Enrich.WithProperty("service.namespace", serviceAttributes.ServiceNamespace)
                .Enrich.WithProperty("deployment.environment", serviceAttributes.DeploymentEnvironment)
                .Enrich.WithProperty("host.name", serviceAttributes.HostName)
                .Enrich.WithProperty("client", serviceAttributes.ClientName);

            if (consoleLogsEnabled)
            {
                loggerConfiguration.WriteTo.Console(new CompactJsonFormatter());
            }
        }, writeToProviders: true);

        if (!verboseDependencyLogs)
        {
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
            builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            builder.Logging.AddFilter("Polly", LogLevel.Error);
        }

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;
            logging.SetResourceBuilder(resourceBuilder);

            if (useOtlpExporter)
            {
                logging.AddOtlpExporter();
            }
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource
                    .AddService(
                        serviceName: serviceAttributes.ServiceName,
                        serviceVersion: serviceAttributes.ServiceVersion,
                        serviceInstanceId: serviceAttributes.HostName)
                    .AddAttributes(serviceAttributes.AsResourceAttributes());
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(HelpdeskMeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation();

                if (useOtlpExporter)
                {
                    metrics.AddOtlpExporter();
                }
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource(HelpdeskActivitySourceName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();

                if (useOtlpExporter)
                {
                    tracing.AddOtlpExporter();
                }
            });

        return builder;
    }

    public static HelpdeskServiceAttributes ResolveServiceAttributes<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var resourceAttributes = ParseResourceAttributes(builder.Configuration["OTEL_RESOURCE_ATTRIBUTES"]);
        var serviceName = FirstNonEmpty(
            builder.Configuration["OTEL_SERVICE_NAME"],
            builder.Configuration["Observability:ServiceName"],
            builder.Environment.ApplicationName);
        var serviceVersion = FirstNonEmpty(
            builder.Configuration["OTEL_SERVICE_VERSION"],
            typeof(Extensions).Assembly.GetName().Version?.ToString(),
            "1.0.0");
        var serviceNamespace = FirstNonEmpty(
            GetResourceAttribute(resourceAttributes, "service.namespace"),
            builder.Configuration["Observability:ServiceNamespace"],
            "helpdesk");
        var deploymentEnvironment = FirstNonEmpty(
            GetResourceAttribute(resourceAttributes, "deployment.environment"),
            builder.Environment.EnvironmentName,
            Environments.Production);
        var hostName = FirstNonEmpty(
            GetResourceAttribute(resourceAttributes, "host.name"),
            Environment.MachineName,
            "unknown-host");
        var clientName = FirstNonEmpty(
            GetResourceAttribute(resourceAttributes, "client.name"),
            builder.Configuration["Observability:ClientName"],
            "rateldesk");

        return new HelpdeskServiceAttributes(
            serviceName,
            serviceVersion,
            serviceNamespace,
            deploymentEnvironment,
            hostName,
            clientName);
    }

    private static ResourceBuilder CreateResourceBuilder(HelpdeskServiceAttributes serviceAttributes)
        => ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceAttributes.ServiceName,
                serviceVersion: serviceAttributes.ServiceVersion,
                serviceInstanceId: serviceAttributes.HostName)
            .AddAttributes(serviceAttributes.AsResourceAttributes());

    private static IReadOnlyDictionary<string, string> ParseResourceAttributes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Dictionary<string, string>();

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);
    }

    private static string GetResourceAttribute(IReadOnlyDictionary<string, string> attributes, string key)
        => attributes.TryGetValue(key, out var value) ? value : string.Empty;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}

public sealed record HelpdeskServiceAttributes(
    string ServiceName,
    string ServiceVersion,
    string ServiceNamespace,
    string DeploymentEnvironment,
    string HostName,
    string ClientName)
{
    public IReadOnlyList<KeyValuePair<string, object>> AsResourceAttributes()
        =>
        [
            new("service.namespace", ServiceNamespace),
            new("deployment.environment", DeploymentEnvironment),
            new("host.name", HostName),
            new("client.name", ClientName)
        ];
}
