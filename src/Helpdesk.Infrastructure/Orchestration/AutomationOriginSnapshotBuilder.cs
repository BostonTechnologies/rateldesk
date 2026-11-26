using System.Text.Json;
using AutomationTrace.Contracts;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Orchestration;

/// <summary>Pre-dispatch snapshot. Input payloads cannot provide or replace any provenance field.</summary>
public sealed class AutomationOriginSnapshotBuilder(HelpdeskDbContext db, RatelDeskIdentityDbContext identity,
    IServicePublicSettingsResolver settings, IOrchestrationCatalogService catalogue, IRepository<RequestTask> tasks)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AutomationOriginContext?> PrepareAsync(RequestTask task, Request request, RequestForm form,
        string correlation, string payloadJson, CancellationToken ct)
    {
        if (task.AutomationAttemptCorrelationId != correlation)
            throw new InvalidOperationException("The dispatch attempt changed before origin preparation.");
        if (task.AutomationDispatchPayloadJson is not null)
            return task.AutomationOriginJson is null ? null : JsonSerializer.Deserialize<AutomationOriginContext>(task.AutomationOriginJson, Json)
                ?? throw new InvalidOperationException("The retained origin snapshot is invalid.");
        if (task.RequestId != request.Id || task.OrganizationId != request.OrganizationId)
            throw new InvalidOperationException("The dispatch origin does not match its parent request.");
        var capabilities = await catalogue.GetCapabilitiesAsync(task.OrganizationId,
            Guid.TryParse(task.OrchestrationLinkId, out var link) ? link : null, ct);
        AutomationOriginContext? origin = null;
        if (capabilities.OriginContextSchema == AutomationTraceContract.OriginVersion)
        {
            var current = (await settings.ResolveAsync(ct)).Identity;
            var organization = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == request.OrganizationId, ct);
            var service = string.IsNullOrWhiteSpace(form.ServiceId) ? null :
                await db.Services.AsNoTracking().SingleOrDefaultAsync(x => x.Id == form.ServiceId, ct);
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.CustomerId &&
                x.OrganizationId == request.OrganizationId, ct);
            var submitter = await SubmittedByAsync(request.SelfServiceSubmitterId, ct);
            var links = new List<AutomationLink>();
            if (Uri.TryCreate(current.WebBaseUrl, UriKind.Absolute, out var web) && web.Scheme == "https" &&
                web.UserInfo.Length == 0 && web.Query.Length == 0 && web.Fragment.Length == 0)
                links.Add(new("source-request", request.TrackingId,
                    web.AbsoluteUri.TrimEnd('/') + "/requests/" + Uri.EscapeDataString(request.Id)));
            origin = new(AutomationTraceContract.OriginVersion, current.InstanceId,
                new(organization.Id, Bound(organization.Name, 256)),
                new(request.Id, request.TrackingId, Bound(request.Title, 512), new DateTimeOffset(
                    DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc))),
                service is null ? null : new(service.Id, Bound(service.Name, 256)),
                new(form.Id, Bound(form.Title, 256)), new(task.Id, Bound(task.Name, 256)), correlation, correlation,
                submitter, customer is null ? null : new(customer.Id, Bound(customer.Name, 256)), links);
            task.AutomationOriginJson = JsonSerializer.Serialize(origin, Json);
        }
        task.AutomationDispatchPayloadJson = payloadJson;
        // Persist both negotiated context and original input bytes before the first HTTP dispatch.
        await tasks.UpdateAsync(task);
        return origin;
    }

    private async Task<AutomationPerson?> SubmittedByAsync(string? key, CancellationToken ct)
    {
        if (key?.StartsWith("account:", StringComparison.Ordinal) == true)
        {
            var user = await identity.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == key.Substring(8), ct);
            return user is null ? new(key, null) : new(key, Bound(user.DisplayName, 256));
        }
        if (key?.StartsWith("customer:", StringComparison.Ordinal) == true)
        {
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == key.Substring(9), ct);
            return customer is null ? new(key, null) : new(key, Bound(customer.Name, 256));
        }
        if (key?.StartsWith("oidc:", StringComparison.Ordinal) == true)
            return new(key, null);
        return null; // Missing legacy identity is never guessed from requester email or mapped integration customer.
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
