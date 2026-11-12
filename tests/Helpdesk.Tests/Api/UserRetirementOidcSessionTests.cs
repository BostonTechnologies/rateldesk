using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Middleware;
using Helpdesk.API.Services.Users;
using Helpdesk.API.Endpoints.Users;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Api;

public sealed class UserRetirementOidcSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Retired_oidc_subject_cannot_use_generic_or_fixed_scheme_routes_and_public_routes_remain_anonymous(bool postgres, bool missingAccount)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var db = fixture.Open())
        {
            await db.Database.MigrateAsync();
            db.Organizations.Add(new Organization { Id = "organization", Name = "Synthetic organization" });
            db.Customers.Add(new Customer { Id = "contact", OrganizationId = "organization", Name = "Synthetic contact", Email = "contact@example.invalid" });
            db.Users.Add(new User { Id = "retired-profile", Name = "Synthetic OIDC person", OrganizationId = "organization" });
            db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "contact", DomainUserId = "retired-profile", LocalAccountId = missingAccount ? "missing-account" : null,
                OidcIssuer = "https://identity.example.invalid/realm", OidcSubject = "verified-subject", InviteStatus = CustomerInviteStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            if (!missingAccount)
                await OidcApplicationAccount.ProvisionAdministratorAsync(identity, "https://identity.example.invalid/realm", "verified-subject", "Synthetic OIDC person");
            identity.Users.Add(new ApplicationUser { Id = "remaining-admin", UserName = "remaining-admin", IsInstanceAdministrator = true });
            await identity.SaveChangesAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => fixture.Open());
        builder.Services.AddScoped(_ => fixture.Identity());
        builder.Services.AddScoped<ICurrentUserAccessService, CurrentUserAccessService>();
        builder.Services.AddScoped<IRepository<User>, EfRepository<User>>();
        builder.Services.AddAuthentication("SyntheticOidc").AddScheme<AuthenticationSchemeOptions, OidcHandler>("SyntheticOidc", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("HelpdeskAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin")));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseMiddleware<UserAccessClaimsMiddleware>(); app.UseAuthorization();
        var privateExecutions = 0;
        app.MapGet("/private", () => { privateExecutions++; return Results.Ok(); }).RequireAuthorization();
        app.MapGet("/fixed-scheme", () => { privateExecutions++; return Results.Ok(); })
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes("SyntheticOidc").RequireAuthenticatedUser());
        app.MapGet("/public", (HttpContext context) => Results.Ok(context.User.Identity?.IsAuthenticated == true)).AllowAnonymous();
        app.MapUserEndpoints();
        await app.StartAsync();
        using var before = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/private")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/fixed-scheme")).StatusCode);
        Assert.Equal(2, privateExecutions);
        using var directoryResponse = await before.GetAsync("/api/v1/users/query");
        Assert.Equal(missingAccount ? HttpStatusCode.Forbidden : HttpStatusCode.OK, directoryResponse.StatusCode);
        if (!missingAccount)
        {
            var directory = await directoryResponse.Content.ReadFromJsonAsync<PagedResult<UserDirectoryDto>>();
            Assert.Equal(25, directory!.PageSize); Assert.Equal(1, directory.Total);
            Assert.False(Assert.Single(directory.Items).CanDelete);
            Assert.Equal(HttpStatusCode.BadRequest, (await before.GetAsync("/api/v1/users/query?pageSize=10")).StatusCode);
        }
        await using (var db = fixture.Open())
        await using (var identity = fixture.Identity())
        {
            var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "remaining-admin")], "Test"));
            Assert.Null((await UserRetirement.RetireAsync("retired-profile", actor, db, identity)).Error);
        }
        using var after = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/private")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/fixed-scheme")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/api/v1/users/query")).StatusCode);
        Assert.Equal(2, privateExecutions);
        var publicResult = await after.GetAsync("/public");
        Assert.Equal(HttpStatusCode.OK, publicResult.StatusCode);
        Assert.False(await publicResult.Content.ReadFromJsonAsync<bool>());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("missing")]
    public async Task Same_scope_oidc_reauthorization_observes_account_changes_from_another_context(string change)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open(); await db.Database.MigrateAsync();
        await using var identity = fixture.Identity(); await identity.Database.MigrateAsync();
        var account = await OidcApplicationAccount.ProvisionAdministratorAsync(identity, "https://identity.example.invalid/realm", "verified-subject", "External administrator");
        identity.Users.Add(new ApplicationUser { Id = "remaining-admin", UserName = "remaining-admin", IsInstanceAdministrator = true });
        db.Users.Add(new User { Id = account.Id, Name = "Unlinked external administrator" });
        await identity.SaveChangesAsync(); await db.SaveChangesAsync();
        var service = new CurrentUserAccessService(db, identity);
        var principal = ExternalPrincipal();
        Assert.True((await service.ResolveAsync(principal)).CanViewRequest("organization", null, null));

        await using (var writer = fixture.Identity())
        {
            if (change == "deleted")
            {
                await using var domainWriter = fixture.Open();
                var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "remaining-admin")], "Test"));
                Assert.Null((await UserRetirement.RetireAsync(account.Id, actor, domainWriter, writer)).Error);
            }
            else
            {
                var changed = await writer.Users.SingleAsync(user => user.Id == account.Id);
                if (change == "missing") writer.Users.Remove(changed);
                else changed.IsEnabled = false;
                await writer.SaveChangesAsync();
            }
        }
        Assert.True(account.IsEnabled); // The original scope still holds the old tracked state.
        var after = await service.ResolveAsync(principal);
        Assert.False(after.CanViewRequest("organization", null, null)); Assert.False(after.IsHelpdeskAdmin);
        Assert.Empty(after.Permissions);
        Assert.Equal(change == "disabled", after.IsAuthenticated);
        Assert.False((await service.ResolveAsync(principal)).IsHelpdeskAdmin);
        // A distinct, previously unprovisioned provider administrator keeps the existing onboarding behavior.
        Assert.True((await service.ResolveAsync(ExternalPrincipal(subject: "different-subject"))).IsHelpdeskAdmin);
    }

    [Fact]
    public async Task Fresh_external_state_reads_preserve_persisted_provider_role_projection()
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open(); await db.Database.MigrateAsync();
        await using var identity = fixture.Identity(); await identity.Database.MigrateAsync();
        var account = await OidcApplicationAccount.ProvisionAdministratorAsync(identity, "https://identity.example.invalid/realm", "verified-subject", "External administrator");
        await using (var writer = fixture.Identity())
        {
            var changed = await writer.Users.SingleAsync(); changed.IsInstanceAdministrator = false; changed.AuthorizationRevision = 4;
            await writer.SaveChangesAsync();
        }
        var service = new CurrentUserAccessService(db, identity);
        Assert.True((await service.ResolveAsync(ExternalPrincipal())).IsHelpdeskAdmin);
        await using (var verify = fixture.Identity())
        {
            var persisted = await verify.Users.SingleAsync(); Assert.True(persisted.IsInstanceAdministrator); Assert.Equal(5, persisted.AuthorizationRevision);
        }
        Assert.False((await service.ResolveAsync(ExternalPrincipal(administrator: false))).IsHelpdeskAdmin);
        await using var final = fixture.Identity();
        var demoted = await final.Users.SingleAsync(); Assert.False(demoted.IsInstanceAdministrator); Assert.Equal(6, demoted.AuthorizationRevision);
    }

    private static ClaimsPrincipal ExternalPrincipal(string subject = "verified-subject", bool administrator = true)
    {
        var claims = new List<Claim> { new("iss", "https://identity.example.invalid/realm"), new("sub", subject) };
        if (administrator) claims.Add(new("groups", "HelpdeskAdmin"));
        return new(new ClaimsIdentity(claims, "Oidc"));
    }

    private sealed class OidcHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("iss", "https://identity.example.invalid/realm"), new Claim("sub", "verified-subject"),
                new Claim(ClaimTypes.NameIdentifier, "verified-subject"), new Claim("groups", "HelpdeskAdmin")
            ], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
