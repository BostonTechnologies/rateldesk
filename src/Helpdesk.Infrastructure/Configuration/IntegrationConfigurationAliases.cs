using Helpdesk.Infrastructure.AiAssistant.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Configuration;

public static class IntegrationConfigurationAliases
{
    private const string NetclawSection = "Netclaw";
    private const string LegacyNetclawSection = "AiAssistantChat";
    private static int compatibilityWarningsLogged;

    private static readonly string[] NetclawNames =
    [
        "Enabled", "Instance", "Endpoint", "DeviceToken", "AllowPrivateHttp", "IdleMinutes",
        "ConnectionCapacity", "TurnInactivityTimeout", "ActivityHeartbeatInterval"
    ];

    public static AiAssistantChatOptions ReadNetclaw(IConfiguration configuration)
    {
        var canonical = SelectCanonicalSection(configuration, NetclawSection, LegacyNetclawSection,
            "Enabled", "Instance", "Endpoint", "DeviceToken", "AllowPrivateHttp", "IdleMinutes", "ConnectionCapacity", "TurnInactivityTimeout", "ActivityHeartbeatInterval");
        return new AiAssistantChatOptions
        {
            Enabled = ReadBool(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "Enabled")),
            Instance = ReadString(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "Instance")) ?? "dev",
            Endpoint = ReadString(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "Endpoint")) ?? string.Empty,
            DeviceToken = ReadString(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "DeviceToken")) ?? string.Empty,
            AllowPrivateHttp = ReadBool(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "AllowPrivateHttp")),
            IdleMinutes = ReadInt(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "IdleMinutes"), 15),
            ConnectionCapacity = ReadInt(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "ConnectionCapacity"), 25),
            TurnInactivityTimeout = ReadTimeSpan(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "TurnInactivityTimeout"), TimeSpan.FromMinutes(5)),
            ActivityHeartbeatInterval = ReadTimeSpan(configuration, Key(canonical, NetclawSection, LegacyNetclawSection, "ActivityHeartbeatInterval"), TimeSpan.FromSeconds(15))
        };
    }

    public static bool HasDeploymentNetclawConfiguration(IConfiguration configuration)
        => HasDeploymentConfiguration(configuration, NetclawSection, LegacyNetclawSection, NetclawNames);

    public static string? GetDeploymentSourceKey(IConfiguration configuration, bool netclaw)
    {
        var canonicalSection = NetclawSection;
        var legacySection = LegacyNetclawSection;
        var names = NetclawNames;

        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                if (names.Any(name => provider.TryGet($"{canonicalSection}:{name}", out _)))
                    return canonicalSection;
                if (names.Any(name => provider.TryGet($"{legacySection}:{name}", out _)))
                    return legacySection;
            }
        }

        if (names.Any(name => configuration[$"{canonicalSection}:{name}"] is not null))
            return canonicalSection;
        return names.Any(name => configuration[$"{legacySection}:{name}"] is not null)
            ? legacySection
            : null;
    }

    public static void ValidateDeploymentConfiguration(IConfiguration configuration)
    {
        ValidateBool(configuration, NetclawSection, "Enabled");
        ValidateBool(configuration, LegacyNetclawSection, "Enabled");
        ValidateBool(configuration, NetclawSection, "AllowPrivateHttp");
        ValidateBool(configuration, LegacyNetclawSection, "AllowPrivateHttp");
        ValidateInt(configuration, NetclawSection, "IdleMinutes");
        ValidateInt(configuration, LegacyNetclawSection, "IdleMinutes");
        ValidateInt(configuration, NetclawSection, "ConnectionCapacity");
        ValidateInt(configuration, LegacyNetclawSection, "ConnectionCapacity");
        ValidateTimeSpan(configuration, NetclawSection, "TurnInactivityTimeout");
        ValidateTimeSpan(configuration, LegacyNetclawSection, "TurnInactivityTimeout");
        ValidateTimeSpan(configuration, NetclawSection, "ActivityHeartbeatInterval");
        ValidateTimeSpan(configuration, LegacyNetclawSection, "ActivityHeartbeatInterval");
    }

    public static void LogCompatibilityWarnings(IConfiguration configuration, ILogger logger)
    {
        if (Interlocked.Exchange(ref compatibilityWarningsLogged, 1) != 0) return;

        var legacyNetclaw = HasDeploymentConfiguration(configuration, LegacyNetclawSection, LegacyNetclawSection, NetclawNames);
        var canonicalNetclaw = HasDeploymentConfiguration(configuration, NetclawSection, NetclawSection, NetclawNames);

        if (legacyNetclaw)
            logger.LogWarning("Legacy AI Assistant chat configuration keys are in use; migrate to the canonical Netclaw__... namespace before the next major release.");

        if (canonicalNetclaw && legacyNetclaw)
            logger.LogWarning("Both Netclaw__... and AiAssistantChat__... keys are present; effective configuration follows source priority, with the canonical namespace winning at equal priority.");
    }

    private static string Key(bool canonical, string preferredSection, string legacySection, string name)
        => $"{(canonical ? preferredSection : legacySection)}:{name}";

    private static string? ReadString(IConfiguration configuration, string key)
    {
        var value = HasKey(configuration, key) ? configuration[key] : null;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ReadBool(IConfiguration configuration, string key)
    {
        if (!HasKey(configuration, key)) return false;
        var value = configuration[key];
        if (!bool.TryParse(value, out var parsed))
            throw InvalidValue(key, value, "true or false");
        return parsed;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        if (!HasKey(configuration, key)) return fallback;
        var value = configuration[key];
        if (!int.TryParse(value, out var parsed))
            throw InvalidValue(key, value, "an integer");
        return parsed;
    }

    private static TimeSpan ReadTimeSpan(IConfiguration configuration, string key, TimeSpan fallback)
    {
        if (!HasKey(configuration, key)) return fallback;
        var value = configuration[key];
        if (!TimeSpan.TryParse(value, out var parsed))
            throw InvalidValue(key, value, "a time span");
        return parsed;
    }

    private static bool HasKey(IConfiguration configuration, string key)
    {
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                if (provider.TryGet(key, out _)) return true;
            }
        }

        return configuration[key] is not null;
    }

    private static bool SelectCanonicalSection(
        IConfiguration configuration,
        string canonicalSection,
        string legacySection,
        params string[] names)
    {
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                var canonical = names.Any(name => provider.TryGet($"{canonicalSection}:{name}", out _));
                var legacy = names.Any(name => provider.TryGet($"{legacySection}:{name}", out _));
                if (canonical) return true;
                if (legacy) return false;
            }
        }

        return names.Any(name => configuration[$"{canonicalSection}:{name}"] is not null);
    }

    private static bool HasDeploymentConfiguration(
        IConfiguration configuration,
        string canonicalSection,
        string legacySection,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (HasMeaningfulDeploymentValue(configuration, $"{canonicalSection}:{name}") ||
                HasMeaningfulDeploymentValue(configuration, $"{legacySection}:{name}"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMeaningfulDeploymentValue(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return configuration[key] is not null;
        }

        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out var value)) continue;

            // Only the packaged base appsettings file may contain neutral example
            // values. Environment, secret, command-line, and appsettings.Production
            // values are operator intent, including explicit empty values.
            if (provider is Microsoft.Extensions.Configuration.FileConfigurationProvider file &&
                IsNeutralExampleValue(configuration, key, value, file.Source?.Path))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsNeutralExampleValue(IConfiguration configuration, string key, string? value, string? path)
    {
        var fileName = path is null ? string.Empty : System.IO.Path.GetFileName(path);
        if (!string.Equals(fileName, "appsettings.json", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (key.StartsWith(NetclawSection + ":", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith(LegacyNetclawSection + ":", StringComparison.OrdinalIgnoreCase))
        {
            var sectionEnabled = ReadBool(configuration, key[..key.LastIndexOf(':')].TrimEnd(':') + ":Enabled");
            var endpoint = ReadString(configuration, key[..key.LastIndexOf(':')].TrimEnd(':') + ":Endpoint");
            if (!sectionEnabled && endpoint is null) return true;
        }
        if (key.EndsWith(":Enabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var fileEnabled))
            return !fileEnabled;
        return key.EndsWith(":ProviderName", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("example", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateBool(IConfiguration configuration, string section, string name)
    {
        var key = $"{section}:{name}";
        if (HasKey(configuration, key) && !bool.TryParse(configuration[key], out _))
            throw InvalidValue(key, configuration[key], "true or false");
    }

    private static void ValidateInt(IConfiguration configuration, string section, string name)
    {
        var key = $"{section}:{name}";
        if (HasKey(configuration, key) && !int.TryParse(configuration[key], out _))
            throw InvalidValue(key, configuration[key], "an integer");
    }

    private static void ValidateTimeSpan(IConfiguration configuration, string section, string name)
    {
        var key = $"{section}:{name}";
        if (HasKey(configuration, key) && !TimeSpan.TryParse(configuration[key], out _))
            throw InvalidValue(key, configuration[key], "a time span");
    }

    private static InvalidOperationException InvalidValue(string key, string? value, string expected)
        => new($"Configuration key '{key}' has an invalid value. Expected {expected}; the value was not accepted.");
}
