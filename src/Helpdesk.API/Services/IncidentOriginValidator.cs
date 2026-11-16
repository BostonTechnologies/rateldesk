using System.Security.Claims;
using System.Text.Json;
using AutomationTrace;
using AutomationTrace.Contracts;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Pairing;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services;

public sealed record IncidentOriginValidation(Request? Request = null, IResult? Error = null);
public sealed class IncidentOriginValidator(HelpdeskDbContext db, IServicePrincipalRegistry services, IServicePublicSettingsResolver settings)
{
    public async Task<IncidentOriginValidation> ValidateAsync(ClaimsPrincipal principal, AuthorizedIncidentSource approved,
        AutomationIncidentContext context, CancellationToken ct)
    {
        var service = await services.ResolvePrincipalAsync(principal, ServiceIdentityScopes.IncidentCreate, ct);
        if (service?.MappingId is not { } mappingId) return Fail(403, "presentation-peer-unavailable");
        var connection = await db.Set<SystemConnection>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == mappingId && x.State == "connected", ct);
        var pair = connection is null ? null : await db.Set<SystemPair>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == connection.PairId && x.State == "paired", ct);
        if (pair is null) return Fail(403, "presentation-peer-unavailable");
        var peer = SystemPairingService.Read<PairingMetadata>(pair.PeerJson);
        var publicWeb = peer.WebOrigin.TrimEnd('/');
        var allowed = new List<string> { publicWeb + "/flows?tenantId=" + service.PeerTenantId + "&run=" + context.FlowRunId };
        if (context.NativeRequestId is not null) allowed.Add(publicWeb + "/requests?search=" + context.NativeRequestId);
        var jobRun = context.JobExecution?.RunId ?? (context.SourceKind != "script" ? context.ExecutionId : null);
        var jobDefinition = context.JobExecution?.DefinitionId ?? (context.SourceKind != "script" ? context.Definition?.Id : null);
        if (jobDefinition is not null && jobRun is not null)
            allowed.Add(publicWeb + "/jobs/" + Uri.EscapeDataString(jobDefinition) + "/runs/" + Uri.EscapeDataString(jobRun));
        if (context.Links.Any(link => !allowed.Contains(link.Url, StringComparer.Ordinal))) return Fail(403, "presentation-link-rejected");
        if (context.Origin is not { } origin) return new();
        var local = (await settings.ResolveAsync(ct)).Identity;
        if (origin.SourceInstanceId != local.InstanceId || origin.Organization.Id != approved.Organization.Id)
            return Fail(403, "origin-context-rejected");
        var request = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == origin.Request.Id && x.OrganizationId == approved.Organization.Id, ct);
        var task = await db.RequestTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == origin.Task.Id && x.RequestId == origin.Request.Id && x.OrganizationId == approved.Organization.Id, ct);
        if (request is null || task is null) return Fail(422, "origin-unavailable");
        if (task.OrchestrationPeerInstanceId != service.PeerInstanceId || task.OrchestrationLinkId != mappingId.ToString("D"))
            return Fail(403, "origin-peer-rejected");
        var snapshot = task.AutomationOriginJson;
        var nativeRequest = task.OrchestrationExternalRequestId;
        var nativeRun = task.OrchestrationExternalRunId ?? task.OrchestratorExecutionId;
        var definition = task.OrchestrationJobDefinitionId ?? task.OrchestrationRequestDefinitionId;
        if (task.AutomationAttemptCorrelationId != origin.AttemptId)
        {
            snapshot = null;
            try
            {
                using var history = JsonDocument.Parse(task.AutomationAttemptHistoryJson ?? "[]");
                foreach (var attempt in history.RootElement.EnumerateArray())
                {
                    if (Get(attempt, "AutomationAttemptCorrelationId") != origin.AttemptId) continue;
                    snapshot = Get(attempt, "AutomationOriginJson"); nativeRequest = Get(attempt, "OrchestrationExternalRequestId");
                    nativeRun = Get(attempt, "OrchestrationExternalRunId") ?? Get(attempt, "OrchestratorExecutionId");
                    definition = Get(attempt, "OrchestrationJobDefinitionId") ?? Get(attempt, "OrchestrationRequestDefinitionId");
                    if (Get(attempt, "OrchestrationPeerInstanceId") != service.PeerInstanceId || Get(attempt, "OrchestrationLinkId") != mappingId.ToString("D"))
                        return Fail(403, "origin-peer-rejected");
                    break;
                }
            }
            catch (JsonException) { return Fail(422, "origin-history-unavailable"); }
        }
        if (snapshot is null || snapshot != JsonSerializer.Serialize(origin, AutomationTraceValidation.Json)) return Fail(403, "origin-attempt-rejected");
        // The pre-dispatch snapshot proves the source attempt. The accepted acknowledgement/callback proves the exact native run.
        // A report racing that acknowledgement defers without an incident, false relation or invented execution binding.
        if (nativeRequest is null || nativeRun is null) return Fail(409, "origin-admission-pending");
        if (nativeRequest != context.NativeRequestId || nativeRun != jobRun || definition != jobDefinition)
            return Fail(403, "origin-execution-rejected");
        return new(request);
    }
    private static string? Get(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static IncidentOriginValidation Fail(int status, string code) => new(Error: IncidentReceiverContract.Problem(status, code));
}
