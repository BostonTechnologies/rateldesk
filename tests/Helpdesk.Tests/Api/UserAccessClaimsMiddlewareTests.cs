using System.Security.Claims;
using Helpdesk.API.Middleware;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;

namespace Helpdesk.Tests.Api;

public sealed class UserAccessClaimsMiddlewareTests
{
    [Fact]
    public async Task Verified_service_grants_are_preserved_without_human_account_projection()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "service:synthetic-principal"),
                new Claim("auth_mode", "service"),
                new Claim("token_use", ServiceIdentityClaims.Purpose),
                new Claim("organization_id", "approved-service-organization"),
                new Claim("scope", ServiceIdentityScopes.IncidentCreate),
                new Claim(ServiceIdentityClaims.PeerTenantId, "approved-peer-tenant")
            ], "RatelDeskService"))
        };
        var original = context.User.Claims.ToArray();
        var called = false;
        var access = new RecordingAccessService(CurrentUserAccessProfile.FromClaims(new ClaimsPrincipal()));
        await new UserAccessClaimsMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, access);
        Assert.True(called);
        Assert.Empty(access.ObservedClaims);
        Assert.Equal(original, context.User.Claims);
        Assert.False(context.User.IsInRole(HelpdeskPermissions.HelpdeskAdmin));
    }

    [Fact]
    public async Task Application_access_claims_are_derived_from_the_resolved_profile()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "external-user"),
                    new Claim("organization_id", "attacker-org"),
                    new Claim("allowed_organization_id", "attacker-org"),
                    new Claim("customer_id", "attacker-customer"),
                    new Claim("scoped_permission", $"{HelpdeskPermissions.IncidentManager}|attacker-org"),
                    new Claim(ClaimTypes.Role, HelpdeskPermissions.ChangeManager),
                    new Claim("roles", HelpdeskPermissions.ChangeManager)
                ],
                "Test"))
        };
        var profile = new CurrentUserAccessProfile(
            true,
            "External user",
            "external@example.test",
            "server-org",
            "Server organization",
            "server-customer",
            false,
            new HashSet<string>(),
            new HashSet<string> { HelpdeskPermissions.IncidentManager },
            new HashSet<string> { "server-org" },
            new HashSet<string>())
        {
            ScopedPermissionGrants = new HashSet<ScopedPermissionGrant>
            {
                new(HelpdeskPermissions.IncidentManager, "server-org")
            }
        };
        var accessService = new RecordingAccessService(profile);
        var middleware = new UserAccessClaimsMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, accessService);

        Assert.DoesNotContain(accessService.ObservedClaims, claim =>
            claim.Type is "organization_id" or "allowed_organization_id" or "customer_id" or "scoped_permission");
        Assert.Contains(context.User.Claims, claim => claim.Type == "organization_id" && claim.Value == "server-org");
        Assert.Contains(context.User.Claims, claim => claim.Type == "customer_id" && claim.Value == "server-customer");
        Assert.Contains(context.User.Claims, claim => claim.Type == "scoped_permission" &&
            claim.Value == $"{HelpdeskPermissions.IncidentManager}|server-org");
        Assert.DoesNotContain(context.User.Claims, claim => claim.Value.StartsWith("attacker-", StringComparison.Ordinal));
        Assert.DoesNotContain(context.User.Claims, claim =>
            claim.Type is ClaimTypes.Role or "roles" && claim.Value == HelpdeskPermissions.ChangeManager);
    }

    private sealed class RecordingAccessService(CurrentUserAccessProfile profile) : ICurrentUserAccessService
    {
        public IReadOnlyList<Claim> ObservedClaims { get; private set; } = [];

        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            ObservedClaims = user.Claims.ToArray();
            return Task.FromResult(profile);
        }
    }
}
