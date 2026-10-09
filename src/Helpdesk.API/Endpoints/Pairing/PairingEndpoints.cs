using System.Security.Claims;
using System.Threading.RateLimiting;
using Helpdesk.API.Authentication;
using Helpdesk.Application.Notifications;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.Pairing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Helpdesk.API.Endpoints.Pairing;

public static class PairingEndpoints
{
    private const string ExchangeRate = "PairingExchange";
    public static IServiceCollection AddSystemPairing(this IServiceCollection services)
    {
        services.AddScoped<PairingAuthority>(); services.AddScoped<PairingTransport>(); services.AddScoped<SystemPairingService>(); services.AddScoped<PairingConnectionResolver>();
        services.AddScoped<IPairingBusinessAuthority>(sp => sp.GetRequiredService<PairingConnectionResolver>());
        services.AddMemoryCache();
        services.AddHttpClient(PairingTransport.ClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => IntegrationSafeHttpMessageHandler.Create(allowPrivateHttp: true));
        services.AddHostedService<PairingCleanupWorker>();
        services.AddRateLimiter(o => o.AddPolicy(ExchangeRate, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
        return services;
    }
    public static void MapSystemPairing(this IEndpointRouteBuilder app)
    {
        var peer = app.MapGroup(PairingContract.Root).WithTags("System pairing").AllowAnonymous();
        peer.MapGet("/metadata", (HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.MetadataProofAsync(http.Request.Headers["X-Pairing-Nonce"].ToString(), ct))));
        peer.MapPost("/exchange", (HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.ExchangeAsync(await ReadAsync<PairingExchangeRequest>(http.Request, ct), ct))))
            .DisableAntiforgery().RequireRateLimiting(ExchangeRate);
        peer.MapGet("/directory", (HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.DirectoryAsync(await AuthAsync(http, service, ct), ct))));
        peer.MapPut("/mappings/{mappingId:guid}", (Guid mappingId, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.AcceptSaveAsync(await AuthAsync(http, service, ct), mappingId, await ReadAsync<PairingSaveRequest>(http.Request, ct), ct)))).DisableAntiforgery();
        peer.MapPost("/mappings/{mappingId:guid}/test", (Guid mappingId, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.AcceptTestAsync(await AuthAsync(http, service, ct), mappingId, ct)))).DisableAntiforgery();
        peer.MapDelete("/mappings/{mappingId:guid}", (Guid mappingId, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => { var pair = await AuthAsync(http, service, ct); await service.DeleteAsync(pair.Id, mappingId, null, false, ct, pair.Generation); return Results.NoContent(); })).DisableAntiforgery();
        peer.MapDelete("/pair", (HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => { var pair = await AuthAsync(http, service, ct); await service.DeleteAsync(pair.Id, null, null, false, ct, pair.Generation); return Results.NoContent(); })).DisableAntiforgery();
        var admin = app.MapGroup("/api/v1/admin/system-connections").WithTags("System connections").RequireAuthorization(ServiceIdentityServiceCollectionExtensions.ManagementPolicy);
        admin.MapGet("/", (HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.ListAsync(await OwnerAsync(http, ct), ct))));
        admin.MapPost("/code", (HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.GenerateAsync(await OwnerAsync(http, ct), ct)))).RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter);
        admin.MapPost("/pair", (PairingConnectRequest request, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.ConnectAsync(request, await OwnerAsync(http, ct), ct))));
        admin.MapGet("/{pairId}/directory", (string pairId, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.SetupDirectoryAsync(pairId, await OwnerAsync(http, ct), ct))));
        admin.MapPut("/{pairId}/mappings/{mappingId:guid}", (string pairId, Guid mappingId, PairingMapping request, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () =>
        { if (request.Id != mappingId.ToString("D")) throw new PairingFailure("wrong_mapping", "The form belongs to a different named connection."); return Results.Ok(await service.SaveAsync(pairId, request, await OwnerAsync(http, ct), ct)); }));
        admin.MapPost("/{pairId}/mappings/{mappingId:guid}/test", (string pairId, Guid mappingId, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => Results.Ok(await service.TestAsync(pairId, mappingId, await OwnerAsync(http, ct), ct))));
        admin.MapDelete("/{pairId}/mappings/{mappingId:guid}", (string pairId, Guid mappingId, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => { await service.DeleteAsync(pairId, mappingId, await OwnerAsync(http, ct), true, ct); return Results.NoContent(); }));
        admin.MapDelete("/{pairId}", (string pairId, HttpContext http, SystemPairingService service, CancellationToken ct) => RunAsync(http, async () => { await service.DeleteAsync(pairId, null, await OwnerAsync(http, ct), true, ct); return Results.NoContent(); }));
    }
    private static async Task<string> OwnerAsync(HttpContext http, CancellationToken ct) => (await http.RequestServices.GetRequiredService<IIntegrationCredentialOwnerResolver>().ResolveAsync(http.User, ct))?.UserId
        ?? throw new PairingFailure("human_administrator_required", "A signed-in administrator must perform this operation.", 403);
    private static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType() || request.ContentLength > 16384) throw new PairingFailure("invalid_request", "Send a bounded JSON pairing request.", 400);
        var buffer = new byte[16385]; var count = 0;
        while (count < buffer.Length) { var read = await request.Body.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) break; count += read; }
        if (count > 16384) throw new PairingFailure("request_too_large", "The pairing request exceeds the supported size.", 413);
        try { return global::System.Text.Json.JsonSerializer.Deserialize<T>(buffer.AsSpan(0, count), new global::System.Text.Json.JsonSerializerOptions(global::System.Text.Json.JsonSerializerDefaults.Web)) ?? throw new PairingFailure("invalid_request", "The pairing request is empty or incompatible."); }
        catch (global::System.Text.Json.JsonException) { throw new PairingFailure("invalid_request", "The pairing request must contain compatible JSON values."); }
    }
    private static Task<SystemPair> AuthAsync(HttpContext http, SystemPairingService service, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Pairing ", StringComparison.Ordinal) || http.Request.Headers[PairingContract.PeerHeader].Count != 1 || http.Request.Headers["X-Pairing-Caller"].Count != 1)
            throw new PairingFailure("pairing_authentication_required", "Current system pairing authentication is required.", 401);
        return service.AuthenticateAsync(http.Request.Headers[PairingContract.PeerHeader].ToString(), header[8..], http.Request.Headers["X-Pairing-Caller"].ToString(), ct);
    }
    private static async Task<IResult> RunAsync(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (PairingFailure failure) { return await FailureAsync(http, failure.Status, failure.Code, failure.Message); }
        catch (global::System.Data.Common.DbException) { return await FailureAsync(http, 409, "connection_changed", "Another operation changed this connection. Refresh and retry the same connection."); }
        catch (DbUpdateException) { return await FailureAsync(http, 409, "connection_changed", "Another operation changed this connection. Refresh and retry the same connection."); }
        catch (IntegrationProviderSecretUnavailableException) { return await FailureAsync(http, 503, "protected_secret_unavailable", "The protected connection credential cannot be read. Restore the shared Data Protection key ring or delete and pair again."); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or global::System.Text.Json.JsonException or global::System.Security.Cryptography.CryptographicException)
        { return await FailureAsync(http, 503, "connection_operation_failed", "The connection could not complete this operation. Review the saved form and use the reference in the scoped operational log."); }
    }
    private static async Task<IResult> FailureAsync(HttpContext http, int status, string code, string message)
    {
        var reference = Guid.NewGuid().ToString("N");
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SystemPairing").LogWarning("Connection operation failed. Code={Code} Reference={Reference}", code, reference);
        if (status >= 500 && http.Request.Path.StartsWithSegments("/api/v1/admin/system-connections") && !HttpMethods.IsGet(http.Request.Method))
        {
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
            var notificationKey = "pairing-notification:" + actor + ":" + code;
            if (actor is not null && !cache.TryGetValue(notificationKey, out _))
            {
                cache.Set(notificationKey, true, TimeSpan.FromMinutes(5));
                try
                {
                    await using var scope = http.RequestServices.CreateAsyncScope();
                    var notifications = scope.ServiceProvider.GetService<INotificationService>();
                    if (notifications is not null) await notifications.CreateNotificationAsync(new CreateNotificationRequest { UserId = actor, Title = "System connection needs attention", Message = message + " Reference: " + reference,
                        Source = "SystemPairing", Category = "SystemPairing", Severity = NotificationSeverity.Warning, Reference = code, CorrelationId = reference, Link = "/account/integration-credentials" }, http.RequestAborted);
                }
                catch (Exception) when (!http.RequestAborted.IsCancellationRequested) { }
            }
        }
        return Results.Json(new { code, message, reference }, statusCode: status);
    }
}

public sealed class PairingCleanupWorker(IServiceScopeFactory scopes, ILogger<PairingCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<SystemPairingService>().CleanupAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("System pairing cleanup did not complete. FailureType={FailureType}", ex.GetType().Name); }
        }
    }
}
