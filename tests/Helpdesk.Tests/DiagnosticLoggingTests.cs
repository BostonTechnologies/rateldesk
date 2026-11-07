using System.Collections.Concurrent;
using Helpdesk.AgentClient.Logging;
using Helpdesk.API.Ops;
using Helpdesk.Application.Notifications;
using Helpdesk.Infrastructure.Logging;
using Helpdesk.Shared.DTOs.Notification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Helpdesk.Tests;

[CollectionDefinition("Diagnostic process streams", DisableParallelization = true)]
public sealed class DiagnosticProcessStreams;

[Collection("Diagnostic process streams")]
public sealed class DiagnosticLoggingTests
{
    [Theory]
    [InlineData("Helpdesk.Application.Workflows")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("System.Net.Http.HttpClient.Api.LogicalHandler")]
    [InlineData("Polly")]
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer")]
    public void Defaults_suppress_routine_events_and_retain_every_warning_and_above(string category)
    {
        var capture = new CapturingProvider();
        var builder = CreateBuilder();
        builder.Logging.AddProvider(capture);
        builder.ConfigureOpenTelemetry();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        EmitLevels(logger);

        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Critical }, capture.Events.Select(entry => entry.Level));
    }

    [Theory]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("Debug", LogLevel.Debug)]
    public void Environment_global_opt_in_controls_actual_application_and_dependency_emission(string value, LogLevel first)
    {
        const string name = "RatelDesk__Logging__MinimumLevel";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var builder = CreateBuilder();
            builder.Configuration.AddEnvironmentVariables();
            var capture = new CapturingProvider();
            builder.Logging.AddProvider(capture);
            builder.ConfigureOpenTelemetry();
            using var host = builder.Build();
            var loggers = host.Services.GetRequiredService<ILoggerFactory>();
            foreach (var category in new[] { "Helpdesk.Application", "Polly", "System.Net.Http.HttpClient", "Microsoft.EntityFrameworkCore.Database.Command" })
                EmitLevels(loggers.CreateLogger(category));

            Assert.Equal(4 * (6 - (int)first), capture.Events.Count);
            Assert.All(capture.Events, entry => Assert.True(entry.Level >= first));
            Assert.Equal(4, capture.Events.Count(entry => entry.Level == first));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void Environment_category_prefix_uses_longest_case_insensitive_match_over_packaged_rules()
    {
        const string name = "RatelDesk__Logging__Overrides__Helpdesk.Native";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "Debug");
        try
        {
            var builder = CreateBuilder(("RatelDesk:Logging:Overrides:Helpdesk", "Error"),
                ("Logging:LogLevel:Default", "Critical"), ("Logging:LogLevel:Helpdesk.Native", "None"));
            builder.Configuration.AddEnvironmentVariables();
            var capture = new CapturingProvider();
            builder.Logging.AddProvider(capture);
            builder.ConfigureOpenTelemetry();
            using var host = builder.Build();
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            EmitLevels(loggerFactory.CreateLogger("helpdesk.native.Transport"));
            EmitLevels(loggerFactory.CreateLogger("Helpdesk.Other"));
            EmitLevels(loggerFactory.CreateLogger("Other"));

            Assert.Equal(5, capture.Events.Count(entry => entry.Category == "helpdesk.native.Transport"));
            Assert.Equal(2, capture.Events.Count(entry => entry.Category == "Helpdesk.Other"));
            Assert.Equal(3, capture.Events.Count(entry => entry.Category == "Other"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Theory]
    [InlineData("RatelDesk:Logging:MinimumLevel", "verbose")]
    [InlineData("RatelDesk:Logging:MinimumLevel", "3")]
    [InlineData("RatelDesk:Logging:MinimumLevel", "-1")]
    [InlineData("RatelDesk:Logging:MinimumLevel", "")]
    [InlineData("RatelDesk:Logging:Overrides:Helpdesk", "secret-synthetic-invalid-value")]
    public void Invalid_levels_fail_before_host_build_without_echoing_values(string key, string value)
    {
        var builder = CreateBuilder((key, value));
        var failure = Assert.Throws<OptionsValidationException>(() => builder.AddRatelDeskDiagnosticLogging());
        Assert.Contains("RatelDesk:Logging", failure.Message, StringComparison.Ordinal);
        if (value.Length > 0)
            Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RatelDesk:Logging:Unknown", "test-value")]
    [InlineData("RatelDesk:Logging:MinimumLevel:Nested", "Information")]
    [InlineData("RatelDesk:Logging:Overrides", "Warning")]
    [InlineData("RatelDesk:Logging:Overrides:Helpdesk:Nested", "Debug")]
    [InlineData("RatelDesk:Logging:Overrides: ", "Debug")]
    public void Malformed_or_unknown_options_fail_before_host_build(string key, string value)
    {
        var builder = CreateBuilder((key, value));
        Assert.Throws<OptionsValidationException>(() => builder.AddRatelDeskDiagnosticLogging());
    }

    [Fact]
    public void Command_line_canonical_settings_override_environment_and_packaged_configuration()
    {
        const string variable = "RatelDesk__Logging__MinimumLevel";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "Information");
        try
        {
            var builder = CreateBuilder(("RatelDesk:Logging:MinimumLevel", "Error"));
            builder.Configuration.AddEnvironmentVariables();
            builder.Configuration.AddCommandLine(["--RatelDesk:Logging:MinimumLevel=Debug"]);
            var capture = new CapturingProvider();
            builder.Logging.AddProvider(capture);
            builder.ConfigureOpenTelemetry();
            using var host = builder.Build();

            EmitLevels(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk.CommandLine"));

            Assert.Equal(new[] { LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical },
                capture.Events.Select(entry => entry.Level));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Category_settings_are_bounded_and_None_disables_emission()
    {
        var configuration = new ConfigurationManager();
        configuration[$"{RatelDeskLoggingOptions.SectionName}:MinimumLevel"] = "None";
        var options = RatelDeskLoggingOptions.Load(configuration);
        Assert.False(options.IsEnabled("Helpdesk", LogLevel.Critical));
        Assert.False(options.IsEnabled("Helpdesk", LogLevel.None));
        configuration[$"{RatelDeskLoggingOptions.SectionName}:Overrides:{new string('x', 129)}"] = "Debug";
        Assert.Throws<OptionsValidationException>(() => RatelDeskLoggingOptions.Load(configuration));
        configuration = new ConfigurationManager();
        for (var index = 0; index < 65; index++)
            configuration[$"{RatelDeskLoggingOptions.SectionName}:Overrides:Category{index}"] = "Warning";
        Assert.Throws<OptionsValidationException>(() => RatelDeskLoggingOptions.Load(configuration));
    }

    [Fact]
    public void Legacy_dependency_opt_in_yields_to_canonical_global_and_category_policy()
    {
        var builder = CreateBuilder(("Observability:VerboseDependencyLogs", "true"));
        var policy = RatelDeskLoggingOptions.Load(builder.Configuration);
        Assert.True(policy.IsEnabled("Polly", LogLevel.Information));
        Assert.False(policy.IsEnabled("Helpdesk", LogLevel.Information));
        builder.Configuration["RatelDesk:Logging:Overrides:Microsoft"] = "Error";
        policy = RatelDeskLoggingOptions.Load(builder.Configuration);
        Assert.Equal(LogLevel.Error, policy.Threshold("Microsoft.EntityFrameworkCore.Database.Command"));
        builder.Configuration["RatelDesk:Logging:MinimumLevel"] = "Warning";
        policy = RatelDeskLoggingOptions.Load(builder.Configuration);
        Assert.False(policy.IsEnabled("Polly", LogLevel.Information));
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [InlineData("Yarp.ReverseProxy.Forwarder.HttpForwarder")]
    public void Verbosity_cannot_enable_framework_records_containing_full_capability_urls(string category)
    {
        var builder = CreateBuilder(("RatelDesk:Logging:MinimumLevel", "Debug"),
            ($"RatelDesk:Logging:Overrides:{category}", "Trace"));
        var capture = new CapturingProvider();
        builder.Logging.AddProvider(capture);
        builder.ConfigureOpenTelemetry();
        using var host = builder.Build();
        EmitLevels(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category));
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Critical }, capture.Events.Select(entry => entry.Level));
    }

    [Fact]
    public async Task One_console_record_preserves_otel_ops_and_notification_consumers_without_recursion()
    {
        var output = new StringWriter();
        var originalOutput = Console.Out;
        Console.SetOut(output);
        try
        {
            var builder = CreateBuilder(("Observability:ConsoleLogsEnabled", "true"));
            var notifications = new ConcurrentQueue<CreateNotificationRequest>();
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationService = Substitute.For<INotificationService>();
            ILogger? nestedLogger = null;
            notificationService.CreateNotificationAsync(Arg.Any<CreateNotificationRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    notifications.Enqueue(call.Arg<CreateNotificationRequest>());
                    nestedLogger!.LogWarning("nested-notification-diagnostic");
                    delivered.TrySetResult();
                    return Task.CompletedTask;
                });
            builder.Services.AddSingleton(notificationService);
            builder.Services.AddSingleton<ILoggerProvider>(services => new NotificationLoggerProvider(
                services.GetRequiredService<IServiceScopeFactory>(), "test", "synthetic", "test"));
            builder.Services.AddSingleton<AiAgentOpsLogBuffer>();
            builder.Services.AddSingleton<ILoggerProvider, AiAgentOpsLoggerProvider>();
            builder.ConfigureOpenTelemetry();
            var otel = new CapturingLogProcessor();
            builder.Services.Configure<OpenTelemetryLoggerOptions>(options => options.AddProcessor(otel));
            Assert.DoesNotContain(builder.Services, descriptor => descriptor.ImplementationType == typeof(ConsoleLoggerProvider));
            using var host = builder.Build();
            nestedLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk.Notifications.Dispatch");
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk.NativeTransport");

            logger.LogWarning("console-record-358-marker");
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Single(notifications);
            Assert.Single(otel.Messages, message => message == "console-record-358-marker");
            Assert.Single(host.Services.GetRequiredService<AiAgentOpsLogBuffer>().Query(null, null, "console-record-358-marker", null, 10).Items);
            Assert.Single(output.ToString().Split(Environment.NewLine), line => line.Contains("console-record-358-marker", StringComparison.Ordinal));
            Assert.DoesNotContain("NotificationLogger.Log called", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Otlp_console_selection_respects_explicit_compatibility_setting(string? consoleSetting, bool expectedConsole)
    {
        var originalOutput = Console.Out;
        var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var builder = CreateBuilder(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9"),
                ("Observability:ConsoleLogsEnabled", consoleSetting));
            builder.ConfigureOpenTelemetry();
            using var host = builder.Build();
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk").LogWarning("otlp-console-marker");
            Assert.Equal(expectedConsole, output.ToString().Contains("otlp-console-marker", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Early_bootstrap_and_normal_registration_keep_otel_when_otlp_disables_console(bool normalHosting)
    {
        var originalOutput = Console.Out;
        var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var builder = CreateBuilder(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9"),
                ("Observability:ConsoleLogsEnabled", null));
            builder.AddRatelDeskDiagnosticLogging();
            if (normalHosting)
                builder.ConfigureOpenTelemetry();
            var processor = new CapturingLogProcessor();
            // Configure only options: calling AddOpenTelemetry here would mask
            // a missing bootstrap provider by registering it from the test.
            builder.Services.Configure<OpenTelemetryLoggerOptions>(options => options.AddProcessor(processor));
            using var host = builder.Build();
            Assert.Single(host.Services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>());

            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk.Bootstrap").LogWarning("bootstrap-otlp-marker");

            Assert.Single(processor.Messages, message => message == "bootstrap-otlp-marker");
            Assert.DoesNotContain("bootstrap-otlp-marker", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public void Notification_formatter_failures_report_once_to_stderr_without_exception_secrets()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var provider = new NotificationLoggerProvider(services.GetRequiredService<IServiceScopeFactory>(), "test", "synthetic", "test");
        var output = new StringWriter();
        var original = Console.Error;
        Console.SetError(output);
        try
        {
            var logger = provider.CreateLogger("Helpdesk.Tests");
            for (var index = 0; index < 10; index++)
                logger.Log(LogLevel.Warning, 0, "ignored", null, (_, _) => throw new InvalidOperationException("synthetic-secret"));
            Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("Subsequent provider failures are suppressed", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-secret", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static HostApplicationBuilder CreateBuilder(params (string Key, string? Value)[] settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:ConsoleLogsEnabled"] = "false"
        });
        builder.Configuration.AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)));
        return builder;
    }

    private static void EmitLevels(ILogger logger)
    {
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })
            logger.Log(level, "synthetic diagnostic {Level}", level);
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level)> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Events);
        public void Dispose() => Events.Clear();
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<(string Category, LogLevel Level)> events) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            events.Enqueue((category, logLevel));
    }

    private sealed class CapturingLogProcessor : BaseProcessor<LogRecord>
    {
        public ConcurrentQueue<string?> Messages { get; } = new();
        public override void OnEnd(LogRecord data) => Messages.Enqueue(data.FormattedMessage);
    }
}
