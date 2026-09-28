using System.Diagnostics;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Connectivity;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Mvc;
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
                return Results.Conflict(new { code = ex.Code, message = ex.Message });
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

        group.MapPost("/pair-and-save", async (
            PairNetclawDeviceDto request,
            IIntegrationProviderSettingsService settings,
            [FromServices] INetclawPairingService pairing,
            IAiAssistantChatClientFactory clients,
            HelpdeskDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.PairingCode) || request.PairingCode.Length > 1024)
                return Results.BadRequest(new { code = "invalid_pairing_code", message = "Enter a valid one-time pairing code." });

            var settingsDraft = ToNetclawSettingsDraft(request, deviceToken: null);
            var tokenReceived = false;
            var settingsSaved = false;
            var legacyOwnershipConfirmed = !string.IsNullOrWhiteSpace(request.LegacyOwnershipReviewToken);
            try
            {
                var target = await settings.ResolveNetclawPairingTargetAsync(
                    settingsDraft,
                    Actor(principal),
                    request.LegacyOwnershipReviewToken,
                    ct);
                var deviceToken = await pairing.ExchangeCodeAsync(target, request.PairingCode.Trim(), ct);
                tokenReceived = true;
                var pairedProfile = await settings.UpdateNetclawSettingsAsync(
                    ToNetclawSettingsDraft(request, deviceToken),
                    ct);
                settingsSaved = true;

                var savedSnapshot = await settings.GetResolvedNetclawSettingsAsync(ct);
                if (!IsSameNetclawProfile(savedSnapshot, pairedProfile))
                    return Results.Conflict(new
                    {
                        code = "pairing_saved_runtime_conflict",
                        message = "Pairing succeeded and was saved, but the saved connection changed before verification. Refresh and review the saved profile before requesting another code."
                    });

                var verification = await VerifyAuthenticatedNetclawSessionAsync(
                    clients,
                    AiAssistantChatRuntimeSnapshot.From(savedSnapshot),
                    ct);
                var recorded = await settings.RecordNetclawTestAsync(
                    pairedProfile.Revision,
                    pairedProfile.ProfileFingerprint,
                    verification.Success,
                    ct);
                if (!recorded)
                    return Results.Conflict(new
                    {
                        code = "pairing_verification_superseded",
                        message = "Pairing succeeded, but the saved connection changed during verification. Refresh and inspect the current profile before requesting another code."
                    });

                var result = await settings.GetNetclawSettingsAsync(ct);
                if (!IsSameNetclawProfile(result, pairedProfile))
                    return Results.Conflict(new
                    {
                        code = "pairing_verification_superseded",
                        message = "Pairing succeeded, but the saved connection changed during verification. Refresh and inspect the current profile before requesting another code."
                    });

                if (!verification.Success)
                {
                    if (!legacyOwnershipConfirmed)
                        AddPairingAudit(db, principal, pairedProfile, verified: false);
                    await db.SaveChangesAsync(ct);
                    return Results.Json(new
                    {
                        code = "pairing_saved_verification_failed",
                        message = "Pairing succeeded and was saved, but authenticated SignalR verification failed. Review the saved connection before requesting another code."
                    }, statusCode: StatusCodes.Status502BadGateway);
                }

                if (result.LastTestSucceeded != true)
                    return Results.Conflict(new
                    {
                        code = "pairing_verification_superseded",
                        message = "Pairing succeeded, but a newer connection check replaced the verification result. Refresh and inspect the current profile."
                    });

                if (!legacyOwnershipConfirmed)
                    AddPairingAudit(db, principal, pairedProfile, verified: true);
                await db.SaveChangesAsync(ct);
                return Results.Ok(result);
            }
            catch (NetclawPairingException ex)
            {
                return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: ex.StatusCode);
            }
            catch (NetclawLegacySessionReviewRequiredException ex)
            {
                return Results.Conflict(ex.Review);
            }
            catch (NetclawLegacySessionReviewConflictException ex)
            {
                var review = ex.Review;
                return Results.Conflict(new NetclawLegacySessionReviewConflictDto(
                    ex.Code,
                    ex.Message,
                    review.CanonicalEndpoint,
                    NetclawEndpointNormalizer.ToDaemonAddress(review.CanonicalEndpoint),
                    review.ConversationCount,
                    review.ReviewToken));
            }
            catch (IntegrationProviderConfigurationConflictException ex) when (!tokenReceived)
            {
                return Results.Conflict(new { code = ex.Code, message = PairingPreflightConflictMessage(ex.Code) });
            }
            catch (IntegrationProviderConfigurationConflictException ex)
            {
                if (ex.Code == "runtime_revision_conflict")
                {
                    return Results.Conflict(new
                    {
                        code = "pairing_saved_runtime_conflict",
                        message = "Pairing succeeded and protected settings were saved, but a newer runtime revision prevented application. Refresh and review the saved profile before requesting another code."
                    });
                }

                if (settingsSaved)
                    return Results.Conflict(new
                    {
                        code = "pairing_saved_verification_uncertain",
                        message = "Pairing succeeded and was saved, but authenticated SignalR verification could not be confirmed. Review the saved connection before requesting another code."
                    });

                return Results.Conflict(new
                {
                    code = "pairing_code_consumed",
                    message = "Netclaw accepted the one-time code, but RatelDesk could not complete the settings save. Resolve the conflict, then request a fresh code."
                });
            }
            catch (NetclawPairingRuntimeUnavailableException ex) when (!tokenReceived)
            {
                return Results.Json(new { code = "netclaw_runtime_unavailable", message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (ArgumentException ex) when (!tokenReceived && string.Equals(ex.ParamName, "SessionLimits", StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    code = "invalid_session_limits",
                    message = "Session limits must use positive whole seconds and keep the activity heartbeat shorter than turn inactivity."
                });
            }
            catch (ArgumentException) when (!tokenReceived && IsPrivateHttpDenied(request.Endpoint))
            {
                return Results.BadRequest(new
                {
                    code = "private_http_not_allowed",
                    message = "Private HTTP pairing requires a private IP address. Use HTTPS for DNS names and public addresses."
                });
            }
            catch (ArgumentException ex) when (!tokenReceived)
            {
                return Results.BadRequest(new { code = "invalid_pairing_draft", message = ex.Message });
            }
            catch (InvalidOperationException ex) when (!tokenReceived)
            {
                return Results.BadRequest(new { code = "pairing_not_available", message = ex.Message });
            }
            catch (Exception) when (tokenReceived && settingsSaved)
            {
                return Results.Json(new
                {
                    code = "pairing_saved_verification_uncertain",
                    message = "Pairing succeeded and was saved, but authenticated SignalR verification could not be confirmed. Review the saved connection before requesting another code."
                }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception) when (tokenReceived)
            {
                return Results.Json(new
                {
                    code = "pairing_outcome_uncertain",
                    message = "Netclaw accepted the one-time code, but RatelDesk could not confirm the save. Refresh the saved profile before requesting another code."
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapGet("/legacy-sessions/unbound", async (
            IIntegrationProviderSettingsService settings,
            CancellationToken ct) => Results.Ok(await settings.GetUnboundNetclawLegacySessionsAsync(ct)));

        group.MapPost("/legacy-sessions/confirm-owner", async (
            ConfirmNetclawLegacySessionsDto request,
            IIntegrationProviderSettingsService settings,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            try
            {
                var result = await settings.ConfirmNetclawLegacySessionsAsync(request, Actor(principal), ct);
                return Results.Ok(result);
            }
            catch (IntegrationProviderConfigurationConflictException ex)
            {
                return Results.Conflict(new { code = ex.Code, message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_legacy_provider", message = ex.Message });
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
                var recorded = await settings.RecordNetclawTestAsync(
                    testedProfile.Revision,
                    testedProfile.ProfileFingerprint,
                    result.Success,
                    ct);
                var superseded = !testedProfile.ManagedByDeployment && testedProfile.Revision > 0 && !recorded;
                db.ActivityLogs.Add(new ActivityLog
                {
                    UserId = Actor(principal),
                    RelatedEntityId = "Netclaw",
                    Message = $"Integration provider connectivity test completed. Provider=Netclaw; Success={result.Success}; Superseded={superseded}; StatusCode={result.StatusCode?.ToString() ?? "none"}."
                });
                await db.SaveChangesAsync(ct);
                if (superseded)
                    return Results.Conflict(new { code = "diagnostic_superseded", message = "The provider configuration changed while this test was running. Reload and test the current settings." });
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
            NetclawConnectivityTestResultDto result;
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
                result = new NetclawConnectivityTestResultDto
                {
                    Success = true,
                    Message = "Authenticated Netclaw SignalR session negotiation succeeded.",
                    SessionProtocol = "EnsureSession",
                    Probes = [Probe("AuthenticatedSignalR", true, 200, "Authenticated session protocol accepted.", stopwatch.ElapsedMilliseconds)]
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw session negotiation timed out.",
                    Probes = [Probe("AuthenticatedSignalR", false, 408, "The authenticated session negotiation timed out.", stopwatch.ElapsedMilliseconds)]
                };
            }
            catch (Exception)
            {
                result = new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw rejected the authenticated session negotiation.",
                    Probes = [Probe("AuthenticatedSignalR", false, null, "The authenticated session negotiation failed.", stopwatch.ElapsedMilliseconds)]
                };
            }
            finally
            {
                if (entered) DiagnosticGate.Release();
            }
            return await CompleteAsync(result);
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
                return Results.Conflict(new { code = ex.Code, message = ex.Message });
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

    private static async Task<NetclawConnectivityTestResultDto> VerifyAuthenticatedNetclawSessionAsync(
        IAiAssistantChatClientFactory clients,
        AiAssistantChatRuntimeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var entered = false;
        try
        {
            await DiagnosticGate.WaitAsync(deadline.Token);
            entered = true;
            await using var client = CreateClient(clients, snapshot);
            var ensured = await client.ConnectAsync(null, _ => Task.CompletedTask, deadline.Token);
            if (ensured is null || string.IsNullOrWhiteSpace(ensured.SessionId))
            {
                stopwatch.Stop();
                return new NetclawConnectivityTestResultDto
                {
                    Success = false,
                    Message = "Netclaw did not confirm an authenticated session.",
                    Probes = [Probe("AuthenticatedSignalR", false, null, "The authenticated session protocol returned no session confirmation.", stopwatch.ElapsedMilliseconds)]
                };
            }

            stopwatch.Stop();
            return new NetclawConnectivityTestResultDto
            {
                Success = true,
                Message = "Authenticated Netclaw SignalR session negotiation succeeded.",
                SessionProtocol = "EnsureSession",
                Probes = [Probe("AuthenticatedSignalR", true, 200, "Authenticated session protocol accepted.", stopwatch.ElapsedMilliseconds)]
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new NetclawConnectivityTestResultDto
            {
                Success = false,
                Message = "Netclaw session negotiation timed out.",
                Probes = [Probe("AuthenticatedSignalR", false, 408, "The authenticated session negotiation timed out.", stopwatch.ElapsedMilliseconds)]
            };
        }
        catch (Exception)
        {
            return new NetclawConnectivityTestResultDto
            {
                Success = false,
                Message = "Netclaw rejected the authenticated session negotiation.",
                Probes = [Probe("AuthenticatedSignalR", false, null, "The authenticated session negotiation failed.", stopwatch.ElapsedMilliseconds)]
            };
        }
        finally
        {
            if (entered)
                DiagnosticGate.Release();
        }
    }

    private static bool IsSameNetclawProfile(NetclawResolvedSettings snapshot, NetclawConnectivitySettingsDto profile)
        => snapshot.Revision == profile.Revision &&
           string.Equals(snapshot.ProfileFingerprint, profile.ProfileFingerprint, StringComparison.Ordinal);

    private static bool IsSameNetclawProfile(NetclawConnectivitySettingsDto current, NetclawConnectivitySettingsDto paired)
        => current.Revision == paired.Revision &&
           string.Equals(current.ProfileFingerprint, paired.ProfileFingerprint, StringComparison.Ordinal);

    private static void AddPairingAudit(
        HelpdeskDbContext db,
        ClaimsPrincipal principal,
        NetclawConnectivitySettingsDto profile,
        bool verified)
    {
        db.ActivityLogs.Add(new ActivityLog
        {
            UserId = Actor(principal),
            RelatedEntityId = profile.ProviderKey,
            Message = $"Integration provider pair-and-connect completed. Provider=Netclaw; Revision={profile.Revision}; Verified={verified}; SecretConfigured={profile.HasDeviceToken}."
        });
    }

    private static string Actor(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue("sub")
        ?? principal.Identity?.Name
        ?? "unknown";

    private static UpdateNetclawConnectivitySettingsDto ToNetclawSettingsDraft(
        PairNetclawDeviceDto request,
        string? deviceToken)
        => new()
        {
            ExpectedRevision = request.ExpectedRevision,
            Enabled = true,
            Instance = "dev",
            Endpoint = request.Endpoint,
            DeviceToken = deviceToken,
            ClearDeviceToken = false,
            AllowPrivateHttp = NetclawEndpointNormalizer.IsPrivateHttpLiteral(request.Endpoint),
            IdleMinutes = request.IdleMinutes,
            ConnectionCapacity = request.ConnectionCapacity,
            TurnInactivityTimeout = request.TurnInactivityTimeout,
            ActivityHeartbeatInterval = request.ActivityHeartbeatInterval
        };

    private static string PairingPreflightConflictMessage(string code) => code switch
    {
        "configuration_revision_conflict" => "The saved Netclaw revision changed before pairing. Keep the draft and refresh before trying again.",
        "unbound_legacy_sessions" => "Confirm historical ownership for unbound legacy sessions before pairing. No pairing code was sent to Netclaw.",
        _ => "Netclaw pairing conflicted with the current settings. No pairing code was sent to Netclaw."
    };

    private static bool IsPrivateHttpDenied(string? endpoint)
        => Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttp &&
           !NetclawEndpointNormalizer.IsPrivateHttpLiteral(uri);

    private static IAiAssistantChatClient CreateClient(
        IAiAssistantChatClientFactory clients,
        AiAssistantChatRuntimeSnapshot snapshot)
        => clients is IAiAssistantChatRuntimeClientFactory snapshotFactory
            ? snapshotFactory.Create(snapshot)
            : clients.Create();
}
