using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.ServiceLink;

/// <summary>Public bounded descriptors, proof-bound bootstrap, and separately authenticated link controls.</summary>
public static partial class ServiceLinkEndpoints
{
    public static IServiceCollection AddServiceLinkProtocol(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceLinkOptions>().Bind(configuration.GetSection(ServiceLinkOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ServiceLinkOptions>, ServiceLinkOptionsValidator>();
        services.AddScoped<ServiceLinkCoordinator>();
        services.AddScoped<ServiceLinkProtocolTokenCache>();
        services.AddHttpClient<ServiceLinkTransport>(client => client.Timeout = TimeSpan.FromSeconds(25))
            .ConfigurePrimaryHttpMessageHandler(provider => IntegrationSafeHttpMessageHandler.CreateServiceLink(
                currentAllowPrivateHttp: () => provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue.AllowPrivateHttp));
        foreach (var clientName in new[] { ServiceLinkOutboundNetwork.TokenClientName, ServiceLinkOutboundNetwork.BusinessClientName })
            services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(provider => IntegrationSafeHttpMessageHandler.CreateServiceLink(
                    currentAllowPrivateHttp: () => provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue.AllowPrivateHttp));
        services.AddHostedService<ServiceLinkWorker>();
        return services;
    }

    public static void MapServiceLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ServiceLinkContract.MetadataPath, (HttpContext http, ServiceLinkCoordinator links) =>
            Respond(http, () => Task.FromResult(links.Metadata()))).AllowAnonymous().WithTags("Reciprocal Service Links");
        var peer = app.MapGroup(ServiceLinkContract.EndpointPath).WithTags("Reciprocal Service Links");
        peer.MapGet("/requests/{attemptId}", (string attemptId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            Respond(http, () => links.PublicDescriptorAsync(attemptId, ct))).AllowAnonymous()
            .RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);
        peer.MapPost("/attempts/{attemptId}/review", (string attemptId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            WithBody<ServiceLinkReviewRequest, ServiceLinkReviewResponse>(http, body => links.ReviewAsync(attemptId, body, ct), ct))
            .AllowAnonymous().DisableAntiforgery().RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);
        peer.MapPost("/attempts/{attemptId}/exchange", (string attemptId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            WithBody<ServiceLinkExchangeRequest, ServiceLinkExchangeResponse>(http, body => links.ExchangeAsync(attemptId, body, ct), ct))
            .AllowAnonymous().DisableAntiforgery().RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);
        peer.MapGet("/links/{linkId}/status", (string linkId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            Respond(http, () => links.StatusAsync(linkId, http.User, ct)))
            .RequireAuthorization(ServiceIdentityServiceCollectionExtensions.ControlPolicy);
        foreach (var operation in new[] { "verify", "ack", "commit", "abort", "revoke", "rotate" })
        {
            var kind = operation;
            peer.MapPost("/links/{linkId}/" + kind, (string linkId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
                WithBody<ServiceLinkLifecycleRequest, object>(http, body => links.LifecycleAsync(linkId, kind, body, http.User, ct), ct, kind))
                .DisableAntiforgery()
                .RequireAuthorization(kind == "verify" ? ServiceIdentityServiceCollectionExtensions.VerifyPolicy : ServiceIdentityServiceCollectionExtensions.ControlPolicy)
                .RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);
        }

        // The normal enabled-account session boundary is followed by current organization
        // administrator checks in every coordinator command. Account API/MCP/service tokens cannot enter.
        var admin = app.MapGroup("/api/v1/admin/service-links").WithTags("Reciprocal Service Links")
            .RequireAuthorization(IntegrationCredentialEndpoints.CredentialManagementPolicy)
            .RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);
        admin.MapGet("/", (HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            Respond(http, () => links.AdminListAsync(http.User, ct)));
        admin.MapGet("/attempts/{attemptId}", (string attemptId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            Respond(http, () => links.AdminStatusAsync(attemptId, http.User, ct)));
        admin.MapPost("/start", (HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            WithBody<ServiceLinkStartRequest, ServiceLinkNavigation>(http, body => links.StartAsync(body, http.User, ct), ct));
        admin.MapPost("/attempts/{attemptId}/continue", (string attemptId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            WithBody<ServiceLinkContinueRequest, ServiceLinkNavigation>(http, body => links.ContinueAsync(attemptId, body, http.User, ct), ct))
            .Accepts<ServiceLinkContinueRequest>("application/json");
        admin.MapPost("/remote-review", (HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            WithBody<ServiceLinkRemoteReviewRequest, ServiceLinkRequestDescriptor>(http, body => links.RemoteReviewAsync(body, http.User, ct), ct));
        admin.MapPost("/remote-approve", (HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            WithBody<ServiceLinkRemoteApproveRequest, ServiceLinkNavigation>(http, body => links.RemoteApproveAsync(body, http.User, ct), ct));
        admin.MapPost("/callback", (HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            WithBody<ServiceLinkCallbackRequest, ServiceLinkReviewResponse>(http, body => links.CallbackAsync(body, http.User, ct), ct));
        admin.MapPost("/attempts/{attemptId}/approve", (string attemptId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            WithBody<ServiceLinkLocalApproveRequest, ServiceLinkAdminStatus>(http, body => links.LocalApproveAsync(attemptId, body, http.User, ct), ct));
        admin.MapPost("/links/{linkId}/test", (string linkId, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
            Respond(http, () => links.TestAsync(linkId, http.User, ct)));
        foreach (var operation in new[] { "resume", "cancel", "revoke", "rotate" })
        {
            var kind = operation;
            foreach (var identifierKind in new[] { "links", "attempts" })
                admin.MapPost("/" + identifierKind + "/{identifier}/" + kind, (string identifier, HttpContext http, ServiceLinkCoordinator links, CancellationToken ct) =>
                    WithBody<ServiceLinkAdminAction, ServiceLinkAdminStatus>(http, body => links.AdminActionAsync(identifier, kind, body, http.User, ct), ct));
        }
    }

    private static Task<IResult> WithBody<TRequest, TResponse>(HttpContext http, Func<TRequest, Task<TResponse>> action, CancellationToken ct, string? lifecycleKind = null) =>
        Respond(http, async () =>
        {
            var maximum = http.RequestServices.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue.MaximumPayloadBytes;
            if (!http.Request.HasJsonContentType() || http.Request.ContentLength > maximum)
                throw new ServiceLinkProtocolException(400, "invalid-request", "A bounded JSON protocol body is required.");
            using var memory = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, ct)) != 0)
            {
                if (memory.Length + count > maximum) throw new ServiceLinkProtocolException(400, "invalid-request", "The protocol body exceeds its configured limit.");
                memory.Write(buffer, 0, count);
            }
            var json = new UTF8Encoding(false, true).GetString(memory.ToArray());
            var body = ServiceLinkCanonicalJson.Deserialize<TRequest>(json);
            if (body is null) throw new ServiceLinkProtocolException(400, "invalid-request", "The protocol body is required.");
            if (lifecycleKind is not null && body is ServiceLinkLifecycleRequest lifecycle)
            {
                using var wire = JsonDocument.Parse(json);
                var fields = wire.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                body = (TRequest)(object)(lifecycle with { WirePropertyNames = fields });
            }
            return await action(body);
        });

    internal static async Task<IResult> Respond<T>(HttpContext http, Func<Task<T>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        try
        {
            if (http.RequestServices.GetService<IServicePublicSettingsResolver>() is not null)
                await http.RequestServices.GetRequiredService<ServiceLinkCoordinator>().RefreshSettingsAsync(http.RequestAborted);
            return Results.Json(await action());
        }
        catch (ServiceLinkProtocolException error)
        { return await FailureAsync(http, error.StatusCode, error.Code); }
        catch (Exception error) when (ServiceLinkFailureReporting.IsNetworkPolicyFailure(error))
        { return await FailureAsync(http, 422, "network-policy-rejected"); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException or FormatException)
        { return await FailureAsync(http, 400, "invalid-request"); }
        catch (Exception error) when (error is DbUpdateException or ServiceClientConflictException or IntegrationProviderConfigurationConflictException || ServiceLinkDatabaseConflict.IsAbortedTransaction(error))
        { return await FailureAsync(http, 409, "service-link-conflict"); }
        catch (CryptographicException)
        { return await FailureAsync(http, 503, "protected-state-unavailable"); }
        catch (HttpRequestException)
        { return await FailureAsync(http, 502, "peer-unavailable"); }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        { return await FailureAsync(http, 504, "peer-timeout"); }
    }
}
