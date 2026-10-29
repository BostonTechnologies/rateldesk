using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed partial class IntegrationCredentialSqliteTests
{
    [Fact]
    public async Task Reviewed_setup_http_delivers_matching_policy_retries_and_acknowledges_once()
    {
        await using var harness = await Harness.CreateAsync();
        var request = SetupRequest();
        using var options = await harness.Client.GetAsync("/api/v1/integration-credentials/setup-options");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        using var response = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/setup", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var first = (await response.Content.ReadFromJsonAsync<IntegrationCredentialSetupDelivery>())!;
        Assert.Equal(request.RequestId, first.RequestId);
        Assert.Equal(request.Credential.ExpiresAtUtc, first.Credential.ExpiresAtUtc);
        using var retry = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/setup", request);
        var second = (await retry.Content.ReadFromJsonAsync<IntegrationCredentialSetupDelivery>())!;
        Assert.True(first.Credential.Secret == second.Credential.Secret, "HTTP retry must preserve the issued credential.");
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(1, await identity.IntegrationCredentials.CountAsync());
            Assert.Equal(first.Credential.Id, (await db.Set<McpAccessPolicy>().SingleAsync()).CredentialId);
            Assert.Equal(1, await db.Set<McpAccessPolicyAudit>().CountAsync());
        }
        using var ack = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/setup/{request.RequestId}/acknowledge", new { });
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        using var ackAgain = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/setup/{request.RequestId}/acknowledge", new { });
        Assert.Equal(HttpStatusCode.NoContent, ackAgain.StatusCode);
        using var replayAfterAck = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/setup", request);
        Assert.Equal(HttpStatusCode.Conflict, replayAfterAck.StatusCode);
        Assert.Contains("setup_acknowledged", await replayAfterAck.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unacknowledged_setup_expiry_is_enforced_by_actual_credential_authentication_before_cleanup()
    {
        await using var harness = await Harness.CreateAsync();
        var request = SetupRequest();
        using var response = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/setup", request);
        var first = (await response.Content.ReadFromJsonAsync<IntegrationCredentialSetupDelivery>())!;
        using var device = harness.DeviceClient();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Credential.Secret);
        using var before = await device.GetAsync("/api/v1/mcp-read-only-check");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            await identity.IntegrationCredentialSetupAttempts.Where(x => x.RequestId == request.RequestId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.DeliveryExpiresAtUnixMilliseconds, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds()));
            Assert.Null((await identity.IntegrationCredentials.SingleAsync()).RevokedAtUtc);
        }
        using var denied = await device.GetAsync("/api/v1/mcp-read-only-check");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var acknowledgement = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/setup/{request.RequestId}/acknowledge", new { });
        Assert.Equal(HttpStatusCode.Gone, acknowledgement.StatusCode);
        Assert.True(await harness.IsRevokedAsync(first.Credential.Id));
    }

    [Fact]
    public async Task Setup_requires_explicit_review_valid_scope_and_interactive_owner()
    {
        await using var harness = await Harness.CreateAsync();
        var request = SetupRequest();
        using var unapproved = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/setup", request with { Approved = false });
        Assert.Equal(HttpStatusCode.BadRequest, unapproved.StatusCode);
        using var invalid = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/setup",
            request with { Policies = [request.Policies[0] with { OrganizationId = "org-b" }] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidExpiry = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/setup",
            request with { Credential = request.Credential with { ExpiresAtUtc = request.Credential.ExpiresAtUtc!.Value.AddTicks(1) } });
        Assert.Equal(HttpStatusCode.BadRequest, invalidExpiry.StatusCode);
        harness.Client.DefaultRequestHeaders.Add("X-Test-Auth-Mode", "integration");
        using var machine = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/setup", request);
        Assert.Equal(HttpStatusCode.Forbidden, machine.StatusCode);
        await using var scope = harness.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>().IntegrationCredentials.ToListAsync());
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<McpAccessPolicy>().ToListAsync());
    }

    private static IntegrationCredentialSetupRequest SetupRequest()
    {
        var expiry = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(1).Ticks / 10 * 10, TimeSpan.Zero);
        return new(Guid.NewGuid(), new IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest(
            "Browser integration", "mcp", "org-a", ["Incident.Read"], null)
        { McpResourceUri = "https://helpdesk.example.test/mcp", ExpiresAtUtc = expiry },
            [new McpPolicyScopeDraft("Organization A base", "organization", "org-a", "Allow", ["Incident.Read"],
                ["helpdesk_incidents.list"], expiry.UtcDateTime, [])], true);
    }
}
