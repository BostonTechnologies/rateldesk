using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_linked_client_revoke_is_denied_without_mutation_or_loss_of_approved_authority_across_restart(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await ActivateInitiatorAsync(local, peer);
        var token = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        using (var accepted = await CapabilitiesAsync(local, peer, token))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        Guid principalId;
        await using (var scope = local.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync();
            Assert.Equal(peer.LinkId, principal.LinkId);
            Assert.Equal("active", principal.Status);
            principalId = principal.Id;
        }
        var before = await LinkedRevokeStateAsync(local);

        for (var replica = 0; replica < 2; replica++)
        {
            if (replica == 1) await local.RestartAsync();
            using (var denied = await local.AdminAsync(HttpMethod.Post,
                       "/api/v1/admin/service-clients/" + principalId.ToString("D") + "/revoke"))
            {
                Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
                Assert.True(denied.Headers.CacheControl?.NoStore);
                var problem = await denied.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("service-client-conflict", problem.GetProperty("code").GetString());
                Assert.Equal("Reciprocal clients must be unlinked through the coordinated link workflow.",
                    problem.GetProperty("error").GetString());
            }
            Assert.Equal(before, await LinkedRevokeStateAsync(local));

            // A denied manual command must not disable an independently approved
            // live grant or silently start a coordinated revocation journal.
            using (var stillAccepted = await CapabilitiesAsync(local, peer, token))
                Assert.Equal(HttpStatusCode.OK, stillAccepted.StatusCode);
            var status = await StatusAsync(local, start.AttemptId);
            Assert.Equal("active", status.LifecycleState);
            Assert.Equal("commit", status.Decision);
            Assert.True(status.LocalInboundActive);
            Assert.True(status.LocalBusinessSenderEnabled);
            Assert.True(status.PeerActiveAcknowledged);
            Assert.Null(status.LastErrorCode);
            Assert.Equal(before, await LinkedRevokeStateAsync(local));
        }
    }

    private static async Task<string> LinkedRevokeStateAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        // Compare opaque hashes so a failure cannot format protected journal
        // payloads, credential verifiers or stored secret material.
        return ServiceLinkCanonicalJson.HashObject(new
        {
            Attempts = await db.Set<ServiceLinkAttempt>().AsNoTracking().OrderBy(row => row.AttemptId).ToArrayAsync(),
            Principals = await db.Set<ServicePrincipalRegistration>().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Credentials = await db.Set<ServicePrincipalSecret>().AsNoTracking().OrderBy(row => row.ServicePrincipalId)
                .ThenBy(row => row.CredentialRevision).ToArrayAsync(),
            Operations = await db.Set<ServiceLinkOperation>().AsNoTracking().OrderBy(row => row.LinkId)
                .ThenBy(row => row.OperationId).ToArrayAsync(),
            Rotations = await db.Set<ServiceLinkRotation>().AsNoTracking().OrderBy(row => row.RotationId).ToArrayAsync(),
            Sources = await db.IncidentReceiverSources.AsNoTracking().OrderBy(row => row.SourceNamespaceId).ToArrayAsync(),
            Bindings = await db.IncidentReceiverPrincipalBindings.AsNoTracking().OrderBy(row => row.SourceNamespaceId)
                .ThenBy(row => row.PrincipalKind).ThenBy(row => row.PrincipalId).ToArrayAsync(),
            Provider = await scope.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>()
                .GetOrchestratorSettingsAsync()
        });
    }
}
