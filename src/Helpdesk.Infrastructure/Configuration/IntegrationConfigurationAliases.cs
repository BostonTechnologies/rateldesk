using Helpdesk.Infrastructure.AiAssistant.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Configuration;

public static class IntegrationConfigurationAliases
{
    private const string OrchestratorSection = "Orchestrator";
    private const string LegacyOrchestratorSection = "Orchestration:Provider";
    private const string NetclawSection = "Netclaw";
    private const string LegacyNetclawSection = "AiAssistantChat";
    private static int compatibilityWarningsLogged;

    private static readonly string[] OrchestratorNames =
    [
        "Enabled", "ProviderName", "BaseUrl", "Audience", "Scope", "TokenEndpoint", "Authority",
        "ClientId", "ClientSecret", "HealthPath", "IngestPath", "CatalogPath"
    ];

    private static readonly string[] NetclawNames =
    [
        "Enabled", "Instance", "Endpoint", "DeviceToken", "AllowPrivateHttp", "IdleMinutes",
        "ConnectionCapacity", "TurnInactivityTimeout", "ActivityHeartbeatInterval"
    ];

    public static OrchestrationM2MOptions ReadOrchestrator(IConfiguration configuration)
    {
        var canonical = SelectCanonicalSection(configuration, OrchestratorSection, LegacyOrchestratorSection,
            "Enabled", "ProviderName", "BaseUrl", "Audience", "Scope", "TokenEndpoint", "Authority", "ClientId", "ClientSecret", "HealthPath", "IngestPath", "CatalogPath");
        var clientId = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "ClientId"));
        var clientSecret = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "ClientSecret"));
        if (!canonical)
        {
            // The old outbound profile used the global M2M client. Keep that
            // upgrade path only when the legacy provider profile has no
            // provider-specific credential fields. A canonical Orchestrator
            // profile never borrows the inbound M2M credential.
            if (!HasKey(configuration, $"{LegacyOrchestratorSection}:ClientId"))
                clientId ??= ReadString(configuration, "M2M:ClientId");
            if (!HasKey(configuration, $"{LegacyOrchestratorSection}:ClientSecret"))
                clientSecret ??= ReadString(configuration, "M2M:ClientSecret");
        }

        return new OrchestrationM2MOptions
        {
            Enabled = ReadBool(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "Enabled")),
            ProviderName = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "ProviderName"))
                ?? "NetRatel orchestrator",
            BaseUrl = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "BaseUrl")),
            Audience = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "Audience")),
            Scope = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "Scope")),
            TokenEndpoint = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "TokenEndpoint")),
            Authority = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "Authority")),
            ClientId = clientId,
            ClientSecret = clientSecret,
            HealthPath = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "HealthPath"))
                ?? "/internal/health",
            IngestPath = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "IngestPath"))
                ?? "/internal/ingest",
            CatalogPath = ReadString(configuration, Key(canonical, OrchestratorSection, LegacyOrchestratorSection, "CatalogPath"))
                ?? "/internal/catalog"
        };
    }

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

    public static bool HasDeploymentOrchestratorConfiguration(IConfiguration configuration)
        => HasDeploymentConfiguration(configuration, OrchestratorSection, LegacyOrchestratorSection, OrchestratorNames);

    public static bool HasDeploymentNetclawConfiguration(IConfiguration configuration)
        => HasDeploymentConfiguration(configuration, NetclawSection, LegacyNetclawSection, NetclawNames);

    public static void LogCompatibilityWarnings(IConfiguration configuration, ILogger logger)
    {
        if (Interlocked.Exchange(ref compatibilityWarningsLogged, 1) != 0) return;

        var legacyOrchestrator = HasDeploymentConfiguration(configuration, LegacyOrchestratorSection, LegacyOrchestratorSection, OrchestratorNames);
        var canonicalOrchestrator = HasDeploymentConfiguration(configuration, OrchestratorSection, OrchestratorSection, OrchestratorNames);
        var legacyNetclaw = HasDeploymentConfiguration(configuration, LegacyNetclawSection, LegacyNetclawSection, NetclawNames);
        var canonicalNetclaw = HasDeploymentConfiguration(configuration, NetclawSection, NetclawSection, NetclawNames);

        if (legacyOrchestrator)
        {
            logger.LogWarning("Legacy orchestration provider configuration keys are in use; migrate to the canonical Orchestrator__... namespace before the next major release.");
            if (!HasKey(configuration, $"{LegacyOrchestratorSection}:ClientId") &&
                (ReadString(configuration, "M2M:ClientId") is not null || ReadString(configuration, "M2M:ClientSecret") is not null))
            {
                logger.LogWarning("The legacy orchestration profile is using the deprecated M2M credential fallback; configure a dedicated Orchestrator__ClientId and Orchestrator__ClientSecret.");
            }
        }

        if (legacyNetclaw)
            logger.LogWarning("Legacy AI Assistant chat configuration keys are in use; migrate to the canonical Netclaw__... namespace before the next major release.");

        if (canonicalOrchestrator && legacyOrchestrator)
            logger.LogWarning("Both Orchestrator__... and Orchestration__Provider__... keys are present; effective configuration follows source priority, with the canonical namespace winning at equal priority.");

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
        => bool.TryParse(ReadString(configuration, key), out var value) && value;

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
        => int.TryParse(ReadString(configuration, key), out var value) ? value : fallback;

    private static TimeSpan ReadTimeSpan(IConfiguration configuration, string key, TimeSpan fallback)
        => TimeSpan.TryParse(ReadString(configuration, key), out var value) ? value : fallback;

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

            // The checked-in appsettings file is an example/default surface. It
            // must not prevent a database-managed profile from taking effect.
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
        if (string.IsNullOrWhiteSpace(value)) return false;
        var fileName = path is null ? string.Empty : System.IO.Path.GetFileName(path);
        if (!fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.StartsWith(OrchestratorSection + ":", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith(LegacyOrchestratorSection + ":", StringComparison.OrdinalIgnoreCase))
        {
            var sectionEnabled = ReadBool(configuration, key[..key.LastIndexOf(':')].TrimEnd(':') + ":Enabled");
            var baseUrl = ReadString(configuration, key[..key.LastIndexOf(':')].TrimEnd(':') + ":BaseUrl");
            if (!sectionEnabled && baseUrl is null) return true;
        }
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
}
