using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.AgentClient.Logging;

/// <summary>Headless diagnostic policy shared by hosted applications and stdio.</summary>
public sealed class RatelDeskLoggingOptions
{
    public const string SectionName = "RatelDesk:Logging";
    public string MinimumLevel { get; set; } = nameof(LogLevel.Warning);
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsValid() => TryParseLevel(MinimumLevel, out _) && Overrides is { Count: <= 64 } &&
        Overrides.All(pair => pair.Key.Length is > 0 and <= 128 &&
                              !string.IsNullOrWhiteSpace(pair.Key) && !pair.Key.Any(char.IsControl) &&
                              TryParseLevel(pair.Value, out _));

    public static RatelDeskLoggingOptions Load(IConfiguration configuration)
    {
        var options = new RatelDeskLoggingOptions();
        var section = configuration.GetSection(SectionName);
        if (section.Value is not null || section.GetSection(nameof(MinimumLevel)).GetChildren().Any() ||
            section.GetSection(nameof(Overrides)).Value is not null ||
            section.GetSection(nameof(Overrides)).GetChildren().Any(category => category.Value is null || category.GetChildren().Any()))
            throw InvalidPolicy();
        try
        {
            section.Bind(options, binding => binding.ErrorOnUnknownConfiguration = true);
        }
        catch (InvalidOperationException)
        {
            throw InvalidPolicy();
        }
        if (!options.IsValid())
            throw InvalidPolicy();

        // Legacy dependency verbosity is an opt-in only. An explicit canonical
        // global threshold or covering category always takes precedence.
        if (configuration[$"{SectionName}:MinimumLevel"] is null &&
            bool.TryParse(configuration["Observability:VerboseDependencyLogs"], out var verbose) && verbose)
        {
            foreach (var category in new[] { "Microsoft.EntityFrameworkCore.Database.Command", "System.Net.Http.HttpClient", "Polly" })
                if (!options.Overrides.Keys.Any(prefix => category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    options.Overrides[category] = nameof(LogLevel.Information);
        }
        if (!options.IsValid())
            throw InvalidPolicy();
        return options;
    }

    private static OptionsValidationException InvalidPolicy() =>
        new(Options.DefaultName, typeof(RatelDeskLoggingOptions),
            ["RatelDesk:Logging requires Trace, Debug, Information, Warning, Error, Critical or None, and at most 64 nonempty category prefixes of at most 128 characters."]);

    public LogLevel Threshold(string? category)
    {
        _ = TryParseLevel(MinimumLevel, out var threshold);
        var longest = -1;
        foreach (var (prefix, value) in Overrides)
            if (category is not null && category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > longest)
            {
                _ = TryParseLevel(value, out threshold);
                longest = prefix.Length;
            }

        // These framework request records contain the complete URL, including
        // one-time public-page/setup capabilities. Verbosity must not expose it.
        if (IsCapabilityRequestCategory(category) && threshold < LogLevel.Warning)
            threshold = LogLevel.Warning;
        return threshold;
    }

    public bool IsEnabled(string? category, LogLevel level) =>
        level is >= LogLevel.Trace and < LogLevel.None && level >= Threshold(category);

    public LogLevel LowestThreshold()
    {
        _ = TryParseLevel(MinimumLevel, out var lowest);
        foreach (var value in Overrides.Values)
        {
            _ = TryParseLevel(value, out var candidate);
            if (candidate < lowest)
                lowest = candidate;
        }
        return lowest;
    }

    private static bool TryParseLevel(string? value, out LogLevel level) =>
        Enum.TryParse(value, ignoreCase: true, out level) && Enum.IsDefined(level) &&
        string.Equals(Enum.GetName(level), value, StringComparison.OrdinalIgnoreCase);

    private static bool IsCapabilityRequestCategory(string? category) => category is not null &&
        (category.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.OrdinalIgnoreCase) ||
         category.StartsWith("Yarp.ReverseProxy", StringComparison.OrdinalIgnoreCase));
}
