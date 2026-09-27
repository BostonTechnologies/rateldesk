using Helpdesk.Application.Orchestration;
using System.Diagnostics;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class OrchestrationConnectivityService(
    IOptions<OrchestrationM2MOptions> options,
    IOptions<M2MClientOptions> m2mClientOptions,
    IOrchestrationTokenService orchestrationTokenService,
    IOrchestrationInternalClient orchestrationClient,
    IIntegrationProviderSettingsService? providerSettings = null,
    IOrchestrationProtectedDiagnosticsClient? protectedDiagnostics = null) : IOrchestrationConnectivityService
{
    private const string DefaultOrchestrationAudience = "netratel.api";

    private readonly OrchestrationM2MOptions _options = options.Value;
    private readonly IOrchestrationTokenService _orchestrationTokenService = orchestrationTokenService;
    private readonly IOrchestrationInternalClient _orchestrationClient = orchestrationClient;
    private readonly IIntegrationProviderSettingsService? _providerSettings = providerSettings;
    private readonly IOrchestrationProtectedDiagnosticsClient? _protectedDiagnostics = protectedDiagnostics;

    public async Task<OrchestrationConnectivitySettingsDto> GetOrchestrationSettingsAsync(CancellationToken cancellationToken = default)
        => _providerSettings is not null
            ? await _providerSettings.GetOrchestratorSettingsAsync(cancellationToken)
            : ToDto(ToResolvedSettings());

    public async Task<OrchestrationConnectivityTestResultDto> TestOrchestrationConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetResolvedOrchestrationSettingsAsync(cancellationToken);
        return await TestOrchestrationConnectivityAsync(settings, cancellationToken);
    }

    public async Task<OrchestrationConnectivityTestResultDto> TestOrchestrationConnectivityAsync(
        OrchestrationResolvedSettings settings,
        CancellationToken cancellationToken = default,
        bool useTokenCache = true)
    {
        if (!settings.Enabled)
        {
            return new OrchestrationConnectivityTestResultDto
            {
                Success = false,
                Message = "External orchestration connectivity is disabled. Enable the provider explicitly before testing it.",
                Probes =
                [
                    CreateProbe(
                        "AcquireToken",
                        "-",
                        OrchestrationConnectivityTrafficLight.Amber,
                        "Connectivity is disabled.",
                        settings.RemoteSystemName),
                    CreateProbe(
                        "RemoteHealth",
                        "(disabled)",
                        OrchestrationConnectivityTrafficLight.Amber,
                        "Connectivity is disabled.",
                        settings.RemoteSystemName)
                ]
            };
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return new OrchestrationConnectivityTestResultDto
            {
                Success = false,
                Message = "BaseUrl is not configured.",
                Probes =
                [
                    CreateProbe(
                        "AcquireToken",
                        "-",
                        OrchestrationConnectivityTrafficLight.Amber,
                        "The provider base URL is missing.",
                        settings.RemoteSystemName),
                    CreateProbe(
                        "RemoteHealth",
                        "(disabled)",
                        OrchestrationConnectivityTrafficLight.Amber,
                        "The provider base URL is missing.",
                        settings.RemoteSystemName)
                ]
            };
        }

        var probes = new List<OrchestrationConnectivityProbeResultDto>();

        try
        {
            var tokenStopwatch = Stopwatch.StartNew();
            await _orchestrationTokenService.GetAccessTokenAsync(settings, cancellationToken, useTokenCache);
            tokenStopwatch.Stop();

            probes.Add(new OrchestrationConnectivityProbeResultDto
            {
                ProbeName = "AcquireToken",
                Target = settings.TokenEndpoint ?? BuildTokenEndpoint(settings.Authority) ?? "-",
                Status = OrchestrationConnectivityTrafficLight.Green,
                LatencyMs = tokenStopwatch.ElapsedMilliseconds,
                Message = "Token acquired successfully.",
                RemoteSystemName = settings.RemoteSystemName,
                CheckedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            const string safeMessage = "Token acquisition failed. Check the configured provider credentials and endpoint.";
            probes.Add(CreateProbe(
                "AcquireToken",
                settings.TokenEndpoint ?? BuildTokenEndpoint(settings.Authority) ?? "-",
                OrchestrationConnectivityTrafficLight.Red,
                safeMessage,
                settings.RemoteSystemName));

            return new OrchestrationConnectivityTestResultDto
            {
                Success = false,
                Message = safeMessage,
                Probes = probes
            };
        }

        try
        {
            var result = await _orchestrationClient.HealthAsync(settings, cancellationToken, useTokenCache);
            probes.Add(new OrchestrationConnectivityProbeResultDto
            {
                ProbeName = "RemoteHealth",
                Target = BuildHealthEndpoint(settings),
                Status = result.Success
                    ? OrchestrationConnectivityTrafficLight.Green
                    : result.StatusCode is 401 or 403
                        ? OrchestrationConnectivityTrafficLight.Amber
                        : OrchestrationConnectivityTrafficLight.Red,
                HttpStatus = result.StatusCode,
                Message = result.Message,
                RemoteSystemName = settings.RemoteSystemName,
                CheckedAtUtc = DateTimeOffset.UtcNow
            });

            if (result.Success && _protectedDiagnostics is not null)
            {
                var identity = await _protectedDiagnostics.IdentityAsync(settings, cancellationToken, useTokenCache);
                probes.Add(new OrchestrationConnectivityProbeResultDto
                {
                    ProbeName = "ProtectedIdentity",
                    Target = BuildIdentityEndpoint(settings),
                    Status = identity.Success
                        ? OrchestrationConnectivityTrafficLight.Green
                        : identity.StatusCode is 401 or 403
                            ? OrchestrationConnectivityTrafficLight.Amber
                            : OrchestrationConnectivityTrafficLight.Red,
                    HttpStatus = identity.StatusCode,
                    Message = identity.Message,
                    RemoteSystemName = settings.RemoteSystemName,
                    CheckedAtUtc = DateTimeOffset.UtcNow
                });
                if (!identity.Success)
                {
                    return new OrchestrationConnectivityTestResultDto
                    {
                        Success = false,
                        StatusCode = identity.StatusCode,
                        Message = identity.Message,
                        Probes = probes
                    };
                }
            }

            return new OrchestrationConnectivityTestResultDto
            {
                Success = result.Success,
                StatusCode = result.StatusCode,
                Message = result.Message,
                Probes = probes
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            const string safeMessage = "Remote health probe failed. Check the provider endpoint and protected health contract.";
            probes.Add(CreateProbe(
                "RemoteHealth",
                BuildHealthEndpoint(settings),
                OrchestrationConnectivityTrafficLight.Red,
                safeMessage,
                settings.RemoteSystemName));

            return new OrchestrationConnectivityTestResultDto
            {
                Success = false,
                Message = safeMessage,
                Probes = probes
            };
        }
    }

    public async Task<OrchestrationResolvedSettings> GetResolvedOrchestrationSettingsAsync(CancellationToken cancellationToken = default)
        => _providerSettings is not null
            ? await _providerSettings.GetResolvedOrchestratorSettingsAsync(cancellationToken)
            : ToResolvedSettings();

    private OrchestrationResolvedSettings ToResolvedSettings()
    {
        // M2MClientOptions remains bound for inbound callback authentication;
        // it is deliberately never reused for outbound NetRatel calls.
        _ = m2mClientOptions.Value;
        var baseUrl = Normalize(_options.BaseUrl);
        var authority = Normalize(_options.Authority)
            ?? baseUrl;
        var audience = Normalize(_options.Audience)
            ?? DefaultOrchestrationAudience;
        var scope = Normalize(_options.Scope) ?? audience;
        var tokenEndpoint = Normalize(_options.TokenEndpoint)
            ?? BuildTokenEndpoint(authority);
        var enabled = _options.Enabled;

        return new OrchestrationResolvedSettings
        {
            Enabled = enabled,
            BaseUrl = baseUrl,
            Audience = audience,
            Authority = authority,
            TokenEndpoint = tokenEndpoint,
            Scope = scope,
            ClientId = Normalize(_options.ClientId),
            ClientSecret = Normalize(_options.ClientSecret),
            AllowPrivateHttp = _options.AllowPrivateHttp,
            RemoteSystemName = Normalize(_options.ProviderName) ?? "NetRatel orchestrator",
            HealthPath = NormalizePath(_options.HealthPath, "/internal/health"),
            IngestPath = NormalizePath(_options.IngestPath, "/internal/ingest"),
            CatalogPath = NormalizePath(_options.CatalogPath, "/internal/catalog"),
            Source = "deployment",
            ManagedByDeployment = true,
            HasClientSecret = !string.IsNullOrWhiteSpace(_options.ClientSecret)
        };
    }

    private static OrchestrationConnectivitySettingsDto ToDto(OrchestrationResolvedSettings settings)
    {
        return new OrchestrationConnectivitySettingsDto
        {
            Enabled = settings.Enabled,
            BaseUrl = settings.BaseUrl,
            Audience = settings.Audience,
            Scope = settings.Scope,
            Authority = settings.Authority,
            TokenEndpoint = settings.TokenEndpoint,
            RemoteSystemName = settings.RemoteSystemName,
            UpdatedAtUtc = settings.UpdatedAtUtc,
            ProviderKey = settings.ProviderKey,
            Revision = settings.Revision,
            Source = settings.Source,
            ManagedByDeployment = settings.ManagedByDeployment,
            HasClientSecret = settings.HasClientSecret,
            AllowPrivateHttp = settings.AllowPrivateHttp,
            SourceKey = settings.SourceKey,
            HealthPath = settings.HealthPath,
            IngestPath = settings.IngestPath,
            CatalogPath = settings.CatalogPath,
            ClientId = settings.ClientId
        };
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizePath(string? value, string fallback)
    {
        var path = Normalize(value) ?? fallback;
        return path.StartsWith('/') ? path : $"/{path}";
    }

    private static string? BuildTokenEndpoint(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return null;
        }

        return $"{authority.TrimEnd('/')}/connect/token";
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string BuildHealthEndpoint(OrchestrationResolvedSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return "(not set)";
        }

        return $"{settings.BaseUrl.TrimEnd('/')}{settings.HealthPath}";
    }

    private static string BuildIdentityEndpoint(OrchestrationResolvedSettings settings)
        => string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? "(not set)"
            : $"{settings.BaseUrl.TrimEnd('/')}/api/v1/system/m2m/ping";

    private static OrchestrationConnectivityProbeResultDto CreateProbe(
        string probeName,
        string target,
        OrchestrationConnectivityTrafficLight status,
        string message,
        string? remoteSystemName,
        int? httpStatus = null,
        long? latencyMs = null)
    {
        return new OrchestrationConnectivityProbeResultDto
        {
            ProbeName = probeName,
            Target = target,
            Status = status,
            HttpStatus = httpStatus,
            LatencyMs = latencyMs,
            Message = message,
            RemoteSystemName = remoteSystemName,
            CheckedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
