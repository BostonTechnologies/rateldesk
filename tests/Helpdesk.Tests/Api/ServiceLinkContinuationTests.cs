using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continue_returns_only_the_original_pinned_navigation_without_new_state_or_authority(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await StartAsync(local, peer);
        var before = await ContinuationStateAsync(local);
        for (var retry = 0; retry < 2; retry++)
        {
            using var response = await ContinueResponseAsync(local, start.AttemptId, SessionBinding);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var continued = await response.Content.ReadFromJsonAsync<ServiceLinkNavigation>();
            Assert.NotNull(continued);
            Assert.Equal(start.AttemptId, continued.AttemptId);
            Assert.Equal("awaiting_approval", continued.LifecycleState);
            Assert.True(continued.NavigationUrl == start.NavigationUrl, "Continue must return exactly the retained pinned navigation.");
            Assert.Equal(before, await ContinuationStateAsync(local));
        }

        using (var foreignSession = await ContinueResponseAsync(local, start.AttemptId, new string('x', 64)))
            await AssertContinuationDeniedAsync(foreignSession, HttpStatusCode.Forbidden, start);
        using (var missingSession = await ContinueResponseAsync(local, start.AttemptId, ""))
            await AssertContinuationDeniedAsync(missingSession, HttpStatusCode.Forbidden, start);

        await using (var scope = local.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.Users.Add(new ApplicationUser { Id = "other-continuation-administrator", UserName = "other-continuation-administrator",
                Email = "other-continuation@example.test", IsEnabled = true, IsInstanceAdministrator = true });
            await identity.SaveChangesAsync();
            var otherActor = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "other-continuation-administrator"),
                new Claim(ClaimTypes.Role, "HelpdeskAdmin"), new Claim("auth_mode", "local")], "LifecycleAdmin"));
            Assert.True((await scope.ServiceProvider.GetRequiredService<ICurrentUserAccessService>().ResolveAsync(otherActor)).IsHelpdeskAdmin);
            var denied = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() =>
                scope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().ContinueAsync(start.AttemptId,
                    new ServiceLinkContinueRequest(SessionBinding), otherActor, CancellationToken.None));
            Assert.Equal(403, denied.StatusCode);
            Assert.True(!denied.Message.Contains(BrowserState(start), StringComparison.Ordinal), "Another actor must not receive the retained correlation.");
        }
        Assert.Equal(before, await ContinuationStateAsync(local));

        var settings = local.Services.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>().CurrentValue;
        var originalAudience = settings.Audience;
        try
        {
            settings.Audience = originalAudience + ".changed";
            using var drifted = await ContinueResponseAsync(local, start.AttemptId, SessionBinding);
            await AssertContinuationDeniedAsync(drifted, HttpStatusCode.Conflict, start);
        }
        finally { settings.Audience = originalAudience; }
        Assert.Equal(before, await ContinuationStateAsync(local));
        Assert.Equal(0, before.Principals);
        Assert.Equal(0, before.Secrets);
        Assert.Equal(0, before.Bindings);
        Assert.Equal(0, before.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continue_never_reopens_an_expired_approval_or_reveals_its_browser_state(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await StartAsync(local, peer);
        var before = await ContinuationStateAsync(local);
        local.Clock.Advance(TimeSpan.FromSeconds(121));
        using var response = await ContinueResponseAsync(local, start.AttemptId, SessionBinding);
        await AssertContinuationDeniedAsync(response, HttpStatusCode.Forbidden, start);
        Assert.Equal(before, await ContinuationStateAsync(local));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continue_never_navigates_a_responder_attempt(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath)
            ?? throw new InvalidOperationException("Missing metadata.");
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var reviewed = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var before = await ContinuationStateAsync(local);
        using var response = await ContinueResponseAsync(local, descriptor.AttemptId, SessionBinding);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(!text.Contains(peer.BrowserState, StringComparison.Ordinal), "A responder rejection must not reveal its retained correlation.");
        Assert.Equal(before, await ContinuationStateAsync(local));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continue_cannot_repeat_reviewed_or_cancelled_consent(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var reviewed = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var before = await ContinuationStateAsync(local);
        using (var response = await ContinueResponseAsync(local, start.AttemptId, SessionBinding))
            await AssertContinuationDeniedAsync(response, HttpStatusCode.Forbidden, start);
        Assert.Equal(before, await ContinuationStateAsync(local));
        using (var cancelled = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/cancel",
                   new ServiceLinkAdminAction("continuation-test-cancel")))
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var tombstone = await ContinuationStateAsync(local);
        using (var response = await ContinueResponseAsync(local, start.AttemptId, SessionBinding))
            await AssertContinuationDeniedAsync(response, HttpStatusCode.Forbidden, start);
        Assert.Equal(tombstone, await ContinuationStateAsync(local));
    }

    private static Task<HttpResponseMessage> ContinueResponseAsync(ServiceLinkKestrelPeer local, string attemptId, string binding) =>
        local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + attemptId + "/continue", new ServiceLinkContinueRequest(binding));

    private static async Task AssertContinuationDeniedAsync(HttpResponseMessage response, HttpStatusCode expected, ServiceLinkNavigation start)
    {
        Assert.Equal(expected, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(!text.Contains(BrowserState(start), StringComparison.Ordinal), "A rejected continuation must not disclose the retained browser state.");
        Assert.True(!text.Contains("navigationUrl", StringComparison.OrdinalIgnoreCase), "A rejected continuation must not return navigation.");
    }

    private sealed record ContinuationState(string Attempts, string Sources, int Principals, int Secrets, int Bindings, int Operations);
    private static async Task<ContinuationState> ContinuationStateAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        return new(ServiceLinkCanonicalJson.HashObject(await db.Set<ServiceLinkAttempt>().AsNoTracking().OrderBy(x => x.AttemptId).ToArrayAsync()),
            ServiceLinkCanonicalJson.HashObject(await db.IncidentReceiverSources.AsNoTracking().OrderBy(x => x.SourceNamespaceId).ToArrayAsync()),
            await db.Set<ServicePrincipalRegistration>().CountAsync(), await db.Set<ServicePrincipalSecret>().CountAsync(),
            await db.IncidentReceiverPrincipalBindings.CountAsync(), await db.Set<ServiceLinkOperation>().CountAsync());
    }
}
