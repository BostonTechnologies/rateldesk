using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Middleware;
using Helpdesk.API.Services.Users;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_oidc_subject_cannot_use_generic_or_fixed_scheme_routes_and_public_routes_remain_anonymous(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var db = fixture.Open())
        {
            await db.Database.MigrateAsync();
            db.Organizations.Add(new Organization { Id = "organization", Name = "Synthetic organization" });
            db.Customers.Add(new Customer { Id = "contact", OrganizationId = "organization", Name = "Synthetic contact", Email = "contact@example.invalid" });
            db.Users.Add(new User { Id = "retired-profile", Name = "Synthetic OIDC person", OrganizationId = "organization" });
            db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "contact", DomainUserId = "retired-profile", OidcIssuer = "https://identity.example.invalid/realm", OidcSubject = "verified-subject", InviteStatus = CustomerInviteStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
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
        builder.Services.AddAuthentication("SyntheticOidc").AddScheme<AuthenticationSchemeOptions, OidcHandler>("SyntheticOidc", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseMiddleware<UserAccessClaimsMiddleware>(); app.UseAuthorization();
        var privateExecutions = 0;
        app.MapGet("/private", () => { privateExecutions++; return Results.Ok(); }).RequireAuthorization();
        app.MapGet("/fixed-scheme", () => { privateExecutions++; return Results.Ok(); })
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes("SyntheticOidc").RequireAuthenticatedUser());
        app.MapGet("/public", (HttpContext context) => Results.Ok(context.User.Identity?.IsAuthenticated == true)).AllowAnonymous();
        await app.StartAsync();
        using var before = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/private")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/fixed-scheme")).StatusCode);
        Assert.Equal(2, privateExecutions);
        await using (var db = fixture.Open())
        await using (var identity = fixture.Identity())
        {
            var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "remaining-admin")], "Test"));
            Assert.Null((await UserRetirement.RetireAsync("retired-profile", actor, db, identity)).Error);
        }
        using var after = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/private")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/fixed-scheme")).StatusCode);
        Assert.Equal(2, privateExecutions);
        var publicResult = await after.GetAsync("/public");
        Assert.Equal(HttpStatusCode.OK, publicResult.StatusCode);
        Assert.False(await publicResult.Content.ReadFromJsonAsync<bool>());
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
