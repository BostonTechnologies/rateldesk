using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Infrastructure.ServiceIdentity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class OrchestrationTokenService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    TimeProvider? timeProvider = null,
    IServiceScopeFactory? scopes = null,
    IOptionsMonitor<ServiceLinkOptions>? currentLinkOptions = null) : IOrchestrationTokenService
{
    private const int MaximumResponseBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IMemoryCache _cache = cache;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<string> GetAccessTokenAsync(
        OrchestrationResolvedSettings settings,
        CancellationToken cancellationToken = default,
        bool useCache = true)
    {
        await RequireCurrentProviderAsync(settings, cancellationToken);
        var tokenEndpoint = settings.TokenEndpoint
            ?? (string.IsNullOrWhiteSpace(settings.Authority) ? null : $"{settings.Authority.TrimEnd('/')}/connect/token");
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            throw new InvalidOperationException("External orchestration token endpoint is not configured.");
        }

        if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var tokenUri))
            throw new InvalidOperationException("External orchestration token endpoint is not a valid absolute URL.");
        ServiceLinkOutboundNetwork.Validate(tokenUri, "TokenEndpoint", settings, currentLinkOptions, settings.ServiceLink is null ? null : await CurrentLinksAsync(cancellationToken));

        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            throw new InvalidOperationException("External orchestration client credentials are not configured.");
        }

        if (string.IsNullOrWhiteSpace(settings.Scope))
        {
            throw new InvalidOperationException("External orchestration scope is not configured.");
        }
        if (string.IsNullOrWhiteSpace(settings.Audience))
            throw new InvalidOperationException("External orchestration audience is not configured.");

        var cacheMaterial = string.Join("\n", tokenEndpoint, settings.Authority, settings.BaseUrl, settings.ClientId,
            settings.Scope, settings.Audience, settings.Source, settings.SourceKey, settings.ProfileFingerprint,
            settings.ServiceLink?.LocalTenantId, settings.ServiceLink?.PeerTenantId, settings.ServiceLink?.PeerInstanceId,
            settings.ServiceLink?.LinkId, settings.ServiceLink?.GrantHash, settings.ServiceLink?.DirectionId,
            settings.ServiceLink?.SourceInstanceId, settings.ServiceLink?.SourceNamespaceId,
            settings.ServiceLink?.LinkRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.ServiceLink?.CredentialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ClientSecret))));
        var cacheKey = $"orchestration_token::{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheMaterial)))}";
        if (useCache && _cache.TryGetValue<CachedAccessToken>(cacheKey, out var cachedToken) && cachedToken is not null)
        {
            if (cachedToken.ReuseUntilUtc > _timeProvider.GetUtcNow()) return cachedToken.Token;
            _cache.Remove(cacheKey);
        }

        var client = _httpClientFactory.CreateClient(settings.ServiceLink is null ? "OrchestrationToken" : ServiceLinkOutboundNetwork.TokenClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        ServiceLinkOutboundNetwork.PrepareRequest(request, settings, currentLinkOptions, settings.ServiceLink is null ? null : await CurrentLinksAsync(cancellationToken));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["scope"] = settings.Scope
        };

        request.Content = new FormUrlEncodedContent(form);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var requestStartedAt = _timeProvider.GetUtcNow();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The external orchestration token request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("The external orchestration token endpoint could not be reached.");
        }

        using (response)
        {
            string content;
            try
            {
                content = await ReadBoundedContentAsync(response.Content, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The external orchestration token request timed out.");
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token request failed ({(int)response.StatusCode}): {IntegrationErrorSafety.ProviderCode(content, settings.ClientSecret)}");

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(content);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Token response was not valid JSON.");
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("access_token", out var tokenElement))
                    throw new InvalidOperationException("Token response did not include access_token.");

                string? token;
                try
                {
                    token = tokenElement.GetString();
                }
                catch (InvalidOperationException)
                {
                    throw new InvalidOperationException("Token response returned an invalid access_token.");
                }

                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("Token response returned an empty access_token.");

                // Local unlink or a provider change can complete while token HTTP is awaiting its body.
                // Observe that committed authority before caching or releasing a business token.
                await RequireCurrentProviderAsync(settings, cancellationToken);

                var expiresInSeconds = doc.RootElement.TryGetProperty("expires_in", out var expiresElement) &&
                                       expiresElement.TryGetInt32(out var value) && value > 0
                    ? value
                    : 300;
                var safetySeconds = Math.Min(30, Math.Max(1, expiresInSeconds / 5));
                var reuseUntil = requestStartedAt.AddSeconds(expiresInSeconds - safetySeconds);
                var remaining = reuseUntil - _timeProvider.GetUtcNow();

                if (useCache && remaining > TimeSpan.Zero)
                {
                    _cache.Set(
                        cacheKey,
                        new CachedAccessToken(token, reuseUntil),
                        remaining);
                }

                return token;
            }
        }
    }

    private async Task<ServiceLinkOptions?> CurrentLinksAsync(CancellationToken cancellationToken)
    {
        if (scopes is null) return null;
        await using var scope = scopes.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetService<IServicePublicSettingsResolver>();
        return settings is null ? null : (await settings.ResolveAsync(cancellationToken)).Linking;
    }

    private async Task RequireCurrentProviderAsync(OrchestrationResolvedSettings settings, CancellationToken cancellationToken)
    {
        if (settings.ServiceLink is not null && !settings.Enabled)
            throw new InvalidOperationException("The service-link business sender is disabled.");
        if (settings.Source == "database" && (settings.Revision > 0 || settings.ServiceLink is not null))
        {
            if (scopes is null) throw new InvalidOperationException("Durable provider authority is unavailable.");
            await using var scope = scopes.CreateAsyncScope();
            var current = await scope.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>()
                .GetResolvedOrchestratorSettingsAsync(cancellationToken);
            if (!current.Enabled || current.SecretUnavailable || current.Revision != settings.Revision ||
                current.ProfileFingerprint != settings.ProfileFingerprint || current.ServiceLink != settings.ServiceLink)
                throw new InvalidOperationException("The provider changed or was disabled. Resolve its current settings before dispatching.");
        }
    }

    private sealed record CachedAccessToken(string Token, DateTimeOffset ReuseUntilUtc);

    private static async Task<string> ReadBoundedContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("The external orchestration token response exceeded the maximum supported size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

}
