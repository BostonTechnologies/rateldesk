using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.AgentClient.Logging;

public static class RatelDeskLoggingRegistration
{
    /// <summary>Registers a startup snapshot; environment changes require a new process.</summary>
    public static IServiceCollection AddRatelDeskLoggingPolicy(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(service => service.ServiceType == typeof(LoggingPolicyRegistration)))
            return services;

        // Resolve before host construction as well, covering restricted bootstrap
        // and deterministic stdio startup errors with the same safe validation.
        var policy = RatelDeskLoggingOptions.Load(configuration);
        services.AddSingleton<LoggingPolicyRegistration>();
        services.AddOptions<RatelDeskLoggingOptions>()
            .Configure(options =>
            {
                options.MinimumLevel = policy.MinimumLevel;
                options.Overrides = new(policy.Overrides, StringComparer.OrdinalIgnoreCase);
            })
            .Validate(options => options.IsValid(), "Invalid RatelDesk:Logging diagnostic policy.")
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<LoggerFilterOptions>, DiagnosticFilter>());
        return services;
    }

    private sealed class LoggingPolicyRegistration;

    private sealed class DiagnosticFilter(IOptions<RatelDeskLoggingOptions> policy) : IPostConfigureOptions<LoggerFilterOptions>
    {
        public void PostConfigure(string? name, LoggerFilterOptions options)
        {
            // Canonical diagnostic Options govern all providers. Packaged or
            // legacy Logging:LogLevel rules must not veto explicit category opt-ins.
            options.MinLevel = LogLevel.Trace;
            options.Rules.Clear();
            options.Rules.Add(new LoggerFilterRule(null, null, null,
                (_, category, level) => policy.Value.IsEnabled(category, level)));
        }
    }
}
