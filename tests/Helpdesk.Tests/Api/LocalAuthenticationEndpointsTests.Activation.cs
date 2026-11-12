using System.Net;
using System.Net.Http.Json;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed partial class LocalAuthenticationEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Administrator_issued_activation_and_reset_preserve_token_after_policy_failure_then_allow_login(bool resetExisting)
    {
        const string email = "activation.fixture@example.test";
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Synthetic activation account" };
            var created = resetExisting ? await users.CreateAsync(user, "a previous safe passphrase") : await users.CreateAsync(user);
            Assert.True(created.Succeeded);
        }
        using var admin = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
        string userId;
        await using (var scope = _factory.Services.CreateAsyncScope())
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
        var issued = await admin.PostAsync($"/api/v1/local-auth/users/{userId}/activation-token", null);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var token = (await issued.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.LocalAccountActivationResponse>())!;
        using var account = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var rejected = await account.PostAsJsonAsync("/api/v1/local-auth/activate",
            new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(email, token.ActivationToken, "12345678901234"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("password_policy", await rejected.Content.ReadAsStringAsync());
        const string passphrase = "123456789012345";
        Assert.Equal(HttpStatusCode.NoContent, (await account.PostAsJsonAsync("/api/v1/local-auth/activate",
            new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(email, token.ActivationToken, passphrase))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await account.PostAsJsonAsync("/api/v1/local-auth/activate",
            new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(email, token.ActivationToken, passphrase))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await account.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest(email, passphrase))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await account.GetAsync("/api/v1/auth/me")).StatusCode);
    }
}
