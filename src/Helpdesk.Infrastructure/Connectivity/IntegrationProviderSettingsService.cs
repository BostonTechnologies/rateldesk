using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class IntegrationProviderSettingsService : IIntegrationProviderSettingsService
{
    private const string DefaultAudience = "netratel.api";
    private const string DefaultScope = "netratel.api";
    private readonly HelpdeskDbContext db;
    private readonly IConfiguration configuration;
    private readonly IntegrationProviderSecretProtector secrets;
    private readonly IOptions<AiAssistantChatOptions> chatOptions;
    private readonly DatabaseOptions databaseOptions;
    private readonly TimeProvider clock;
    private readonly ILogger<IntegrationProviderSettingsService> logger;
    private readonly IAiAssistantChatTransport? chatRuntime;
    private readonly OrchestrationM2MOptions deploymentOrchestrator;
    private readonly AiAssistantChatOptions deploymentNetclaw;
    private readonly bool orchestratorManagedByDeployment;
    private readonly bool netclawManagedByDeployment;

    public IntegrationProviderSettingsService(
        HelpdeskDbContext db,
        IConfiguration configuration,
        IntegrationProviderSecretProtector secrets,
        IOptions<AiAssistantChatOptions> chatOptions,
        DatabaseOptions databaseOptions,
        TimeProvider clock,
        ILogger<IntegrationProviderSettingsService> logger,
        IAiAssistantChatTransport? chatRuntime = null)
    {
        this.db = db;
        this.configuration = configuration;
        this.secrets = secrets;
        this.chatOptions = chatOptions;
        this.databaseOptions = databaseOptions;
        this.clock = clock;
        this.logger = logger;
        this.chatRuntime = chatRuntime;
        IntegrationConfigurationAliases.LogCompatibilityWarnings(configuration, logger);
        deploymentOrchestrator = IntegrationConfigurationAliases.ReadOrchestrator(configuration);
        deploymentNetclaw = IntegrationConfigurationAliases.ReadNetclaw(configuration);
        orchestratorManagedByDeployment = IntegrationConfigurationAliases.HasDeploymentOrchestratorConfiguration(configuration);
        netclawManagedByDeployment = IntegrationConfigurationAliases.HasDeploymentNetclawConfiguration(configuration);
    }

    public async Task<OrchestrationConnectivitySettingsDto> GetOrchestratorSettingsAsync(CancellationToken cancellationToken = default)
        => ToOrchestratorDto(await GetResolvedOrchestratorSettingsAsync(cancellationToken), await LoadOrchestratorAsync(cancellationToken));

    public async Task<OrchestrationResolvedSettings> GetResolvedOrchestratorSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            return ResolveOrchestrator(deploymentOrchestrator, null, "deployment", managedByDeployment: true);

        var stored = await LoadOrchestratorAsync(cancellationToken);
        return stored is null
            ? new OrchestrationResolvedSettings
            {
                ProviderKey = "Orchestrator",
                Enabled = false,
                Audience = DefaultAudience,
                Scope = DefaultScope,
                HealthPath = "/internal/health",
                IngestPath = "/internal/ingest",
                CatalogPath = "/internal/catalog",
                Source = "database",
                Revision = 0
            }
            : ResolveOrchestrator(null, stored, "database", managedByDeployment: false);
    }

    public async Task<OrchestrationConnectivitySettingsDto> UpdateOrchestratorSettingsAsync(
        UpdateOrchestrationConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            throw new InvalidOperationException("The NetRatel orchestrator is managed by deployment configuration and is read-only here.");

        var existing = await LoadOrchestratorAsync(cancellationToken);
        var currentRevision = existing?.Revision ?? 0;
        if (request.ExpectedRevision is not null && request.ExpectedRevision.Value != currentRevision)
            throw new IntegrationProviderConfigurationConflictException("The NetRatel orchestrator configuration changed; reload before saving.");

        var baseUrl = NormalizeUrl(request.BaseUrl, "BaseUrl");
        var authority = NormalizeUrl(request.Authority, "Authority");
        var tokenEndpoint = NormalizeUrl(request.TokenEndpoint, "TokenEndpoint");
        var audience = Normalize(request.Audience) ?? DefaultAudience;
        var scope = Normalize(request.Scope) ?? audience;
        var clientId = Normalize(request.ClientId);
        var remoteSystemName = Normalize(request.RemoteSystemName) ?? "NetRatel orchestrator";
        var healthPath = NormalizePath(request.HealthPath, "/internal/health");
        var ingestPath = NormalizePath(request.IngestPath, "/internal/ingest");
        var catalogPath = NormalizePath(request.CatalogPath, "/internal/catalog");
        var protectedSecret = existing?.ProtectedClientSecret ?? string.Empty;

        if (request.ClearClientSecret)
            protectedSecret = string.Empty;
        else if (!string.IsNullOrWhiteSpace(request.ClientSecret))
            protectedSecret = secrets.Protect(request.ClientSecret.Trim());

        if (request.Enabled && string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("A NetRatel base URL is required when the orchestrator is enabled.", nameof(request.BaseUrl));
        if (request.Enabled && string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("A dedicated NetRatel M2M client id is required when the orchestrator is enabled.", nameof(request.ClientId));
        if (request.Enabled && string.IsNullOrWhiteSpace(protectedSecret))
            throw new ArgumentException("A dedicated NetRatel M2M client secret is required when the orchestrator is enabled.", nameof(request.ClientSecret));

        var stored = existing ?? new M2MConnectivitySettings();
        stored.Enabled = request.Enabled;
        stored.RemoteBaseUrl = baseUrl;
        stored.RemoteAudience = audience;
        stored.RemoteScope = scope;
        stored.RemoteSystemName = remoteSystemName;
        stored.RemoteTokenEndpoint = tokenEndpoint;
        stored.RemoteAuthority = authority;
        stored.ClientId = clientId;
        stored.ProtectedClientSecret = protectedSecret;
        stored.HealthPath = healthPath;
        stored.IngestPath = ingestPath;
        stored.CatalogPath = catalogPath;
        stored.Revision = currentRevision + 1;
        stored.UpdatedAtUtc = clock.GetUtcNow();
        stored.LastAppliedAtUtc = stored.UpdatedAtUtc;
        stored.LastTestedAtUtc = null;
        stored.LastTestSucceeded = null;
        if (existing is null) db.M2MConnectivitySettings.Add(stored);
        await db.SaveChangesAsync(cancellationToken);
        return ToOrchestratorDto(ResolveOrchestrator(null, stored, "database", managedByDeployment: false), stored);
    }

    public async Task RecordOrchestratorTestAsync(bool succeeded, CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment) return;
        var stored = await LoadOrchestratorAsync(cancellationToken);
        if (stored is null) return;
        stored.LastTestedAtUtc = clock.GetUtcNow();
        stored.LastTestSucceeded = succeeded;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NetclawConnectivitySettingsDto> GetNetclawSettingsAsync(CancellationToken cancellationToken = default)
        => ToNetclawDto(await GetResolvedNetclawSettingsAsync(cancellationToken), await LoadNetclawAsync(cancellationToken));

    public async Task<NetclawResolvedSettings> GetResolvedNetclawSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            return ResolveNetclaw(deploymentNetclaw, null, "deployment", managedByDeployment: true);

        var stored = await LoadNetclawAsync(cancellationToken);
        return stored is null
            ? ResolveNetclaw(new AiAssistantChatOptions(), null, "database", managedByDeployment: false)
            : ResolveNetclaw(null, stored, "database", managedByDeployment: false);
    }

    public async Task<NetclawConnectivitySettingsDto> UpdateNetclawSettingsAsync(
        UpdateNetclawConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            throw new InvalidOperationException("Netclaw is managed by deployment configuration and is read-only here.");

        var existing = await LoadNetclawAsync(cancellationToken);
        var currentRevision = existing?.Revision ?? 0;
        if (request.ExpectedRevision is not null && request.ExpectedRevision.Value != currentRevision)
            throw new IntegrationProviderConfigurationConflictException("The Netclaw configuration changed; reload before saving.");

        var instance = Normalize(request.Instance) ?? "dev";
        var endpoint = NormalizeUrl(request.Endpoint, "Endpoint");
        var idleMinutes = request.IdleMinutes;
        var capacity = request.ConnectionCapacity;
        var turnTimeout = request.TurnInactivityTimeout;
        var heartbeat = request.ActivityHeartbeatInterval;
        var protectedToken = existing?.ProtectedDeviceToken ?? string.Empty;
        if (request.ClearDeviceToken)
            protectedToken = string.Empty;
        else if (!string.IsNullOrWhiteSpace(request.DeviceToken))
            protectedToken = secrets.Protect(request.DeviceToken.Trim());

        var candidate = new AiAssistantChatOptions
        {
            Enabled = request.Enabled,
            Instance = instance,
            Endpoint = endpoint ?? string.Empty,
            DeviceToken = string.IsNullOrWhiteSpace(request.DeviceToken) ? "stored-device-token" : request.DeviceToken.Trim(),
            AllowPrivateHttp = request.AllowPrivateHttp,
            IdleMinutes = idleMinutes,
            ConnectionCapacity = capacity,
            TurnInactivityTimeout = turnTimeout,
            ActivityHeartbeatInterval = heartbeat
        };
        if (request.Enabled && (string.IsNullOrWhiteSpace(protectedToken) || !candidate.IsValid()))
            throw new ArgumentException("Enabled Netclaw configuration requires a valid dev /hub/session endpoint, paired-device token, and positive limits.");

        var stored = existing ?? new NetclawConnectivitySettings();
        stored.Enabled = request.Enabled;
        stored.Instance = instance;
        stored.Endpoint = endpoint;
        stored.ProtectedDeviceToken = protectedToken;
        stored.AllowPrivateHttp = request.AllowPrivateHttp;
        stored.IdleMinutes = idleMinutes;
        stored.ConnectionCapacity = capacity;
        stored.TurnInactivityTimeoutSeconds = checked((int)turnTimeout.TotalSeconds);
        stored.ActivityHeartbeatIntervalSeconds = checked((int)heartbeat.TotalSeconds);
        stored.Revision = currentRevision + 1;
        stored.UpdatedAtUtc = clock.GetUtcNow();
        stored.LastAppliedAtUtc = stored.UpdatedAtUtc;
        stored.LastTestedAtUtc = null;
        stored.LastTestSucceeded = null;
        if (existing is null) db.NetclawConnectivitySettings.Add(stored);
        await db.SaveChangesAsync(cancellationToken);

        var resolved = ResolveNetclaw(null, stored, "database", managedByDeployment: false);
        await ApplyNetclawRuntimeAsync(resolved, cancellationToken);
        return ToNetclawDto(resolved, stored);
    }

    public async Task RecordNetclawTestAsync(bool succeeded, CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment) return;
        var stored = await LoadNetclawAsync(cancellationToken);
        if (stored is null) return;
        stored.LastTestedAtUtc = clock.GetUtcNow();
        stored.LastTestSucceeded = succeeded;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ApplyNetclawRuntimeAsync(NetclawResolvedSettings settings, CancellationToken cancellationToken = default)
    {
        chatOptions.Value.Apply(new AiAssistantChatOptions
        {
            Enabled = settings.Enabled,
            Instance = settings.Instance,
            Endpoint = settings.Endpoint ?? string.Empty,
            DeviceToken = settings.DeviceToken ?? string.Empty,
            AllowPrivateHttp = settings.AllowPrivateHttp,
            IdleMinutes = settings.IdleMinutes,
            ConnectionCapacity = settings.ConnectionCapacity,
            TurnInactivityTimeout = settings.TurnInactivityTimeout,
            ActivityHeartbeatInterval = settings.ActivityHeartbeatInterval
        });

        if (chatRuntime is not null)
            await chatRuntime.ReconfigureAsync(cancellationToken);
    }

    private async Task<M2MConnectivitySettings?> LoadOrchestratorAsync(CancellationToken cancellationToken)
        => await db.M2MConnectivitySettings
            .OrderByDescending(x => x.Revision)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<NetclawConnectivitySettings?> LoadNetclawAsync(CancellationToken cancellationToken)
        => await db.NetclawConnectivitySettings
            .OrderByDescending(x => x.Revision)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private OrchestrationResolvedSettings ResolveOrchestrator(
        OrchestrationM2MOptions? options,
        M2MConnectivitySettings? stored,
        string source,
        bool managedByDeployment)
    {
        var baseUrl = Normalize(options?.BaseUrl ?? stored?.RemoteBaseUrl);
        var authority = Normalize(options?.Authority ?? stored?.RemoteAuthority) ?? baseUrl;
        var audience = Normalize(options?.Audience ?? stored?.RemoteAudience) ?? DefaultAudience;
        var scope = Normalize(options?.Scope ?? stored?.RemoteScope) ?? audience;
        var tokenEndpoint = Normalize(options?.TokenEndpoint ?? stored?.RemoteTokenEndpoint) ?? BuildTokenEndpoint(authority);
        var clientId = Normalize(options?.ClientId ?? stored?.ClientId);
        var secret = options?.ClientSecret;
        if (secret is null && !string.IsNullOrWhiteSpace(stored?.ProtectedClientSecret))
            secret = secrets.Unprotect(stored.ProtectedClientSecret);
        var enabled = options?.Enabled ?? stored?.Enabled ?? false;

        return new OrchestrationResolvedSettings
        {
            ProviderKey = "Orchestrator",
            Enabled = enabled,
            BaseUrl = baseUrl,
            Audience = audience,
            Authority = authority,
            TokenEndpoint = tokenEndpoint,
            Scope = scope,
            ClientId = clientId,
            ClientSecret = Normalize(secret),
            RemoteSystemName = Normalize(options?.ProviderName ?? stored?.RemoteSystemName) ?? "NetRatel orchestrator",
            HealthPath = NormalizePath(options?.HealthPath ?? stored?.HealthPath, "/internal/health"),
            IngestPath = NormalizePath(options?.IngestPath ?? stored?.IngestPath, "/internal/ingest"),
            CatalogPath = NormalizePath(options?.CatalogPath ?? stored?.CatalogPath, "/internal/catalog"),
            UpdatedAtUtc = stored?.UpdatedAtUtc,
            LastAppliedAtUtc = stored?.LastAppliedAtUtc,
            LastTestedAtUtc = stored?.LastTestedAtUtc,
            LastTestSucceeded = stored?.LastTestSucceeded,
            Revision = stored?.Revision ?? 0,
            Source = source,
            ManagedByDeployment = managedByDeployment,
            HasClientSecret = !string.IsNullOrWhiteSpace(options?.ClientSecret) || !string.IsNullOrWhiteSpace(stored?.ProtectedClientSecret)
        };
    }

    private NetclawResolvedSettings ResolveNetclaw(
        AiAssistantChatOptions? options,
        NetclawConnectivitySettings? stored,
        string source,
        bool managedByDeployment)
    {
        var instance = Normalize(options?.Instance ?? stored?.Instance) ?? "dev";
        var endpoint = Normalize(options?.Endpoint ?? stored?.Endpoint);
        var token = options?.DeviceToken;
        if (token is null && !string.IsNullOrWhiteSpace(stored?.ProtectedDeviceToken))
            token = secrets.Unprotect(stored.ProtectedDeviceToken);
        var configuredEnabled = options?.Enabled ?? stored?.Enabled ?? false;
        var runtimeSupported = databaseOptions.ResolveProvider(configuration.GetConnectionString("HelpdeskDb")) is DatabaseProvider.PostgreSql;
        var runtimeEnabled = configuredEnabled && runtimeSupported;
        var runtimeIssue = configuredEnabled && !runtimeSupported
            ? "Native Netclaw chat requires PostgreSQL durable session ownership; SQLite webhook/history fallback remains available."
            : null;

        return new NetclawResolvedSettings
        {
            Enabled = runtimeEnabled,
            ConfiguredEnabled = configuredEnabled,
            RuntimeSupported = runtimeSupported,
            RuntimeIssue = runtimeIssue,
            Instance = instance,
            Endpoint = endpoint,
            DeviceToken = Normalize(token),
            AllowPrivateHttp = options?.AllowPrivateHttp ?? stored?.AllowPrivateHttp ?? false,
            IdleMinutes = options?.IdleMinutes ?? stored?.IdleMinutes ?? 15,
            ConnectionCapacity = options?.ConnectionCapacity ?? stored?.ConnectionCapacity ?? 25,
            TurnInactivityTimeout = options?.TurnInactivityTimeout ?? TimeSpan.FromSeconds(stored?.TurnInactivityTimeoutSeconds ?? 300),
            ActivityHeartbeatInterval = options?.ActivityHeartbeatInterval ?? TimeSpan.FromSeconds(stored?.ActivityHeartbeatIntervalSeconds ?? 15),
            UpdatedAtUtc = stored?.UpdatedAtUtc,
            LastAppliedAtUtc = stored?.LastAppliedAtUtc,
            LastTestedAtUtc = stored?.LastTestedAtUtc,
            LastTestSucceeded = stored?.LastTestSucceeded,
            Revision = stored?.Revision ?? 0,
            Source = source,
            ManagedByDeployment = managedByDeployment,
            HasDeviceToken = !string.IsNullOrWhiteSpace(options?.DeviceToken) || !string.IsNullOrWhiteSpace(stored?.ProtectedDeviceToken)
        };
    }

    private static OrchestrationConnectivitySettingsDto ToOrchestratorDto(
        OrchestrationResolvedSettings resolved,
        M2MConnectivitySettings? stored)
        => new()
        {
            ProviderKey = resolved.ProviderKey,
            Enabled = resolved.Enabled,
            BaseUrl = resolved.BaseUrl,
            Audience = resolved.Audience,
            Scope = resolved.Scope,
            Authority = resolved.Authority,
            TokenEndpoint = resolved.TokenEndpoint,
            RemoteSystemName = resolved.RemoteSystemName,
            UpdatedAtUtc = resolved.UpdatedAtUtc,
            LastAppliedAtUtc = resolved.LastAppliedAtUtc,
            LastTestedAtUtc = resolved.LastTestedAtUtc,
            LastTestSucceeded = resolved.LastTestSucceeded,
            Revision = resolved.Revision,
            Source = resolved.Source,
            ManagedByDeployment = resolved.ManagedByDeployment,
            HasClientSecret = resolved.HasClientSecret,
            HealthPath = resolved.HealthPath,
            IngestPath = resolved.IngestPath,
            CatalogPath = resolved.CatalogPath,
            ClientId = resolved.ClientId
        };

    private static NetclawConnectivitySettingsDto ToNetclawDto(
        NetclawResolvedSettings resolved,
        NetclawConnectivitySettings? stored)
        => new()
        {
            ProviderKey = resolved.ProviderKey,
            Enabled = resolved.ConfiguredEnabled,
            RuntimeSupported = resolved.RuntimeSupported,
            RuntimeIssue = resolved.RuntimeIssue,
            Instance = resolved.Instance,
            Endpoint = resolved.Endpoint,
            AllowPrivateHttp = resolved.AllowPrivateHttp,
            IdleMinutes = resolved.IdleMinutes,
            ConnectionCapacity = resolved.ConnectionCapacity,
            TurnInactivityTimeout = resolved.TurnInactivityTimeout,
            ActivityHeartbeatInterval = resolved.ActivityHeartbeatInterval,
            UpdatedAtUtc = resolved.UpdatedAtUtc,
            LastAppliedAtUtc = resolved.LastAppliedAtUtc,
            LastTestedAtUtc = resolved.LastTestedAtUtc,
            LastTestSucceeded = resolved.LastTestSucceeded,
            Revision = resolved.Revision,
            Source = resolved.Source,
            ManagedByDeployment = resolved.ManagedByDeployment,
            HasDeviceToken = resolved.HasDeviceToken
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeUrl(string? value, string fieldName)
    {
        var normalized = Normalize(value);
        if (normalized is null) return null;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            throw new ArgumentException($"{fieldName} must be an absolute HTTP or HTTPS URL.", fieldName);
        IntegrationEndpointPolicy.Validate(uri, fieldName);
        return normalized;
    }

    private static string NormalizePath(string? value, string fallback)
    {
        var path = Normalize(value) ?? fallback;
        if (!path.StartsWith('/')) path = "/" + path;
        if (path.Contains("//", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Provider paths must be rooted paths without traversal.");
        return path;
    }

    private static string? BuildTokenEndpoint(string? authority)
        => string.IsNullOrWhiteSpace(authority) ? null : $"{authority.TrimEnd('/')}/connect/token";
}

public sealed class IntegrationProviderRuntimeBootstrap(
    IServiceScopeFactory scopes,
    ILogger<IntegrationProviderRuntimeBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>();
            await settings.ApplyNetclawRuntimeAsync(await settings.GetResolvedNetclawSettingsAsync(cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Persisted Netclaw settings could not be applied at startup; the current runtime configuration remains unchanged.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
