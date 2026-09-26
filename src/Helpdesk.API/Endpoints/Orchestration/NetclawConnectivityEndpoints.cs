using System.Diagnostics;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Orchestration;

public static class NetclawConnectivityEndpoints
{
    public static void MapNetclawConnectivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/netclaw")
            .WithTags("Netclaw")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async (IIntegrationProviderSettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.GetNetclawSettingsAsync(ct)));

        group.MapPut("/", async (
            UpdateNetclawConnectivitySettingsDto request,
            IIntegrationProviderSettingsService settings,
            HelpdeskDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            try
            {
                var result = await settings.UpdateNetclawSettingsAsync(request, ct);
                db.ActivityLogs.Add(new ActivityLog
                {
                    UserId = Actor(principal),
                    RelatedEntityId = result.ProviderKey,
                    Message = $"Integration provider settings updated. Provider={result.ProviderKey}; Revision={result.Revision}; Source={result.Source}; Enabled={result.Enabled}; SecretConfigured={result.HasDeviceToken}; SecretAction={(request.ClearDeviceToken ? "cleared" : !string.IsNullOrWhiteSpace(request.DeviceToken) ? "replaced" : "unchanged")}."
                });
                await db.SaveChangesAsync(ct);
                return Results.Ok(result);
            }
            catch (IntegrationProviderConfigurationConflictException ex)
            {
                return Results.Conflict(new { code = "configuration_conflict", message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_configuration", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { code = "configuration_not_editable", message = ex.Message });
            }
        });

        group.MapPost("/test", async (
            IIntegrationProviderSettingsService settings,
            IAiAssistantChatClientFactory clients,
            HelpdeskDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            async Task<IResult> CompleteAsync(NetclawConnectivityTestResultDto result)
            {
                await settings.RecordNetclawTestAsync(result.Success, ct);
                db.ActivityLogs.Add(new ActivityLog
                {
                    UserId = Actor(principal),
                    RelatedEntityId = "Netclaw",
                    Message = $"Integration provider connectivity test completed. Provider=Netclaw; Success={result.Success}; StatusCode={result.StatusCode?.ToString() ?? "none"}."
                });
                await db.SaveChangesAsync(ct);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            }

            var resolved = await settings.GetResolvedNetclawSettingsAsync(ct);
            if (!resolved.ConfiguredEnabled)
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw is disabled.",
                    Probes = [Probe("Configuration", false, null, "Enable Netclaw before testing.")]
                });
            }

            if (!resolved.RuntimeSupported)
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = resolved.RuntimeIssue ?? "Native Netclaw chat is unavailable for this database provider.",
                    Probes = [Probe("RuntimeCapability", false, null, resolved.RuntimeIssue ?? "Native chat is unavailable.")]
                });
            }

            if (!resolved.Enabled || string.IsNullOrWhiteSpace(resolved.Endpoint) || string.IsNullOrWhiteSpace(resolved.DeviceToken))
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw configuration is incomplete.",
                    Probes = [Probe("Configuration", false, null, "A valid endpoint and paired-device token are required.")]
                });
            }

            var stopwatch = Stopwatch.StartNew();
            await using var client = clients.Create();
            try
            {
                // EnsureSession is the smallest authenticated production
                // protocol operation. No prompt is sent and the connection is
                // disposed before the diagnostic request completes.
                var session = await client.ConnectAsync(null, _ => Task.CompletedTask, ct);
                stopwatch.Stop();
                var result = new NetclawConnectivityTestResultDto
                {
                    Success = true,
                    Message = "Authenticated Netclaw SignalR session negotiation succeeded.",
                    SessionProtocol = "EnsureSession",
                    Probes = [Probe("AuthenticatedSignalR", true, 200, "Authenticated session protocol accepted.", stopwatch.ElapsedMilliseconds)]
                };
                return await CompleteAsync(result);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw session negotiation timed out.",
                    Probes = [Probe("AuthenticatedSignalR", false, 408, "The authenticated session negotiation timed out.", stopwatch.ElapsedMilliseconds)]
                });
            }
            catch (Exception exception)
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw rejected the authenticated session negotiation.",
                    Probes = [Probe("AuthenticatedSignalR", false, null, exception.GetType().Name, stopwatch.ElapsedMilliseconds)]
                });
            }
        });
    }

    private static NetclawConnectivityProbeDto Probe(
        string name,
        bool success,
        int? statusCode,
        string message,
        long? latencyMs = null)
        => new()
        {
            ProbeName = name,
            Success = success,
            StatusCode = statusCode,
            Message = latencyMs is null ? message : $"{message} ({latencyMs} ms)",
            CheckedAtUtc = DateTimeOffset.UtcNow
        };

    private static string Actor(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue("sub")
        ?? principal.Identity?.Name
        ?? "unknown";
}
