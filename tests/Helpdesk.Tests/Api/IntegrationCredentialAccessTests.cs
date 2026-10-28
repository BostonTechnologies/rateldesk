using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class IntegrationCredentialSqliteTests
{
    [Theory]
    [InlineData("api")]
    [InlineData("mcp")]
    public async Task Reviewed_access_edit_keeps_saved_secret_identity_exact_expiry_and_unrelated_credentials(string purpose)
    {
        await using var harness = await Harness.CreateAsync();
        var created = await CreateEditableAsync(harness, purpose);
        var unrelated = Credential("owner", "unrelated", DateTimeOffset.UtcNow);
        await harness.AddCredentialsAsync(unrelated);
        var before = await ReadCredentialAsync(harness, created.Id);
        var access = await ReadAccessAsync(harness, created.Id);
        Assert.True(access.CanEdit);
        Assert.Contains("Incident.Write", access.AvailablePermissions);
        var review = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Read", "Incident.Write");
        Assert.Equal(["Incident.Write"], review.AddedPermissions);
        Assert.Empty(review.RemovedPermissions);
        Assert.Equal("Incident.Read", (await ReadCredentialAsync(harness, created.Id)).Permissions);

        using var withoutConsent = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/{created.Id}/access/apply",
            new ApplyIntegrationCredentialAccess(review.Confirmation, false));
        Assert.Equal(HttpStatusCode.BadRequest, withoutConsent.StatusCode);
        using var applied = await ApplyAccessAsync(harness, created.Id, review);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal("no-store", applied.Headers.CacheControl?.ToString());
        var savedAccess = await applied.Content.ReadFromJsonAsync<IntegrationCredentialAccessDetails>();
        Assert.Equal(["Incident.Read", "Incident.Write"], savedAccess!.Permissions);
        Assert.Equal(savedAccess.Permissions, savedAccess.EffectivePermissions);
        Assert.Empty(savedAccess.DeniedPermissions);
        var after = await ReadCredentialAsync(harness, created.Id);
        Assert.Equal((before.Id, before.OwnerUserId, before.Name, before.SecretHash, before.Prefix, before.Purpose,
                before.OrganizationId, before.McpResourceUri, before.CreatedAtUtc, before.ExpiresAtUtc),
            (after.Id, after.OwnerUserId, after.Name, after.SecretHash, after.Prefix, after.Purpose,
                after.OrganizationId, after.McpResourceUri, after.CreatedAtUtc, after.ExpiresAtUtc));
        Assert.Equal("Incident.Read", (await ReadCredentialAsync(harness, unrelated.Id)).Permissions);

        using var device = harness.DeviceClient();
        using var authenticate = new HttpRequestMessage(HttpMethod.Get, purpose == "api"
            ? "/api/v1/pairing-read-only-check" : "/api/v1/mcp-read-only-check");
        authenticate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.Secret);
        using var authenticated = await device.SendAsync(authenticate);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        // Actual client last-use updates must not invalidate replay of the accepted review.
        using var replay = await ApplyAccessAsync(harness, created.Id, review);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var scope = harness.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().ActivityLogs
            .Where(x => x.RelatedEntityId == created.Id.ToString("N") && x.Message.Contains("access updated")).ToListAsync();
        Assert.Single(audit);
        Assert.DoesNotContain(created.Secret, audit[0].Message);
        Assert.DoesNotContain(before.SecretHash, audit[0].Message);
        var list = await harness.Client.GetFromJsonAsync<List<IntegrationCredentialEndpoints.IntegrationCredentialMetadata>>("/api/v1/integration-credentials/");
        Assert.Equal(savedAccess.Permissions, list!.Single(x => x.Id == created.Id).Permissions);
    }

    [Fact]
    public async Task Stale_review_cannot_overwrite_a_concurrent_grant_edit()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await CreateEditableAsync(harness);
        var access = await ReadAccessAsync(harness, created.Id);
        var first = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Read", "Incident.Write");
        var competing = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Write");
        Assert.Equal(["Incident.Read"], competing.RemovedPermissions);
        using var applied = await ApplyAccessAsync(harness, created.Id, first);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        using var rejected = await ApplyAccessAsync(harness, created.Id, competing);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var staleDraft = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/{created.Id}/access/review",
            new IntegrationCredentialAccessDraft(["Incident.Write"], access.Version));
        Assert.Equal(HttpStatusCode.Conflict, staleDraft.StatusCode);
        Assert.Equal("Incident.Read Incident.Write", (await ReadCredentialAsync(harness, created.Id)).Permissions);
    }

    [Fact]
    public async Task Permission_removal_applies_only_after_an_exact_review_and_keeps_policy_constraints()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await CreateEditableAsync(harness, "mcp");
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Set<McpAccessPolicy>().Add(new()
            {
                CredentialId = created.Id, OrganizationId = "org-a", PermissionsJson = "[\"Incident.Read\"]",
                State = "Active", Revision = 7, ExpiresUtc = DateTime.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
        }
        var access = await ReadAccessAsync(harness, created.Id);
        var added = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Read", "Incident.Write");
        using var applied = await ApplyAccessAsync(harness, created.Id, added);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var constrained = (await applied.Content.ReadFromJsonAsync<IntegrationCredentialAccessDetails>())!;
        Assert.Equal(["Incident.Read"], constrained.EffectivePermissions);
        Assert.Equal(["Incident.Write"], constrained.DeniedPermissions);
        Assert.Equal("Active", constrained.PolicyState);
        var removed = await ReviewAccessAsync(harness, created.Id, constrained.Version, "Incident.Write");
        Assert.Equal(["Incident.Read"], removed.RemovedPermissions);
        using var removal = await ApplyAccessAsync(harness, created.Id, removed);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);
        var denied = (await removal.Content.ReadFromJsonAsync<IntegrationCredentialAccessDetails>())!;
        Assert.Empty(denied.EffectivePermissions);
        Assert.Equal(["Incident.Write"], denied.DeniedPermissions);
        await using var verify = harness.Services.CreateAsyncScope();
        var policy = await verify.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<McpAccessPolicy>().SingleAsync();
        Assert.Equal(7, policy.Revision);
        Assert.Equal("[\"Incident.Read\"]", policy.PermissionsJson);
    }

    [Fact]
    public async Task Access_review_rechecks_current_authority_on_apply()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await CreateEditableAsync(harness);
        var access = await ReadAccessAsync(harness, created.Id);
        var review = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Read", "Incident.Write");
        var current = (TestAccessService)harness.Services.GetRequiredService<ICurrentUserAccessService>();
        current.Profile = current.Profile with
        {
            Permissions = new HashSet<string>(["Incident.Read"]),
            ScopedPermissionGrants = new HashSet<ScopedPermissionGrant> { new("Incident.Read", "org-a") }
        };
        using var rejected = await ApplyAccessAsync(harness, created.Id, review);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("Incident.Read", (await ReadCredentialAsync(harness, created.Id)).Permissions);
    }

    [Fact]
    public async Task Owner_administration_with_a_policy_reports_the_execution_context_denial()
    {
        await using var harness = await Harness.CreateAsync();
        var current = (TestAccessService)harness.Services.GetRequiredService<ICurrentUserAccessService>();
        current.Profile = current.Profile with { IsHelpdeskAdmin = true };
        var credential = Credential("owner", "owner administration", DateTimeOffset.UtcNow);
        credential.Purpose = IntegrationCredentialGrantKinds.OwnerAdministrationPurpose;
        credential.Permissions = HelpdeskPermissions.HelpdeskAdmin;
        credential.McpResourceUri = "https://desk.example/mcp";
        await harness.AddCredentialsAsync(credential);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            (await identity.Users.SingleAsync(x => x.Id == "owner")).IsInstanceAdministrator = true;
            await identity.SaveChangesAsync();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Set<McpAccessPolicy>().Add(new()
            {
                CredentialId = credential.Id, OrganizationId = "org-a", PermissionsJson = "[\"Incident.Read\"]",
                State = "Active", Revision = 1, ExpiresUtc = DateTime.UtcNow.AddHours(1)
            });
            await db.SaveChangesAsync();
        }
        var access = await ReadAccessAsync(harness, credential.Id);
        Assert.Empty(access.EffectivePermissions);
        Assert.Equal([HelpdeskPermissions.HelpdeskAdmin], access.DeniedPermissions);
        Assert.Contains("requires an instance-administrator context", access.PolicySummary);
        Assert.False(access.CanEdit);
    }

    [Theory]
    [InlineData("integration")]
    [InlineData("mcp")]
    [InlineData("gateway")]
    [InlineData("service")]
    [InlineData("machine")]
    public async Task Machines_cannot_inspect_review_or_apply_same_owner_credential_edits(string mode)
    {
        await using var harness = await Harness.CreateAsync();
        var created = await CreateEditableAsync(harness);
        var access = await ReadAccessAsync(harness, created.Id);
        var review = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Read", "Incident.Write");
        harness.Client.DefaultRequestHeaders.Add("X-Test-Auth-Mode", mode);
        using var get = await harness.Client.GetAsync($"/api/v1/integration-credentials/{created.Id}/access");
        using var preview = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/{created.Id}/access/review",
            new IntegrationCredentialAccessDraft(["Incident.Write"], access.Version));
        using var apply = await ApplyAccessAsync(harness, created.Id, review);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
        Assert.Equal("Incident.Read", (await ReadCredentialAsync(harness, created.Id)).Permissions);
    }

    [Fact]
    public async Task Review_is_bound_to_the_owner_and_exact_credential_and_cookie_mutations_keep_csrf_protection()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await CreateEditableAsync(harness);
        var sibling = await CreateEditableAsync(harness);
        var access = await ReadAccessAsync(harness, created.Id);
        var review = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Read", "Incident.Write");
        using var wrongTarget = await ApplyAccessAsync(harness, sibling.Id, review);
        Assert.Equal(HttpStatusCode.BadRequest, wrongTarget.StatusCode);
        harness.Client.DefaultRequestHeaders.Add("X-Test-User", "other");
        using var foreignRead = await harness.Client.GetAsync($"/api/v1/integration-credentials/{created.Id}/access");
        using var foreignApply = await ApplyAccessAsync(harness, created.Id, review);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignApply.StatusCode);
        harness.Client.DefaultRequestHeaders.Remove("X-Test-User");
        harness.Client.DefaultRequestHeaders.Remove("X-Requested-With");
        using var noCsrf = await ApplyAccessAsync(harness, created.Id, review);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        Assert.Equal("Incident.Read", (await ReadCredentialAsync(harness, created.Id)).Permissions);
    }

    [Theory]
    [InlineData("revoked", HttpStatusCode.Conflict)]
    [InlineData("expired", HttpStatusCode.Conflict)]
    [InlineData("disabled-owner", HttpStatusCode.Forbidden)]
    [InlineData("disabled-organization", HttpStatusCode.BadRequest)]
    public async Task Revocation_expiry_and_disabled_authority_after_review_prevent_apply(string reason, HttpStatusCode expected)
    {
        await using var harness = await Harness.CreateAsync();
        var created = await CreateEditableAsync(harness);
        var access = await ReadAccessAsync(harness, created.Id);
        var review = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Read", "Incident.Write");
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var credential = await identity.IntegrationCredentials.SingleAsync();
            if (reason == "revoked") credential.RevokedAtUtc = DateTimeOffset.UtcNow;
            if (reason == "expired") credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (reason == "disabled-owner") (await identity.Users.SingleAsync(x => x.Id == "owner")).IsEnabled = false;
            await identity.SaveChangesAsync();
            if (reason == "disabled-organization")
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                (await db.Organizations.SingleAsync(x => x.Id == "org-a")).IsEnabled = false;
                await db.SaveChangesAsync();
            }
        }
        using var rejected = await ApplyAccessAsync(harness, created.Id, review);
        Assert.Equal(expected, rejected.StatusCode);
        Assert.Equal("Incident.Read", (await ReadCredentialAsync(harness, created.Id)).Permissions);
        if (reason == "revoked")
        {
            var refreshed = await ReadAccessAsync(harness, created.Id);
            Assert.NotNull(refreshed.RevokedAtUtc);
            Assert.False(refreshed.CanEdit);
        }
    }

    [Theory]
    [InlineData("mcp-setup")]
    [InlineData(IntegrationCredentialGrantKinds.OwnerAdministrationPurpose)]
    public async Task Special_consent_grants_are_viewable_but_cannot_be_converted_by_the_access_editor(string purpose)
    {
        await using var harness = await Harness.CreateAsync();
        var credential = Credential("owner", "special consent", DateTimeOffset.UtcNow);
        credential.Purpose = purpose;
        credential.McpResourceUri = "https://desk.example/mcp";
        await harness.AddCredentialsAsync(credential);
        var access = await ReadAccessAsync(harness, credential.Id);
        Assert.False(access.CanEdit);
        Assert.Contains("dedicated consent", access.EditUnavailableReason);
        using var rejected = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/{credential.Id}/access/review",
            new IntegrationCredentialAccessDraft(["Incident.Read", "Incident.Write"], access.Version));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(purpose, (await ReadCredentialAsync(harness, credential.Id)).Purpose);
    }

    [Fact]
    public async Task Expired_and_tampered_review_tokens_require_a_new_review()
    {
        var clock = new AccessReviewClock(DateTimeOffset.UtcNow);
        await using var harness = await Harness.CreateAsync(clock);
        var created = await CreateEditableAsync(harness);
        var access = await ReadAccessAsync(harness, created.Id);
        var review = await ReviewAccessAsync(harness, created.Id, access.Version, "Incident.Read", "Incident.Write");
        using var tampered = await ApplyAccessAsync(harness, created.Id, review with { Confirmation = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        using var expired = await ApplyAccessAsync(harness, created.Id, review);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal("Incident.Read", (await ReadCredentialAsync(harness, created.Id)).Permissions);
    }

    [Fact]
    public async Task Access_editor_rejects_unknown_unowned_and_empty_permission_sets()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await CreateEditableAsync(harness);
        var access = await ReadAccessAsync(harness, created.Id);
        foreach (var (permissions, expected) in new (string[], HttpStatusCode)[]
        {
            ([], HttpStatusCode.BadRequest), (["Unknown.Permission"], HttpStatusCode.BadRequest),
            (["Request.Write"], HttpStatusCode.Forbidden), (["HelpdeskAdmin"], HttpStatusCode.BadRequest)
        })
        {
            using var rejected = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/{created.Id}/access/review",
                new IntegrationCredentialAccessDraft(permissions, access.Version));
            Assert.Equal(expected, rejected.StatusCode);
        }
        Assert.Equal("Incident.Read", (await ReadCredentialAsync(harness, created.Id)).Permissions);
    }

    private static async Task<IntegrationCredentialEndpoints.CreatedIntegrationCredential> CreateEditableAsync(Harness harness, string purpose = "api")
    {
        using var response = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/", new
        {
            name = "Existing connection", purpose, organizationId = "org-a", permissions = new[] { "Incident.Read" },
            lifetimeDays = 30, mcpResourceUri = purpose == "mcp" ? "https://desk.example/mcp" : null
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IntegrationCredentialEndpoints.CreatedIntegrationCredential>())!;
    }

    private static async Task<IntegrationCredential> ReadCredentialAsync(Harness harness, Guid id)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>().IntegrationCredentials
            .AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static async Task<IntegrationCredentialAccessDetails> ReadAccessAsync(Harness harness, Guid id) =>
        (await harness.Client.GetFromJsonAsync<IntegrationCredentialAccessDetails>($"/api/v1/integration-credentials/{id}/access"))!;

    private static async Task<IntegrationCredentialAccessReview> ReviewAccessAsync(Harness harness, Guid id, string version, params string[] permissions)
    {
        using var response = await harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/{id}/access/review",
            new IntegrationCredentialAccessDraft(permissions, version));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IntegrationCredentialAccessReview>())!;
    }

    private static Task<HttpResponseMessage> ApplyAccessAsync(Harness harness, Guid id, IntegrationCredentialAccessReview review) =>
        harness.Client.PostAsJsonAsync($"/api/v1/integration-credentials/{id}/access/apply", new ApplyIntegrationCredentialAccess(review.Confirmation, true));

    private sealed class AccessReviewClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
