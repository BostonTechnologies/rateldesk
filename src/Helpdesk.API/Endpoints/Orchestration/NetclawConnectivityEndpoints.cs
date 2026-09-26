using System.Diagnostics;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Orchestration;

public static class NetclawConnectivityEndpoints
{
    private static readonly SemaphoreSlim DiagnosticGate = new(4, 4);

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
            var testedProfile = await settings.GetResolvedNetclawSettingsAsync(ct);
            async Task<IResult> CompleteAsync(NetclawConnectivityTestResultDto result)
            {
                await settings.RecordNetclawTestAsync(
                    testedProfile.Revision,
                    testedProfile.ProfileFingerprint,
                    result.Success,
                    ct);
                db.ActivityLogs.Add(new ActivityLog
                {
                    UserId = Actor(principal),
                    RelatedEntityId = "Netclaw",
                    Message = $"Integration provider connectivity test completed. Provider=Netclaw; Success={result.Success}; StatusCode={result.StatusCode?.ToString() ?? "none"}."
                });
                await db.SaveChangesAsync(ct);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            }

            if (!testedProfile.ConfiguredEnabled)
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw is disabled.",
                    Probes = [Probe("Configuration", false, null, "Enable Netclaw before testing.")]
                });
            }

            if (!testedProfile.RuntimeSupported)
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = testedProfile.RuntimeIssue ?? "Native Netclaw chat is unavailable for this database provider.",
                    Probes = [Probe("RuntimeCapability", false, null, testedProfile.RuntimeIssue ?? "Native chat is unavailable.")]
                });
            }

            if (!testedProfile.Enabled || string.IsNullOrWhiteSpace(testedProfile.Endpoint) || string.IsNullOrWhiteSpace(testedProfile.DeviceToken))
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw configuration is incomplete.",
                    Probes = [Probe("Configuration", false, null, "A valid endpoint and paired-device token are required.")]
                });
            }

            var stopwatch = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var entered = false;
            try
            {
                await DiagnosticGate.WaitAsync(deadline.Token);
                entered = true;
                await using var client = CreateClient(clients, AiAssistantChatRuntimeSnapshot.From(testedProfile));
                // EnsureSession is the smallest authenticated production
                // protocol operation. No prompt is sent and the connection is
                // disposed before the diagnostic request completes.
                _ = await client.ConnectAsync(null, _ => Task.CompletedTask, deadline.Token);
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
            catch (Exception)
            {
                return await CompleteAsync(new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw rejected the authenticated session negotiation.",
                    Probes = [Probe("AuthenticatedSignalR", false, null, "The authenticated session negotiation failed.", stopwatch.ElapsedMilliseconds)]
                });
            }
            finally
            {
                if (entered) DiagnosticGate.Release();
            }
        });

        group.MapPost("/test-draft", async (
            UpdateNetclawConnectivitySettingsDto request,
            IIntegrationProviderSettingsService settings,
            IAiAssistantChatClientFactory clients,
            CancellationToken ct) =>
        {
            try
            {
                var draft = await settings.ResolveNetclawDraftAsync(request, ct);
                if (!draft.ConfiguredEnabled)
                {
                    return Results.BadRequest(new NetclawConnectivityTestResultDto
                    {
                        Success = false,
                        Message = "Netclaw is disabled in this draft.",
                        Probes = [Probe("Configuration", false, null, "Enable Netclaw before testing the draft.")]
                    });
                }

                if (!draft.RuntimeSupported)
                {
                    return Results.BadRequest(new NetclawConnectivityTestResultDto
                    {
                        Success = false,
                        Message = draft.RuntimeIssue ?? "Native Netclaw chat is unavailable for this database provider.",
                        Probes = [Probe("RuntimeCapability", false, null, draft.RuntimeIssue ?? "Native chat is unavailable.")]
                    });
                }

                if (!draft.Enabled || string.IsNullOrWhiteSpace(draft.Endpoint) || string.IsNullOrWhiteSpace(draft.DeviceToken))
                {
                    return Results.BadRequest(new NetclawConnectivityTestResultDto
                    {
                        Success = false,
                        Message = "The Netclaw draft is incomplete.",
                        Probes = [Probe("Configuration", false, null, "A valid endpoint and paired-device token are required.")]
                    });
                }

                var stopwatch = Stopwatch.StartNew();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var entered = false;
                try
                {
                    await DiagnosticGate.WaitAsync(deadline.Token);
                    entered = true;
                    await using var client = CreateClient(clients, AiAssistantChatRuntimeSnapshot.From(draft));
                    _ = await client.ConnectAsync(null, _ => Task.CompletedTask, deadline.Token);
                    stopwatch.Stop();
                    return Results.Ok(new NetclawConnectivityTestResultDto
                    {
                        Success = true,
                        Message = "Authenticated Netclaw draft session negotiation succeeded; no settings were saved or applied.",
                        SessionProtocol = "EnsureSession",
                        Probes = [Probe("AuthenticatedSignalR", true, 200, "Authenticated draft session protocol accepted.", stopwatch.ElapsedMilliseconds)]
                    });
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.BadRequest(new NetclawConnectivityTestResultDto
                    {
                        Success = false,
                        Message = "Netclaw draft session negotiation timed out.",
                        Probes = [Probe("AuthenticatedSignalR", false, 408, "The authenticated draft session negotiation timed out.", stopwatch.ElapsedMilliseconds)]
                    });
                }
                catch (Exception)
                {
                    return Results.BadRequest(new NetclawConnectivityTestResultDto
                    {
                        Success = false,
                        Message = "Netclaw rejected the authenticated draft session negotiation.",
                        Probes = [Probe("AuthenticatedSignalR", false, null, "The authenticated draft session negotiation failed.", stopwatch.ElapsedMilliseconds)]
                    });
                }
                finally
                {
                    if (entered) DiagnosticGate.Release();
                }
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
                return Results.BadRequest(new { code = "draft_not_testable", message = ex.Message });
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

    private static IAiAssistantChatClient CreateClient(
        IAiAssistantChatClientFactory clients,
        AiAssistantChatRuntimeSnapshot snapshot)
        => clients is IAiAssistantChatRuntimeClientFactory snapshotFactory
            ? snapshotFactory.Create(snapshot)
            : clients.Create();
}
