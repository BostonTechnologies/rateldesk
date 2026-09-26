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
    private readonly DatabaseOptions databaseOptions;
    private readonly TimeProvider clock;
    private readonly ILogger<IntegrationProviderSettingsService> logger;
    private readonly IAiAssistantChatTransport? chatRuntime;
    private readonly IAiAssistantChatRuntimeState runtimeConfiguration;
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
        IAiAssistantChatTransport? chatRuntime = null,
        IAiAssistantChatRuntimeState? runtimeState = null)
    {
        this.db = db;
        this.configuration = configuration;
        this.secrets = secrets;
        this.databaseOptions = databaseOptions;
        this.clock = clock;
        this.logger = logger;
        this.chatRuntime = chatRuntime;
        runtimeConfiguration = runtimeState ?? new AiAssistantChatRuntimeState(chatOptions);
        IntegrationConfigurationAliases.LogCompatibilityWarnings(configuration, logger);
        deploymentOrchestrator = IntegrationConfigurationAliases.ReadOrchestrator(configuration);
        deploymentNetclaw = IntegrationConfigurationAliases.ReadNetclaw(configuration);
        orchestratorManagedByDeployment = IntegrationConfigurationAliases.HasDeploymentOrchestratorConfiguration(configuration);
        netclawManagedByDeployment = IntegrationConfigurationAliases.HasDeploymentNetclawConfiguration(configuration);
    }

    public async Task<OrchestrationConnectivitySettingsDto> GetOrchestratorSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            return ToOrchestratorDto(ResolveOrchestrator(deploymentOrchestrator, null, "deployment", managedByDeployment: true, resolveSecret: false), null);

        var stored = await LoadOrchestratorAsync(cancellationToken);
        var resolved = stored is null
            ? ResolveOrchestrator(null, null, "database", managedByDeployment: false, resolveSecret: false)
            : ResolveOrchestrator(null, stored, "database", managedByDeployment: false, resolveSecret: false);
        return ToOrchestratorDto(resolved, stored);
    }

    public async Task<OrchestrationResolvedSettings> GetResolvedOrchestratorSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            return ResolveOrchestrator(deploymentOrchestrator, null, "deployment", managedByDeployment: true, resolveSecret: false);

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
            : ResolveOrchestrator(null, stored, "database", managedByDeployment: false, resolveSecret: true);
    }

    public async Task<OrchestrationResolvedSettings> ResolveOrchestratorDraftAsync(
        UpdateOrchestrationConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            throw new InvalidOperationException("The NetRatel orchestrator is managed by deployment configuration and cannot be tested as a database draft.");

        var existing = await LoadOrchestratorAsync(cancellationToken);
        var currentRevision = existing?.Revision ?? 0;
        RequireExpectedRevision(request.ExpectedRevision, currentRevision, "The NetRatel orchestrator configuration changed; reload before testing the draft.");

        var baseUrl = NormalizeUrl(request.BaseUrl, "BaseUrl", request.AllowPrivateHttp);
        var authority = NormalizeUrl(request.Authority, "Authority", request.AllowPrivateHttp);
        var tokenEndpoint = NormalizeUrl(request.TokenEndpoint, "TokenEndpoint", request.AllowPrivateHttp);
        var audience = Normalize(request.Audience) ?? DefaultAudience;
        var scope = Normalize(request.Scope) ?? audience;
        var clientId = Normalize(request.ClientId);
        var remoteSystemName = Normalize(request.RemoteSystemName) ?? "NetRatel orchestrator";
        var healthPath = NormalizePath(request.HealthPath, "/internal/health");
        var ingestPath = NormalizePath(request.IngestPath, "/internal/ingest");
        var catalogPath = NormalizePath(request.CatalogPath, "/internal/catalog");
        var profileFingerprint = BuildOrchestratorSecretBindingFingerprint(
            baseUrl,
            authority,
            tokenEndpoint,
            audience,
            scope,
            clientId);

        string? clientSecret = null;
        if (!request.ClearClientSecret && !string.IsNullOrWhiteSpace(request.ClientSecret))
            clientSecret = request.ClientSecret.Trim();
        else if (!request.ClearClientSecret && !string.IsNullOrWhiteSpace(existing?.ProtectedClientSecret))
        {
            if (!CanRetainOrchestratorSecret(existing, profileFingerprint))
                throw new ArgumentException("The saved NetRatel secret is bound to a different destination or client. Provide a replacement or clear it before testing this draft.", nameof(request.ClientSecret));

            try
            {
                clientSecret = secrets.Unprotect(existing.ProtectedClientSecret);
            }
            catch (IntegrationProviderSecretUnavailableException)
            {
                throw new InvalidOperationException("The saved NetRatel secret is unavailable. Provide a replacement secret before testing this draft.");
            }
        }

        if (request.Enabled && string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("A NetRatel base URL is required when the orchestrator is enabled.", nameof(request.BaseUrl));
        if (request.Enabled && string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("A dedicated NetRatel M2M client id is required when the orchestrator is enabled.", nameof(request.ClientId));
        if (request.Enabled && string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("A dedicated NetRatel M2M client secret is required when the orchestrator is enabled.", nameof(request.ClientSecret));

        return ResolveOrchestrator(
            new OrchestrationM2MOptions
            {
                Enabled = request.Enabled,
                ProviderName = remoteSystemName,
                BaseUrl = baseUrl,
                Authority = authority,
                TokenEndpoint = tokenEndpoint,
                Audience = audience,
                Scope = scope,
                ClientId = clientId,
                ClientSecret = clientSecret,
                AllowPrivateHttp = request.AllowPrivateHttp,
                HealthPath = healthPath,
                IngestPath = ingestPath,
                CatalogPath = catalogPath
            },
            null,
            "draft",
            managedByDeployment: false,
            resolveSecret: true,
            revisionOverride: currentRevision,
            profileFingerprintOverride: profileFingerprint);
    }

    public async Task<OrchestrationConnectivitySettingsDto> UpdateOrchestratorSettingsAsync(
        UpdateOrchestrationConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            throw new InvalidOperationException("The NetRatel orchestrator is managed by deployment configuration and is read-only here.");

        var existing = await LoadOrchestratorAsync(cancellationToken);
        var currentRevision = existing?.Revision ?? 0;
        RequireExpectedRevision(request.ExpectedRevision, currentRevision, "The NetRatel orchestrator configuration changed; reload before saving.");

        var baseUrl = NormalizeUrl(request.BaseUrl, "BaseUrl", request.AllowPrivateHttp);
        var authority = NormalizeUrl(request.Authority, "Authority", request.AllowPrivateHttp);
        var tokenEndpoint = NormalizeUrl(request.TokenEndpoint, "TokenEndpoint", request.AllowPrivateHttp);
        var audience = Normalize(request.Audience) ?? DefaultAudience;
        var scope = Normalize(request.Scope) ?? audience;
        var clientId = Normalize(request.ClientId);
        var remoteSystemName = Normalize(request.RemoteSystemName) ?? "NetRatel orchestrator";
        var healthPath = NormalizePath(request.HealthPath, "/internal/health");
        var ingestPath = NormalizePath(request.IngestPath, "/internal/ingest");
        var catalogPath = NormalizePath(request.CatalogPath, "/internal/catalog");
        var nextRevision = currentRevision + 1;
        var secretBindingFingerprint = BuildOrchestratorSecretBindingFingerprint(
            baseUrl,
            authority,
            tokenEndpoint,
            audience,
            scope,
            clientId);
        var protectedSecret = existing?.ProtectedClientSecret ?? string.Empty;

        if (request.ClearClientSecret)
        {
            protectedSecret = string.Empty;
            secretBindingFingerprint = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            protectedSecret = secrets.Protect(request.ClientSecret.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(protectedSecret) &&
                 existing is not null &&
                 !CanRetainOrchestratorSecret(existing, secretBindingFingerprint))
        {
            throw new ArgumentException("The saved NetRatel secret is bound to a different destination or client. Clear it or provide a replacement secret before changing the authenticated profile.", nameof(request.ClientSecret));
        }

        if (request.Enabled && string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("A NetRatel base URL is required when the orchestrator is enabled.", nameof(request.BaseUrl));
        if (request.Enabled && string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("A dedicated NetRatel M2M client id is required when the orchestrator is enabled.", nameof(request.ClientId));
        if (request.Enabled && string.IsNullOrWhiteSpace(protectedSecret))
            throw new ArgumentException("A dedicated NetRatel M2M client secret is required when the orchestrator is enabled.", nameof(request.ClientSecret));

        var stored = existing ?? new M2MConnectivitySettings { ProviderKey = "Orchestrator" };
        stored.Enabled = request.Enabled;
        stored.RemoteBaseUrl = baseUrl;
        stored.RemoteAudience = audience;
        stored.RemoteScope = scope;
        stored.RemoteSystemName = remoteSystemName;
        stored.RemoteTokenEndpoint = tokenEndpoint;
        stored.RemoteAuthority = authority;
        stored.ClientId = clientId;
        stored.ProtectedClientSecret = protectedSecret;
        stored.SecretBindingFingerprint = secretBindingFingerprint;
        stored.SecretBindingRevision = string.IsNullOrWhiteSpace(protectedSecret) ? null : nextRevision;
        stored.ProfileFingerprint = secretBindingFingerprint;
        stored.AllowPrivateHttp = request.AllowPrivateHttp;
        stored.HealthPath = healthPath;
        stored.IngestPath = ingestPath;
        stored.CatalogPath = catalogPath;
        stored.Revision = nextRevision;
        stored.UpdatedAtUtc = clock.GetUtcNow();
        stored.LastAppliedAtUtc = stored.UpdatedAtUtc;
        stored.LastTestedAtUtc = null;
        stored.LastTestSucceeded = null;
        if (existing is null) db.M2MConnectivitySettings.Add(stored);
        await SaveProviderConfigurationAsync(cancellationToken);
        return ToOrchestratorDto(ResolveOrchestrator(null, stored, "database", managedByDeployment: false, resolveSecret: false), stored);
    }

    public async Task<bool> RecordOrchestratorTestAsync(
        int expectedRevision,
        string profileFingerprint,
        bool succeeded,
        CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment) return false;
        var stored = await LoadOrchestratorAsync(cancellationToken);
        if (stored is null || stored.Revision != expectedRevision || !string.Equals(stored.ProfileFingerprint, profileFingerprint, StringComparison.Ordinal))
            return false;
        stored.LastTestedAtUtc = clock.GetUtcNow();
        stored.LastTestSucceeded = succeeded;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task<NetclawConnectivitySettingsDto> GetNetclawSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            return ToNetclawDto(ResolveNetclaw(deploymentNetclaw, null, "deployment", managedByDeployment: true, resolveSecret: false), null);

        var stored = await LoadNetclawAsync(cancellationToken);
        var resolved = stored is null
            ? ResolveNetclaw(new AiAssistantChatOptions(), null, "database", managedByDeployment: false, resolveSecret: false)
            : ResolveNetclaw(null, stored, "database", managedByDeployment: false, resolveSecret: false);
        return ToNetclawDto(resolved, stored);
    }

    public async Task<NetclawResolvedSettings> GetResolvedNetclawSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            return ResolveNetclaw(deploymentNetclaw, null, "deployment", managedByDeployment: true, resolveSecret: false);

        var stored = await LoadNetclawAsync(cancellationToken);
        return stored is null
            ? ResolveNetclaw(new AiAssistantChatOptions(), null, "database", managedByDeployment: false, resolveSecret: true)
            : ResolveNetclaw(null, stored, "database", managedByDeployment: false, resolveSecret: true);
    }

    public async Task<NetclawResolvedSettings> ResolveNetclawDraftAsync(
        UpdateNetclawConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            throw new InvalidOperationException("Netclaw is managed by deployment configuration and cannot be tested as a database draft.");

        var existing = await LoadNetclawAsync(cancellationToken);
        var currentRevision = existing?.Revision ?? 0;
        RequireExpectedRevision(request.ExpectedRevision, currentRevision, "The Netclaw configuration changed; reload before testing the draft.");

        var instance = Normalize(request.Instance) ?? "dev";
        var endpoint = NormalizeUrl(request.Endpoint, "Endpoint", request.AllowPrivateHttp);
        var profileFingerprint = BuildNetclawSecretBindingFingerprint(instance, endpoint);
        string? deviceToken = null;

        if (!request.ClearDeviceToken && !string.IsNullOrWhiteSpace(request.DeviceToken))
            deviceToken = request.DeviceToken.Trim();
        else if (!request.ClearDeviceToken && !string.IsNullOrWhiteSpace(existing?.ProtectedDeviceToken))
        {
            if (!CanRetainNetclawSecret(existing, profileFingerprint))
                throw new ArgumentException("The saved Netclaw token is bound to a different endpoint or instance. Provide a replacement or clear it before testing this draft.", nameof(request.DeviceToken));

            try
            {
                deviceToken = secrets.Unprotect(existing.ProtectedDeviceToken);
            }
            catch (IntegrationProviderSecretUnavailableException)
            {
                throw new InvalidOperationException("The saved Netclaw token is unavailable. Provide a replacement token before testing this draft.");
            }
        }

        var candidate = new AiAssistantChatOptions
        {
            Enabled = request.Enabled,
            Instance = instance,
            Endpoint = endpoint ?? string.Empty,
            DeviceToken = deviceToken ?? string.Empty,
            AllowPrivateHttp = request.AllowPrivateHttp,
            IdleMinutes = request.IdleMinutes,
            ConnectionCapacity = request.ConnectionCapacity,
            TurnInactivityTimeout = request.TurnInactivityTimeout,
            ActivityHeartbeatInterval = request.ActivityHeartbeatInterval
        };
        if (request.Enabled && !candidate.IsValid())
            throw new ArgumentException("Enabled Netclaw configuration requires a valid dev /hub/session endpoint, paired-device token, and positive limits.");

        return ResolveNetclaw(
            candidate,
            null,
            "draft",
            managedByDeployment: false,
            resolveSecret: true,
            revisionOverride: currentRevision,
            profileFingerprintOverride: profileFingerprint);
    }

    public async Task<NetclawConnectivitySettingsDto> UpdateNetclawSettingsAsync(
        UpdateNetclawConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            throw new InvalidOperationException("Netclaw is managed by deployment configuration and is read-only here.");

        var existing = await LoadNetclawAsync(cancellationToken);
        var currentRevision = existing?.Revision ?? 0;
        RequireExpectedRevision(request.ExpectedRevision, currentRevision, "The Netclaw configuration changed; reload before saving.");

        var instance = Normalize(request.Instance) ?? "dev";
        var endpoint = NormalizeUrl(request.Endpoint, "Endpoint", request.AllowPrivateHttp);
        var idleMinutes = request.IdleMinutes;
        var capacity = request.ConnectionCapacity;
        var turnTimeout = request.TurnInactivityTimeout;
        var heartbeat = request.ActivityHeartbeatInterval;
        var nextRevision = currentRevision + 1;
        var secretBindingFingerprint = BuildNetclawSecretBindingFingerprint(instance, endpoint);
        var protectedToken = existing?.ProtectedDeviceToken ?? string.Empty;
        if (request.ClearDeviceToken)
        {
            protectedToken = string.Empty;
            secretBindingFingerprint = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.DeviceToken))
        {
            protectedToken = secrets.Protect(request.DeviceToken.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(protectedToken) &&
                 existing is not null &&
                 !CanRetainNetclawSecret(existing, secretBindingFingerprint))
        {
            throw new ArgumentException("The saved Netclaw token is bound to a different endpoint or instance. Clear it or provide a replacement token before changing the authenticated profile.", nameof(request.DeviceToken));
        }

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

        var stored = existing ?? new NetclawConnectivitySettings { ProviderKey = "Netclaw" };
        stored.Enabled = request.Enabled;
        stored.Instance = instance;
        stored.Endpoint = endpoint;
        stored.ProtectedDeviceToken = protectedToken;
        stored.SecretBindingFingerprint = secretBindingFingerprint;
        stored.SecretBindingRevision = string.IsNullOrWhiteSpace(protectedToken) ? null : nextRevision;
        stored.ProfileFingerprint = secretBindingFingerprint;
        stored.AllowPrivateHttp = request.AllowPrivateHttp;
        stored.IdleMinutes = idleMinutes;
        stored.ConnectionCapacity = capacity;
        stored.TurnInactivityTimeoutSeconds = checked((int)turnTimeout.TotalSeconds);
        stored.ActivityHeartbeatIntervalSeconds = checked((int)heartbeat.TotalSeconds);
        stored.Revision = nextRevision;
        stored.UpdatedAtUtc = clock.GetUtcNow();
        stored.LastAppliedAtUtc = null;
        stored.LastTestedAtUtc = null;
        stored.LastTestSucceeded = null;
        if (existing is null) db.NetclawConnectivitySettings.Add(stored);
        await SaveProviderConfigurationAsync(cancellationToken);

        var resolved = ResolveNetclaw(null, stored, "database", managedByDeployment: false, resolveSecret: true);
        await ApplyNetclawRuntimeAsync(resolved, cancellationToken);
        stored.LastAppliedAtUtc = clock.GetUtcNow();
        await SaveProviderConfigurationAsync(cancellationToken);
        resolved = ResolveNetclaw(null, stored, "database", managedByDeployment: false, resolveSecret: false);
        return ToNetclawDto(resolved, stored);
    }

    public async Task<bool> RecordNetclawTestAsync(
        int expectedRevision,
        string profileFingerprint,
        bool succeeded,
        CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment) return false;
        var stored = await LoadNetclawAsync(cancellationToken);
        if (stored is null || stored.Revision != expectedRevision || !string.Equals(stored.ProfileFingerprint, profileFingerprint, StringComparison.Ordinal))
            return false;
        stored.LastTestedAtUtc = clock.GetUtcNow();
        stored.LastTestSucceeded = succeeded;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task ApplyNetclawRuntimeAsync(NetclawResolvedSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Enabled && (settings.SecretUnavailable || string.IsNullOrWhiteSpace(settings.DeviceToken)))
            throw new InvalidOperationException("Netclaw cannot be enabled because its paired-device token is unavailable. Replace the token or restore the shared Data Protection key ring.");

        var snapshot = AiAssistantChatRuntimeSnapshot.From(settings);
        var applied = chatRuntime is not null
            ? await chatRuntime.TryReconfigureAsync(snapshot, cancellationToken)
            : runtimeConfiguration.TryPublish(snapshot);
        if (!applied)
            throw new IntegrationProviderConfigurationConflictException(
                "The Netclaw runtime has already applied a newer provider revision; reload before applying this profile.");
    }

    private async Task<M2MConnectivitySettings?> LoadOrchestratorAsync(CancellationToken cancellationToken)
        => await db.M2MConnectivitySettings
            .Where(x => x.ProviderKey == "Orchestrator")
            .OrderByDescending(x => x.Revision)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<NetclawConnectivitySettings?> LoadNetclawAsync(CancellationToken cancellationToken)
        => await db.NetclawConnectivitySettings
            .Where(x => x.ProviderKey == "Netclaw")
            .OrderByDescending(x => x.Revision)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task SaveProviderConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new IntegrationProviderConfigurationConflictException(
                "The integration provider configuration changed while it was being saved; reload before saving again.");
        }
        catch (DbUpdateException)
        {
            throw new IntegrationProviderConfigurationConflictException(
                "Another integration provider configuration save won the singleton creation race; reload before saving again.");
        }
    }

    private OrchestrationResolvedSettings ResolveOrchestrator(
        OrchestrationM2MOptions? options,
        M2MConnectivitySettings? stored,
        string source,
        bool managedByDeployment,
        bool resolveSecret,
        int? revisionOverride = null,
        string? profileFingerprintOverride = null)
    {
        var baseUrl = Normalize(options?.BaseUrl ?? stored?.RemoteBaseUrl);
        var authority = Normalize(options?.Authority ?? stored?.RemoteAuthority) ?? baseUrl;
        var audience = Normalize(options?.Audience ?? stored?.RemoteAudience) ?? DefaultAudience;
        var scope = Normalize(options?.Scope ?? stored?.RemoteScope) ?? audience;
        var tokenEndpoint = Normalize(options?.TokenEndpoint ?? stored?.RemoteTokenEndpoint) ?? BuildTokenEndpoint(authority);
        var clientId = Normalize(options?.ClientId ?? stored?.ClientId);
        var secret = options?.ClientSecret;
        var secretUnavailable = false;
        if (resolveSecret && secret is null && !string.IsNullOrWhiteSpace(stored?.ProtectedClientSecret))
        {
            try
            {
                secret = secrets.Unprotect(stored.ProtectedClientSecret);
            }
            catch (IntegrationProviderSecretUnavailableException)
            {
                secretUnavailable = true;
            }
        }
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
            AllowPrivateHttp = options?.AllowPrivateHttp ?? stored?.AllowPrivateHttp ?? false,
            RemoteSystemName = Normalize(options?.ProviderName ?? stored?.RemoteSystemName) ?? "NetRatel orchestrator",
            HealthPath = NormalizePath(options?.HealthPath ?? stored?.HealthPath, "/internal/health"),
            IngestPath = NormalizePath(options?.IngestPath ?? stored?.IngestPath, "/internal/ingest"),
            CatalogPath = NormalizePath(options?.CatalogPath ?? stored?.CatalogPath, "/internal/catalog"),
            UpdatedAtUtc = stored?.UpdatedAtUtc,
            LastAppliedAtUtc = stored?.LastAppliedAtUtc,
            LastTestedAtUtc = stored?.LastTestedAtUtc,
            LastTestSucceeded = stored?.LastTestSucceeded,
            Revision = revisionOverride ?? stored?.Revision ?? 0,
            Source = source,
            ManagedByDeployment = managedByDeployment,
            HasClientSecret = !string.IsNullOrWhiteSpace(options?.ClientSecret) || !string.IsNullOrWhiteSpace(stored?.ProtectedClientSecret),
            SecretUnavailable = secretUnavailable,
            SecretState = secretUnavailable ? "unavailable" :
                !string.IsNullOrWhiteSpace(options?.ClientSecret) ? "available" :
                !string.IsNullOrWhiteSpace(stored?.ProtectedClientSecret) ? (resolveSecret ? "available" : "configured") : "not-configured",
            SourceKey = managedByDeployment ? IntegrationConfigurationAliases.GetDeploymentSourceKey(configuration, netclaw: false) : "database",
            ProfileFingerprint = profileFingerprintOverride ?? stored?.ProfileFingerprint ?? BuildOrchestratorSecretBindingFingerprint(baseUrl, authority, tokenEndpoint, audience, scope, clientId)
        };
    }

    private NetclawResolvedSettings ResolveNetclaw(
        AiAssistantChatOptions? options,
        NetclawConnectivitySettings? stored,
        string source,
        bool managedByDeployment,
        bool resolveSecret,
        int? revisionOverride = null,
        string? profileFingerprintOverride = null)
    {
        var instance = Normalize(options?.Instance ?? stored?.Instance) ?? "dev";
        var endpoint = Normalize(options?.Endpoint ?? stored?.Endpoint);
        var token = options?.DeviceToken;
        var secretUnavailable = false;
        if (resolveSecret && token is null && !string.IsNullOrWhiteSpace(stored?.ProtectedDeviceToken))
        {
            try
            {
                token = secrets.Unprotect(stored.ProtectedDeviceToken);
            }
            catch (IntegrationProviderSecretUnavailableException)
            {
                secretUnavailable = true;
            }
        }
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
            Revision = revisionOverride ?? stored?.Revision ?? 0,
            Source = source,
            ManagedByDeployment = managedByDeployment,
            HasDeviceToken = !string.IsNullOrWhiteSpace(options?.DeviceToken) || !string.IsNullOrWhiteSpace(stored?.ProtectedDeviceToken),
            SecretUnavailable = secretUnavailable,
            SecretState = secretUnavailable ? "unavailable" :
                !string.IsNullOrWhiteSpace(options?.DeviceToken) ? "available" :
                !string.IsNullOrWhiteSpace(stored?.ProtectedDeviceToken) ? (resolveSecret ? "available" : "configured") : "not-configured",
            SourceKey = managedByDeployment ? IntegrationConfigurationAliases.GetDeploymentSourceKey(configuration, netclaw: true) : "database",
            ProfileFingerprint = profileFingerprintOverride ?? stored?.ProfileFingerprint ?? BuildNetclawSecretBindingFingerprint(instance, endpoint),
            CanAdoptLegacySessions = !string.IsNullOrWhiteSpace(endpoint) &&
                (managedByDeployment || stored is not null)
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
            SecretUnavailable = resolved.SecretUnavailable,
            SecretState = resolved.SecretState,
            AllowPrivateHttp = resolved.AllowPrivateHttp,
            ProfileFingerprint = resolved.ProfileFingerprint,
            SourceKey = resolved.SourceKey,
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
            HasDeviceToken = resolved.HasDeviceToken,
            SecretUnavailable = resolved.SecretUnavailable,
            SecretState = resolved.SecretState,
            SourceKey = resolved.SourceKey,
            ProfileFingerprint = resolved.ProfileFingerprint
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void RequireExpectedRevision(
        int? expectedRevision,
        int currentRevision,
        string conflictMessage)
    {
        if (expectedRevision is null)
            throw new IntegrationProviderConfigurationConflictException(
                "ExpectedRevision is required; reload the provider settings before saving or testing a draft.");
        if (expectedRevision.Value != currentRevision)
            throw new IntegrationProviderConfigurationConflictException(conflictMessage);
    }

    private static string BuildOrchestratorSecretBindingFingerprint(
        string? baseUrl,
        string? authority,
        string? tokenEndpoint,
        string? audience,
        string? scope,
        string? clientId)
        => IntegrationProviderSecretBinding.Fingerprint(
            "Orchestrator",
            baseUrl,
            authority ?? baseUrl,
            tokenEndpoint ?? BuildTokenEndpoint(authority ?? baseUrl),
            audience,
            scope,
            clientId);

    private static string BuildNetclawSecretBindingFingerprint(string? instance, string? endpoint)
        => IntegrationProviderSecretBinding.Fingerprint("Netclaw", instance, endpoint);

    private static bool CanRetainOrchestratorSecret(
        M2MConnectivitySettings existing,
        string candidateFingerprint)
    {
        var existingFingerprint = existing.SecretBindingFingerprint;
        if (string.IsNullOrWhiteSpace(existingFingerprint))
        {
            existingFingerprint = BuildOrchestratorSecretBindingFingerprint(
                existing.RemoteBaseUrl,
                existing.RemoteAuthority,
                existing.RemoteTokenEndpoint,
                existing.RemoteAudience ?? DefaultAudience,
                existing.RemoteScope ?? existing.RemoteAudience ?? DefaultScope,
                existing.ClientId);
        }

        return string.Equals(existingFingerprint, candidateFingerprint, StringComparison.Ordinal);
    }

    private static bool CanRetainNetclawSecret(
        NetclawConnectivitySettings existing,
        string candidateFingerprint)
    {
        var existingFingerprint = existing.SecretBindingFingerprint;
        if (string.IsNullOrWhiteSpace(existingFingerprint))
            existingFingerprint = BuildNetclawSecretBindingFingerprint(existing.Instance, existing.Endpoint);
        return string.Equals(existingFingerprint, candidateFingerprint, StringComparison.Ordinal);
    }

    private static string? NormalizeUrl(string? value, string fieldName, bool allowPrivateHttp = false)
    {
        var normalized = Normalize(value);
        if (normalized is null) return null;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            throw new ArgumentException($"{fieldName} must be an absolute HTTP or HTTPS URL.", fieldName);
        IntegrationEndpointPolicy.Validate(uri, fieldName, allowPrivateHttp);
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
