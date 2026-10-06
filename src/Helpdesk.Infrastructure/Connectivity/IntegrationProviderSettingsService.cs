using System.Data;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Connectivity;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

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
    private readonly IOptionsMonitor<ServiceIdentityOptions>? currentIdentityOptions;
    private readonly IOptionsMonitor<ServiceLinkOptions>? currentLinkOptions;

    public IntegrationProviderSettingsService(
        HelpdeskDbContext db,
        IConfiguration configuration,
        IntegrationProviderSecretProtector secrets,
        IOptions<AiAssistantChatOptions> chatOptions,
        DatabaseOptions databaseOptions,
        TimeProvider clock,
        ILogger<IntegrationProviderSettingsService> logger,
        IAiAssistantChatTransport? chatRuntime = null,
        IAiAssistantChatRuntimeState? runtimeState = null,
        IOptionsMonitor<ServiceIdentityOptions>? currentIdentityOptions = null,
        IOptionsMonitor<ServiceLinkOptions>? currentLinkOptions = null)
    {
        this.db = db;
        this.configuration = configuration;
        this.secrets = secrets;
        this.databaseOptions = databaseOptions;
        this.clock = clock;
        this.logger = logger;
        this.chatRuntime = chatRuntime;
        this.currentIdentityOptions = currentIdentityOptions;
        this.currentLinkOptions = currentLinkOptions;
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

        var stored = await LoadOrchestratorSnapshotAsync(cancellationToken);
        var resolved = stored is null
            ? ResolveOrchestrator(null, null, "database", managedByDeployment: false, resolveSecret: false)
            : ResolveOrchestrator(null, stored, "database", managedByDeployment: false, resolveSecret: false,
                enabledOverride: await CurrentLinkedSenderEnabledAsync(stored, cancellationToken));
        return ToOrchestratorDto(resolved, stored);
    }

    public async Task<OrchestrationResolvedSettings> GetResolvedOrchestratorSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            return ResolveOrchestrator(deploymentOrchestrator, null, "deployment", managedByDeployment: true, resolveSecret: false);

        var stored = await LoadOrchestratorSnapshotAsync(cancellationToken);
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
            : ResolveOrchestrator(null, stored, "database", managedByDeployment: false, resolveSecret: true,
                enabledOverride: await CurrentLinkedSenderEnabledAsync(stored, cancellationToken));
    }

    private async Task<bool> CurrentLinkedSenderEnabledAsync(M2MConnectivitySettings stored, CancellationToken ct)
    {
        if (stored.LinkId is null) return stored.Enabled;
        if (!stored.Enabled || !stored.ManagedSenderEnabled) return false;
        var currentIdentity = currentIdentityOptions?.CurrentValue;
        var currentLinking = currentLinkOptions?.CurrentValue;
        if (currentIdentity is null || currentLinking is null) return false;
        var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.LinkId == stored.LinkId &&
            x.LinkRevision == stored.LinkRevision && x.GrantHash == stored.GrantHash && x.LocalTenantId == stored.LocalTenantId &&
            x.PeerTenantId == stored.PeerTenantId && x.PeerInstanceId == stored.PeerInstanceId && x.Decision == "commit" &&
            x.LifecycleState == "active" && x.LocalInboundActive && x.LocalBusinessSenderEnabled && x.PeerActiveAcknowledged, ct);
        return attempt is not null && await ServiceLinkAuthority.InboundUsableAsync(db, attempt, clock, currentIdentity, currentLinking, ct);
    }

    public async Task<OrchestrationResolvedSettings> ResolveOrchestratorDraftAsync(
        UpdateOrchestrationConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            throw new InvalidOperationException("The NetRatel orchestrator is managed by deployment configuration and cannot be tested as a database draft.");

        var existing = await LoadOrchestratorAsync(cancellationToken);
        if (existing?.LinkId is not null)
            throw new InvalidOperationException("This provider belongs to an approved service link. Reconnect to approve a changed destination or grant.");
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
                clientSecret = UnprotectOrchestratorSecret(existing);
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

    public Task<OrchestrationConnectivitySettingsDto> UpdateOrchestratorSettingsAsync(
        UpdateOrchestrationConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
        => SaveOrchestratorSettingsAsync(request, null, cancellationToken);

    public Task<OrchestrationConnectivitySettingsDto> StageLinkedOrchestratorSettingsAsync(
        UpdateOrchestrationConnectivitySettingsDto request, ServiceLinkOrchestratorBinding binding,
        CancellationToken cancellationToken = default)
    {
        ValidateServiceLinkBinding(binding);
        if (request.Enabled) throw new ArgumentException("A staged service-link provider must remain disabled until the peer activation acknowledgement.", nameof(request));
        return SaveOrchestratorSettingsAsync(request, binding, cancellationToken);
    }

    private async Task<OrchestrationConnectivitySettingsDto> SaveOrchestratorSettingsAsync(
        UpdateOrchestrationConnectivitySettingsDto request, ServiceLinkOrchestratorBinding? binding,
        CancellationToken cancellationToken)
    {
        if (orchestratorManagedByDeployment)
            throw new InvalidOperationException("The NetRatel orchestrator is managed by deployment configuration and is read-only here.");

        var existing = await LoadOrchestratorAsync(cancellationToken);
        if (binding is null && existing?.LinkId is not null)
            throw new InvalidOperationException("This provider belongs to an approved service link. Reconnect to approve a changed destination or grant.");
        if (binding is not null && existing is not null)
        {
            if (existing.LinkId is not null && existing.LinkId != binding.LinkId)
            {
                if (!await CanReplaceTerminatedLinkedProviderAsync(existing, binding, cancellationToken))
                    throw new IntegrationProviderConfigurationConflictException("The provider is already owned by another configuration. Revoke its local business authority before a newly approved reconnect.", "provider_ownership_conflict");
                if (string.IsNullOrWhiteSpace(request.ClientSecret))
                    throw new ArgumentException("A new approved link requires its own outbound credential; the previous link secret cannot be retained.", nameof(request.ClientSecret));
            }
            else if (existing.LinkId is null && (!string.IsNullOrWhiteSpace(existing.RemoteBaseUrl) || !string.IsNullOrWhiteSpace(existing.ClientId)))
                throw new IntegrationProviderConfigurationConflictException("The provider is already owned by a manual configuration. Select an explicit ownership transition before linking.", "provider_ownership_conflict");
        }
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
            clientId, binding);
        var stored = existing ?? new M2MConnectivitySettings { ProviderKey = "Orchestrator" };
        var protectedSecret = existing?.ProtectedClientSecret ?? string.Empty;

        if (request.ClearClientSecret)
        {
            protectedSecret = string.Empty;
            secretBindingFingerprint = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            protectedSecret = secrets.Protect(request.ClientSecret.Trim(), OrchestratorSecretPurpose(stored.Id, secretBindingFingerprint));
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

        stored.Enabled = request.Enabled;
        stored.ManagedSenderEnabled = binding is null;
        ApplyServiceLinkBinding(stored, binding);
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

    private async Task<bool> CanReplaceTerminatedLinkedProviderAsync(M2MConnectivitySettings stored,
        ServiceLinkOrchestratorBinding next, CancellationToken ct)
    {
        if (stored.Enabled || stored.ManagedSenderEnabled || stored.LocalTenantId != next.LocalTenantId) return false;
        return await db.Set<ServiceLinkAttempt>().AsNoTracking().AnyAsync(x => x.LinkId == stored.LinkId &&
            x.LocalTenantId == next.LocalTenantId && !x.LocalInboundActive && !x.LocalBusinessSenderEnabled &&
            (x.LifecycleState == "revoked" || x.Decision == "abort" && (x.LifecycleState == "expired" || x.LifecycleState == "failed")), ct);
    }

    public async Task<OrchestrationConnectivitySettingsDto> SetLinkedOrchestratorSenderEnabledAsync(
        string linkId, long linkRevision, int expectedProfileRevision, bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (orchestratorManagedByDeployment)
            throw new InvalidOperationException("The NetRatel orchestrator is deployment-managed and read-only.");
        var stored = await LoadOrchestratorAsync(cancellationToken);
        if (stored is null || stored.LinkId != linkId || stored.LinkRevision != linkRevision)
            throw new IntegrationProviderConfigurationConflictException("The provider no longer belongs to this service link.", "provider_ownership_conflict");
        RequireExpectedRevision(expectedProfileRevision, stored.Revision, "The service-link provider changed; reload its current revision.");
        if (enabled && (string.IsNullOrWhiteSpace(stored.ProtectedClientSecret) || string.IsNullOrWhiteSpace(stored.RemoteBaseUrl)))
            throw new InvalidOperationException("An incomplete service-link provider cannot send business requests.");
        stored.Enabled = enabled;
        stored.ManagedSenderEnabled = enabled;
        stored.Revision++;
        stored.UpdatedAtUtc = clock.GetUtcNow();
        stored.LastAppliedAtUtc = stored.UpdatedAtUtc;
        await SaveProviderConfigurationAsync(cancellationToken);
        return ToOrchestratorDto(ResolveOrchestrator(null, stored, "database", false, false), stored);
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
        var testedAt = clock.GetUtcNow();
        return await db.M2MConnectivitySettings
            .Where(x => x.Id == stored.Id && x.Revision == expectedRevision && x.ProfileFingerprint == profileFingerprint)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.LastTestedAtUtc, testedAt)
                .SetProperty(x => x.LastTestSucceeded, succeeded), cancellationToken) == 1;
    }

    public async Task<NetclawConnectivitySettingsDto> GetNetclawSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            return ToNetclawDto(ResolveNetclaw(deploymentNetclaw, null, "deployment", managedByDeployment: true, resolveSecret: false), null);

        var stored = await db.NetclawConnectivitySettings
            .AsNoTracking()
            .Where(x => x.ProviderKey == "Netclaw")
            .OrderByDescending(x => x.Revision)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var resolved = stored is null
            ? ResolveNetclaw(new AiAssistantChatOptions(), null, "database", managedByDeployment: false, resolveSecret: false)
            : ResolveNetclaw(null, stored, "database", managedByDeployment: false, resolveSecret: false);
        return ToNetclawDto(resolved, stored);
    }

    public async Task<NetclawResolvedSettings> GetResolvedNetclawSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            return ResolveNetclaw(deploymentNetclaw, null, "deployment", managedByDeployment: true, resolveSecret: false);

        var stored = await db.NetclawConnectivitySettings
            .AsNoTracking()
            .Where(x => x.ProviderKey == "Netclaw")
            .OrderByDescending(x => x.Revision)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
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
        ValidateNetclawSessionLimits(
            request.IdleMinutes,
            request.ConnectionCapacity,
            request.TurnInactivityTimeout,
            request.ActivityHeartbeatInterval);

        var existing = await LoadNetclawAsync(cancellationToken);
        var currentRevision = existing?.Revision ?? 0;
        RequireExpectedRevision(
            request.ExpectedRevision,
            currentRevision,
            "The Netclaw configuration changed; reload before testing the draft.",
            "configuration_revision_conflict");

        var instance = Normalize(request.Instance) ?? "dev";
        var endpoint = NetclawEndpointNormalizer.Normalize(request.Endpoint, "Endpoint", request.AllowPrivateHttp);
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

    public async Task<NetclawPairingTarget> ResolveNetclawPairingTargetAsync(
        UpdateNetclawConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
        => await ResolveNetclawPairingTargetAsync(
            request,
            "unknown",
            legacyOwnershipReviewToken: null,
            cancellationToken);

    public async Task<NetclawPairingTarget> ResolveNetclawPairingTargetAsync(
        UpdateNetclawConnectivitySettingsDto request,
        string administratorId,
        string? legacyOwnershipReviewToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(administratorId))
            throw new ArgumentException("An authenticated administrator is required.", nameof(administratorId));
        if (netclawManagedByDeployment)
            throw new InvalidOperationException("Netclaw is managed by deployment configuration and cannot be paired here.");

        var allowPrivateHttp = NetclawEndpointNormalizer.IsPrivateHttpLiteral(request.Endpoint);

        // Pairing creates the token needed by an enabled profile. Validate the
        // rest of the draft with a server-only placeholder; it is never stored,
        // returned, or included in diagnostics.
        var candidate = await ResolveNetclawDraftAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = request.ExpectedRevision,
            Enabled = true,
            Instance = "dev",
            Endpoint = request.Endpoint,
            DeviceToken = "pairing-preflight-placeholder",
            AllowPrivateHttp = allowPrivateHttp,
            IdleMinutes = request.IdleMinutes,
            ConnectionCapacity = request.ConnectionCapacity,
            TurnInactivityTimeout = request.TurnInactivityTimeout,
            ActivityHeartbeatInterval = request.ActivityHeartbeatInterval
        }, cancellationToken);

        if (!candidate.ConfiguredEnabled || string.IsNullOrWhiteSpace(candidate.Endpoint))
            throw new ArgumentException("Enter the Netclaw daemon address shown by \"netclaw daemon pair\".");
        if (!candidate.RuntimeSupported)
            throw new NetclawPairingRuntimeUnavailableException("Native Netclaw pairing requires PostgreSQL durable session ownership.");
        if (!Uri.TryCreate(candidate.Endpoint, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("Enter the Netclaw daemon address shown by \"netclaw daemon pair\".");

        // Recheck the revision and ownership gate immediately before the
        // one-time remote code exchange. UpdateNetclawSettingsAsync repeats
        // these checks when persisting, covering races during the exchange.
        var existing = await LoadNetclawSnapshotAsync(cancellationToken);
        RequireExpectedRevision(
            request.ExpectedRevision,
            existing?.Revision ?? 0,
            "The Netclaw configuration changed; reload before pairing.",
            "configuration_revision_conflict");

        var profileFingerprint = BuildNetclawSecretBindingFingerprint(candidate.Instance, candidate.Endpoint);
        var unboundIds = await GetUnboundNetclawLegacySessionIdsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(legacyOwnershipReviewToken))
        {
            if (unboundIds.Count > 0)
                throw CreateLegacyReviewRequired(CreateNetclawLegacySessionReview(
                    unboundIds,
                    candidate.Revision,
                    profileFingerprint,
                    candidate.Endpoint));
        }
        else
        {
            await ConfirmPairingLegacySessionOwnershipAsync(
                candidate.Revision,
                candidate.Instance,
                candidate.Endpoint,
                profileFingerprint,
                legacyOwnershipReviewToken,
                administratorId,
                cancellationToken);
        }

        return new NetclawPairingTarget(endpoint, allowPrivateHttp);
    }

    public async Task<NetclawConnectivitySettingsDto> UpdateNetclawSettingsAsync(
        UpdateNetclawConnectivitySettingsDto request,
        CancellationToken cancellationToken = default)
    {
        if (netclawManagedByDeployment)
            throw new InvalidOperationException("Netclaw is managed by deployment configuration and is read-only here.");
        ValidateNetclawSessionLimits(
            request.IdleMinutes,
            request.ConnectionCapacity,
            request.TurnInactivityTimeout,
            request.ActivityHeartbeatInterval);

        var existing = await LoadNetclawAsync(cancellationToken);
        if (existing is not null)
            await db.Entry(existing).ReloadAsync(cancellationToken);
        var currentRevision = existing?.Revision ?? 0;
        RequireExpectedRevision(
            request.ExpectedRevision,
            currentRevision,
            "The Netclaw configuration changed; reload before saving.",
            "configuration_revision_conflict");

        var instance = Normalize(request.Instance) ?? "dev";
        var endpoint = NetclawEndpointNormalizer.Normalize(request.Endpoint, "Endpoint", request.AllowPrivateHttp);
        var idleMinutes = request.IdleMinutes;
        var capacity = request.ConnectionCapacity;
        var turnTimeout = request.TurnInactivityTimeout;
        var heartbeat = request.ActivityHeartbeatInterval;
        var nextRevision = currentRevision + 1;
        var secretBindingFingerprint = BuildNetclawSecretBindingFingerprint(instance, endpoint);
        if (!string.Equals(existing?.ProfileFingerprint, secretBindingFingerprint, StringComparison.Ordinal) &&
            await UnboundLegacySessions().AnyAsync(cancellationToken))
            throw new IntegrationProviderConfigurationConflictException(
                "Legacy Netclaw sessions have no provider binding. Confirm their historical provider before changing the saved profile.",
                "unbound_legacy_sessions");
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

    public async Task<NetclawLegacySessionConfirmationDto> ConfirmNetclawLegacySessionsAsync(
        ConfirmNetclawLegacySessionsDto request,
        string administratorId,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentException("A legacy session confirmation request is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(administratorId))
            throw new ArgumentException("An authenticated administrator is required.", nameof(administratorId));
        if (request.ExpectedEligibleConversations < 1)
            throw new ArgumentException("Confirm the positive number of legacy conversations to bind.", nameof(request.ExpectedEligibleConversations));
        var submittedIds = request.ConversationIds;
        if (submittedIds is null ||
            submittedIds.Count != request.ExpectedEligibleConversations ||
            submittedIds.Any(id => id == Guid.Empty) ||
            submittedIds.Distinct().Count() != submittedIds.Count)
            throw new ArgumentException("Identify each eligible legacy conversation exactly once.", nameof(request.ConversationIds));
        var conversationIds = submittedIds.ToArray();

        var instance = Normalize(request.HistoricalInstance) ?? "dev";
        var endpoint = NetclawEndpointNormalizer.Normalize(request.HistoricalEndpoint, "HistoricalEndpoint", request.AllowPrivateHttp);
        if (endpoint is null || !new AiAssistantChatOptions
            {
                Enabled = true,
                Instance = instance,
                Endpoint = endpoint,
                DeviceToken = "confirmation-only",
                AllowPrivateHttp = request.AllowPrivateHttp
            }.IsValid())
            throw new ArgumentException("Identify a valid historical Netclaw hub endpoint and instance.", nameof(request.HistoricalEndpoint));

        var fingerprint = BuildNetclawSecretBindingFingerprint(instance, endpoint);
        var auditEndpoint = new Uri(endpoint, UriKind.Absolute).GetLeftPart(UriPartial.Path);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var eligible = UnboundLegacySessions().Where(conversation => conversationIds.Contains(conversation.Id));
        var count = await eligible.CountAsync(cancellationToken);
        if (count != request.ExpectedEligibleConversations)
            throw new IntegrationProviderConfigurationConflictException(
                "The eligible legacy conversation count changed. Reload and confirm the historical provider again.",
                "legacy_session_conflict");

        var updated = await eligible.ExecuteUpdateAsync(
            setters => setters.SetProperty(conversation => conversation.ProviderProfileFingerprint, fingerprint),
            cancellationToken);
        if (updated != count)
            throw new IntegrationProviderConfigurationConflictException(
                "The eligible legacy conversations changed during confirmation. Retry after reloading.",
                "legacy_session_conflict");

        db.ActivityLogs.Add(new ActivityLog
        {
            UserId = administratorId,
            RelatedEntityId = "Netclaw",
            Message = $"Legacy Netclaw session owner confirmed. HistoricalInstance={instance}; HistoricalEndpoint={auditEndpoint}; ProviderFingerprint={fingerprint}; BoundConversations={updated}."
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NetclawLegacySessionConfirmationDto(updated, fingerprint);
    }

    private async Task ConfirmPairingLegacySessionOwnershipAsync(
        int expectedRevision,
        string instance,
        string endpoint,
        string profileFingerprint,
        string submittedReviewToken,
        string administratorId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var existing = await LoadNetclawSnapshotAsync(cancellationToken);
            RequireExpectedRevision(
                expectedRevision,
                existing?.Revision ?? 0,
                "The Netclaw configuration changed; reload and review pairing before continuing.",
                "configuration_revision_conflict");

            var eligibleIds = await GetUnboundNetclawLegacySessionIdsAsync(cancellationToken);
            var currentReview = CreateNetclawLegacySessionReview(eligibleIds, expectedRevision, profileFingerprint, endpoint);
            if (!ReviewTokensEqual(submittedReviewToken, currentReview.ReviewToken))
                throw CreateLegacyReviewConflict(currentReview);

            if (eligibleIds.Count == 0)
                throw CreateLegacyReviewConflict(currentReview);

            var updated = await UnboundLegacySessions()
                .Where(conversation => eligibleIds.Contains(conversation.Id))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(conversation => conversation.ProviderProfileFingerprint, profileFingerprint),
                    cancellationToken);
            if (updated != eligibleIds.Count)
                throw new ConcurrentLegacySessionSetChangedException();

            db.ActivityLogs.Add(new ActivityLog
            {
                UserId = administratorId,
                RelatedEntityId = "Netclaw",
                Message = $"Legacy Netclaw same-server ownership confirmed. Endpoint={endpoint}; ProviderFingerprint={profileFingerprint}; BoundConversations={updated}."
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ConcurrentLegacySessionSetChangedException)
        {
            var refreshedIds = await GetUnboundNetclawLegacySessionIdsAsync(cancellationToken);
            throw CreateLegacyReviewConflict(CreateNetclawLegacySessionReview(
                refreshedIds,
                expectedRevision,
                profileFingerprint,
                endpoint));
        }
        catch (Exception exception) when (IsLegacyReviewConcurrencyFailure(exception))
        {
            var refreshedIds = await GetUnboundNetclawLegacySessionIdsAsync(cancellationToken);
            var refreshedReview = CreateNetclawLegacySessionReview(
                refreshedIds,
                expectedRevision,
                profileFingerprint,
                endpoint);
            throw CreateLegacyReviewConflict(refreshedReview);
        }
    }

    private async Task<IReadOnlyList<Guid>> GetUnboundNetclawLegacySessionIdsAsync(
        CancellationToken cancellationToken)
        => await UnboundLegacySessions()
            .OrderBy(conversation => conversation.Id)
            .Select(conversation => conversation.Id)
            .ToListAsync(cancellationToken);

    private static NetclawLegacySessionReviewDto CreateNetclawLegacySessionReview(
        IReadOnlyList<Guid> conversationIds,
        int revision,
        string profileFingerprint,
        string endpoint)
    {
        var orderedIds = conversationIds.Order().Select(id => id.ToString("N"));
        var reviewMaterial = $"netclaw-legacy-review-v1\n{revision}\n{profileFingerprint}\n{string.Join('\n', orderedIds)}";
        var reviewToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reviewMaterial)));
        return new NetclawLegacySessionReviewDto(
            conversationIds.Count,
            endpoint,
            reviewToken);
    }

    private static bool ReviewTokensEqual(string provided, string expected)
    {
        if (provided.Length != expected.Length)
            return false;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static NetclawLegacySessionReviewRequiredException CreateLegacyReviewRequired(
        NetclawLegacySessionReviewDto review)
    {
        var message = review.ConversationCount == 1
            ? "1 older conversation needs provider confirmation before pairing."
            : $"{review.ConversationCount} older conversations need provider confirmation before pairing.";
        return new NetclawLegacySessionReviewRequiredException(new NetclawLegacySessionReviewConflictDto(
            "legacy_ownership_confirmation_required",
            message,
            review.CanonicalEndpoint,
            NetclawEndpointNormalizer.ToDaemonAddress(review.CanonicalEndpoint),
            review.ConversationCount,
            review.ReviewToken));
    }

    private static NetclawLegacySessionReviewConflictException CreateLegacyReviewConflict(
        NetclawLegacySessionReviewDto review)
        => new(
            "The older-conversation list changed. Review it again; your pairing code was not used.",
            review);

    private static bool IsLegacyReviewConcurrencyFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException)
                return true;
            if (current is PostgresException postgresException &&
                postgresException.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
                return true;
        }

        return false;
    }

    private sealed class ConcurrentLegacySessionSetChangedException()
        : Exception("The unbound legacy session set changed during pairing ownership confirmation.")
    {
    }

    public async Task<IReadOnlyList<NetclawUnboundLegacySessionDto>> GetUnboundNetclawLegacySessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = await UnboundLegacySessions()
            .AsNoTracking()
            .Select(conversation => new NetclawUnboundLegacySessionDto(
                conversation.Id,
                conversation.OrganizationId,
                conversation.TicketId,
                conversation.TicketType,
                conversation.LastActivityUtc))
            .ToListAsync(cancellationToken);
        return sessions
            .OrderBy(conversation => conversation.LastActivityUtc)
            .ThenBy(conversation => conversation.ConversationId)
            .ToArray();
    }

    private IQueryable<AiAssistantChatConversation> UnboundLegacySessions()
        => db.Set<AiAssistantChatConversation>().Where(conversation =>
            conversation.ProviderProfileFingerprint == null &&
            conversation.AiAssistantSessionId != null &&
            conversation.AiAssistantSessionId != "");

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
        var testedAt = clock.GetUtcNow();
        var updated = await db.NetclawConnectivitySettings
            .Where(x => x.Id == stored.Id && x.Revision == expectedRevision && x.ProfileFingerprint == profileFingerprint)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.LastTestedAtUtc, testedAt)
                .SetProperty(x => x.LastTestSucceeded, succeeded), cancellationToken) == 1;
        return updated;
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
                "The Netclaw runtime has already applied a newer provider revision; reload before applying this profile.",
                "runtime_revision_conflict");
    }

    private async Task<M2MConnectivitySettings?> LoadOrchestratorAsync(CancellationToken cancellationToken)
        => await db.M2MConnectivitySettings
            .Where(x => x.ProviderKey == "Orchestrator")
            .OrderByDescending(x => x.Revision)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<M2MConnectivitySettings?> LoadOrchestratorSnapshotAsync(CancellationToken cancellationToken)
        => await db.M2MConnectivitySettings.AsNoTracking()
            .Where(x => x.ProviderKey == "Orchestrator")
            .OrderByDescending(x => x.Revision).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<NetclawConnectivitySettings?> LoadNetclawAsync(CancellationToken cancellationToken)
        => await db.NetclawConnectivitySettings
            .Where(x => x.ProviderKey == "Netclaw")
            .OrderByDescending(x => x.Revision)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<NetclawConnectivitySettings?> LoadNetclawSnapshotAsync(CancellationToken cancellationToken)
        => await db.NetclawConnectivitySettings
            .AsNoTracking()
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
        string? profileFingerprintOverride = null,
        bool? enabledOverride = null)
    {
        var baseUrl = Normalize(options?.BaseUrl ?? stored?.RemoteBaseUrl);
        var authority = options?.SuppressedDefaults.Contains("Authority") == true ? string.Empty : Normalize(options?.Authority ?? stored?.RemoteAuthority) ?? baseUrl;
        var audience = options?.SuppressedDefaults.Contains("Audience") == true ? string.Empty : Normalize(options?.Audience ?? stored?.RemoteAudience) ?? DefaultAudience;
        var scope = options?.SuppressedDefaults.Contains("Scope") == true ? string.Empty : Normalize(options?.Scope ?? stored?.RemoteScope) ?? audience;
        var tokenEndpoint = options?.SuppressedDefaults.Contains("TokenEndpoint") == true ? string.Empty : Normalize(options?.TokenEndpoint ?? stored?.RemoteTokenEndpoint) ?? BuildTokenEndpoint(authority);
        var clientId = Normalize(options?.ClientId ?? stored?.ClientId);
        var secret = options?.ClientSecret;
        var secretUnavailable = false;
        if (resolveSecret && secret is null && !string.IsNullOrWhiteSpace(stored?.ProtectedClientSecret))
        {
            try
            {
                secret = UnprotectOrchestratorSecret(stored);
            }
            catch (IntegrationProviderSecretUnavailableException)
            {
                secretUnavailable = true;
            }
        }
        var enabled = (enabledOverride ?? options?.Enabled ?? stored?.Enabled ?? false) && (stored?.LinkId is null || stored.ManagedSenderEnabled);

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
            AllowPrivateHttp = stored?.LinkId is not null ? currentLinkOptions?.CurrentValue.AllowPrivateHttp == true :
                options?.AllowPrivateHttp ?? stored?.AllowPrivateHttp ?? false,
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
            ProfileFingerprint = profileFingerprintOverride ?? stored?.ProfileFingerprint ?? BuildOrchestratorSecretBindingFingerprint(baseUrl, authority, tokenEndpoint, audience, scope, clientId),
            ServiceLink = stored is null ? null : ServiceLinkBinding(stored)
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
        var endpointValue = Normalize(options?.Endpoint ?? stored?.Endpoint);
        var allowPrivateHttp = options?.AllowPrivateHttp ?? stored?.AllowPrivateHttp ?? false;
        var endpointValid = endpointValue is null;
        string? canonicalEndpoint = null;
        if (endpointValue is not null)
        {
            try
            {
                canonicalEndpoint = NetclawEndpointNormalizer.Normalize(endpointValue, "Endpoint", allowPrivateHttp);
                endpointValid = canonicalEndpoint is not null;
            }
            catch (ArgumentException)
            {
                endpointValid = false;
            }
        }
        var endpoint = canonicalEndpoint ?? endpointValue;
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
        var idleMinutes = options?.IdleMinutes ?? stored?.IdleMinutes ?? 15;
        var connectionCapacity = options?.ConnectionCapacity ?? stored?.ConnectionCapacity ?? 25;
        var turnInactivityTimeout = options?.TurnInactivityTimeout ?? TimeSpan.FromSeconds(stored?.TurnInactivityTimeoutSeconds ?? 300);
        var activityHeartbeatInterval = options?.ActivityHeartbeatInterval ?? TimeSpan.FromSeconds(stored?.ActivityHeartbeatIntervalSeconds ?? 15);
        var hasDeviceToken = !string.IsNullOrWhiteSpace(options?.DeviceToken) || !string.IsNullOrWhiteSpace(stored?.ProtectedDeviceToken);
        var resolvedSecretAvailable = !resolveSecret || (!secretUnavailable && !string.IsNullOrWhiteSpace(token));
        var runtimeConfigurationValid = endpointValid && endpoint is not null && instance == "dev" &&
            idleMinutes > 0 && connectionCapacity > 0 &&
            AiAssistantChatOptions.AreSessionLimitsStorageCompatible(turnInactivityTimeout, activityHeartbeatInterval) &&
            hasDeviceToken && resolvedSecretAvailable;
        var runtimeEnabled = configuredEnabled && runtimeSupported && runtimeConfigurationValid;
        var runtimeIssue = configuredEnabled && !runtimeSupported
            ? "Native Netclaw chat requires PostgreSQL durable session ownership; SQLite webhook/history fallback remains available."
            : configuredEnabled && !runtimeConfigurationValid
                ? "The saved Netclaw profile is incomplete or invalid; review its endpoint, paired-device token, and session limits."
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
            AllowPrivateHttp = allowPrivateHttp,
            IdleMinutes = idleMinutes,
            ConnectionCapacity = connectionCapacity,
            TurnInactivityTimeout = turnInactivityTimeout,
            ActivityHeartbeatInterval = activityHeartbeatInterval,
            UpdatedAtUtc = stored?.UpdatedAtUtc,
            LastAppliedAtUtc = stored?.LastAppliedAtUtc,
            LastTestedAtUtc = stored?.LastTestedAtUtc,
            LastTestSucceeded = stored?.LastTestSucceeded,
            Revision = revisionOverride ?? stored?.Revision ?? 0,
            Source = source,
            ManagedByDeployment = managedByDeployment,
            HasDeviceToken = hasDeviceToken,
            SecretUnavailable = secretUnavailable,
            SecretState = secretUnavailable ? "unavailable" :
                !string.IsNullOrWhiteSpace(options?.DeviceToken) ? "available" :
                !string.IsNullOrWhiteSpace(stored?.ProtectedDeviceToken) ? (resolveSecret ? "available" : "configured") : "not-configured",
            SourceKey = managedByDeployment ? IntegrationConfigurationAliases.GetDeploymentSourceKey(configuration, netclaw: true) : "database",
            ProfileFingerprint = profileFingerprintOverride ?? stored?.ProfileFingerprint ??
                (endpointValid ? BuildNetclawSecretBindingFingerprint(instance, endpoint) : string.Empty),
            CanAdoptLegacySessions = false
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
            ClientId = resolved.ClientId,
            PeerInstanceId = resolved.ServiceLink?.PeerInstanceId,
            PeerTenantId = resolved.ServiceLink?.PeerTenantId,
            LocalTenantId = resolved.ServiceLink?.LocalTenantId,
            LinkId = resolved.ServiceLink?.LinkId,
            LinkRevision = resolved.ServiceLink?.LinkRevision ?? 0,
            CredentialRevision = resolved.ServiceLink?.CredentialRevision ?? 0,
            ManagedSenderEnabled = stored?.ManagedSenderEnabled ?? true
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
            DaemonAddress = NetclawEndpointNormalizer.TryGetDaemonAddress(resolved.Endpoint),
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
        string conflictMessage,
        string conflictCode = "configuration_conflict")
    {
        if (expectedRevision is null)
            throw new IntegrationProviderConfigurationConflictException(
                "ExpectedRevision is required; reload the provider settings before saving or testing a draft.",
                conflictCode);
        if (expectedRevision.Value != currentRevision)
            throw new IntegrationProviderConfigurationConflictException(conflictMessage, conflictCode);
    }

    private static string BuildOrchestratorSecretBindingFingerprint(
        string? baseUrl,
        string? authority,
        string? tokenEndpoint,
        string? audience,
        string? scope,
        string? clientId,
        ServiceLinkOrchestratorBinding? binding = null)
    {
        var endpointFingerprint = IntegrationProviderSecretBinding.Fingerprint(
            "Orchestrator",
            baseUrl,
            authority ?? baseUrl,
            tokenEndpoint ?? BuildTokenEndpoint(authority ?? baseUrl),
            audience,
            scope,
            clientId);
        return binding is null ? endpointFingerprint : IntegrationProviderSecretBinding.Fingerprint(
            "RatelDesk.Orchestrator.ServiceLink.v1", endpointFingerprint,
            binding.LocalTenantId, binding.PeerTenantId, binding.PeerInstanceId, binding.LinkId,
            binding.LinkRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.CredentialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.GrantHash, binding.DirectionId, binding.SourceInstanceId, binding.SourceNamespaceId);
    }

    private static ServiceLinkOrchestratorBinding? ServiceLinkBinding(M2MConnectivitySettings stored)
        => stored.LinkId is null ? null : new(stored.LocalTenantId ?? string.Empty, stored.PeerTenantId ?? string.Empty,
            stored.PeerInstanceId ?? string.Empty, stored.LinkId, stored.LinkRevision, stored.CredentialRevision,
            stored.GrantHash ?? string.Empty, stored.DirectionId ?? string.Empty, stored.SourceInstanceId, stored.SourceNamespaceId);

    private static void ApplyServiceLinkBinding(M2MConnectivitySettings stored, ServiceLinkOrchestratorBinding? binding)
    {
        stored.LocalTenantId = binding?.LocalTenantId;
        stored.PeerTenantId = binding?.PeerTenantId;
        stored.PeerInstanceId = binding?.PeerInstanceId;
        stored.LinkId = binding?.LinkId;
        stored.LinkRevision = binding?.LinkRevision ?? 0;
        stored.CredentialRevision = binding?.CredentialRevision ?? 0;
        stored.GrantHash = binding?.GrantHash;
        stored.DirectionId = binding?.DirectionId;
        stored.SourceInstanceId = binding?.SourceInstanceId;
        stored.SourceNamespaceId = binding?.SourceNamespaceId;
    }

    private static void ValidateServiceLinkBinding(ServiceLinkOrchestratorBinding binding)
    {
        if (new[] { binding.LocalTenantId, binding.PeerTenantId, binding.PeerInstanceId, binding.LinkId }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Any(char.IsControl)) ||
            binding.LinkRevision < 1 || binding.CredentialRevision < 1 || binding.GrantHash.Length != 64 ||
            binding.GrantHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            binding.DirectionId is not ("initiator_to_responder" or "responder_to_initiator") ||
            new[] { binding.SourceInstanceId, binding.SourceNamespaceId }.Any(x => x is not null && !Guid.TryParseExact(x, "D", out _)))
            throw new ArgumentException("The outbound credential requires complete approved tenant, peer, link, grant and direction bindings.", nameof(binding));
    }

    private static string OrchestratorSecretPurpose(Guid profileId, string? fingerprint) => $"Orchestrator:{profileId:N}:{fingerprint}";

    private string UnprotectOrchestratorSecret(M2MConnectivitySettings stored)
    {
        var expected = BuildOrchestratorSecretBindingFingerprint(stored.RemoteBaseUrl, stored.RemoteAuthority,
            stored.RemoteTokenEndpoint, stored.RemoteAudience ?? DefaultAudience,
            stored.RemoteScope ?? stored.RemoteAudience ?? DefaultScope, stored.ClientId, ServiceLinkBinding(stored));
        if (!string.IsNullOrWhiteSpace(stored.SecretBindingFingerprint) && stored.SecretBindingFingerprint != expected)
            throw new IntegrationProviderSecretUnavailableException("The saved secret does not belong to the current provider identity.", new System.Security.Cryptography.CryptographicException());
        return secrets.Unprotect(stored.ProtectedClientSecret ?? string.Empty, OrchestratorSecretPurpose(stored.Id, expected));
    }

    private static string BuildNetclawSecretBindingFingerprint(string? instance, string? endpoint)
    {
        var canonicalEndpoint = NetclawEndpointNormalizer.Normalize(endpoint, "Endpoint", allowPrivateHttp: true);
        return IntegrationProviderSecretBinding.Fingerprint("Netclaw", instance, canonicalEndpoint);
    }

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
        {
            try
            {
                existingFingerprint = BuildNetclawSecretBindingFingerprint(existing.Instance, existing.Endpoint);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        return string.Equals(existingFingerprint, candidateFingerprint, StringComparison.Ordinal);
    }

    private static void ValidateNetclawSessionLimits(
        int idleMinutes,
        int connectionCapacity,
        TimeSpan turnInactivityTimeout,
        TimeSpan activityHeartbeatInterval)
    {
        if (idleMinutes <= 0 || connectionCapacity <= 0 ||
            !AiAssistantChatOptions.AreSessionLimitsStorageCompatible(turnInactivityTimeout, activityHeartbeatInterval))
            throw new ArgumentException(
                "Session limits must be positive, use whole seconds, fit the saved range, and keep the activity heartbeat shorter than turn inactivity.",
                "SessionLimits");
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
