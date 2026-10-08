using System.Net;
using System.Net.Http.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Fact]
    public async Task Incident_only_pairing_activates_receiver_without_orchestration_profile_or_reverse_resources()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        using var started = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start",
            new ServiceLinkStartRequest(peer.BaseUrl, local.OrganizationId, null, [], SessionBinding)
            {
                LocalCustomerIds = [local.CustomerId],
                InboundScopes = [ServiceIdentityScopes.IncidentCreate, ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.IncidentTargets],
                OutboundScopes = [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope]
            });
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        var start = await started.Content.ReadFromJsonAsync<ServiceLinkNavigation>() ?? throw new InvalidOperationException("Missing start result.");
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var reviewed = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        using (var approved = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve",
            new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding))) Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var status = await StatusAsync(local, start.AttemptId);
        for (var step = 0; step < 12 && status.LifecycleState != "active"; step++)
        {
            using var resumed = await ResumeAsync(local, peer.LinkId);
            Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
            status = await resumed.Content.ReadFromJsonAsync<ServiceLinkAdminStatus>() ?? throw new InvalidOperationException("Missing resume status.");
        }
        Assert.Equal("active", status.LifecycleState);
        Assert.True(status.LocalInboundActive);
        Assert.True(status.PeerActiveAcknowledged);
        Assert.False(status.LocalBusinessSenderEnabled);
        Assert.Null(status.LastErrorCode);
        var reverse = status.GrantSummary!.Grants.Single(g => g.TargetProduct == "netratel");
        Assert.True(ServiceLinkValidation.IncidentOnlyGrant(reverse));
        Assert.Empty(reverse.ResourceConstraints.ResourceIds);
        Assert.Empty(reverse.ResourceConstraints.RequestDefinitionIds);
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Empty(await db.M2MConnectivitySettings.ToListAsync());
        Assert.Single(await db.Set<ServicePrincipalRegistration>().ToListAsync());
        Assert.Single(await db.IncidentReceiverPrincipalBindings.ToListAsync());
        using var denied = await TokenResponseAsync(peer.OutboundCredential, ServiceIdentityScopes.Callback);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using var allowed = await TokenResponseAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var operationCount = await db.Set<ServiceLinkOperation>().CountAsync();
        using var tested = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/links/" + peer.LinkId + "/test", new { });
        Assert.True(tested.IsSuccessStatusCode);
        var observation = await tested.Content.ReadFromJsonAsync<ServiceLinkTestResult>();
        Assert.True(observation!.OutboundAuthenticated);
        Assert.True(observation.PeerAcknowledgedInbound);
        // This historical contract fixture exposes no candidate Flow readiness observation; do not fabricate it.
        Assert.False(observation.IncidentDeliveryReady);
        Assert.Equal("connector-readiness-required", observation.ErrorCode);
        Assert.Equal(operationCount, await db.Set<ServiceLinkOperation>().CountAsync());
        Assert.Empty(await db.Incidents.AsNoTracking().ToListAsync());
        using var duplicateResume = await ResumeAsync(local, peer.LinkId);
        Assert.True(duplicateResume.IsSuccessStatusCode);
        Assert.Single(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync());
        Assert.Empty(await db.M2MConnectivitySettings.AsNoTracking().ToListAsync());
    }
}
