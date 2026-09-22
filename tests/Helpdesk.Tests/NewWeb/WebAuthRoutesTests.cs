extern alias NewWeb;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.Auth;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using NewWeb::HelpDesk.NewWeb;
using NewWeb::HelpDesk.NewWeb.Services;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Notification;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace Helpdesk.Tests.NewWeb;

public class WebAuthRoutesTests
{
    [Fact]
    public async Task Anonymous_session_poll_returns_unauthorized_without_an_oidc_redirect()
    {
        using var factory = CreateFactory(authenticationMode: "Oidc");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/session/access");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Omitted_mode_and_oidc_credentials_default_to_a_working_local_login_form()
    {
        using var factory = CreateFactory(omitAuthenticationMode: true);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/local-login\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/login-authentik\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oidc_only_logout_does_not_require_a_local_cookie_handler()
    {
        using var factory = CreateFactory(authenticationMode: "Oidc");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/logout");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://id.example.com/application/o/rateldesk/end-session", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HelpdeskApi")]
    [InlineData("HelpdeskApiStreaming")]
    [InlineData("SystemApi")]
    [InlineData("SystemApiNoAuth")]
    public void Shared_api_handlers_never_store_browser_cookies(string name)
    {
        using var factory = CreateFactory(localAuthentication: true, stubApi: false);
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler wrapper)
            handler = wrapper.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).UseCookies);
    }

    [Fact]
    public async Task Local_login_rejects_a_form_without_an_antiforgery_token()
    {
        using var factory = CreateFactory(localAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "user@example.test", ["password"] = "password" });
        var response = await client.PostAsync("/local-login", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?status=form-expired", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_Page_Shows_Provider_Neutral_Authentik_Branding()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/login");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("RatelDesk", content, StringComparison.Ordinal);
        Assert.Contains("Self-hosted", content, StringComparison.Ordinal);
        Assert.Contains("Continue to RatelDesk using your authorised account.", content, StringComparison.Ordinal);
        Assert.Contains("Sign in to RatelDesk", content, StringComparison.Ordinal);
        Assert.Contains("href=\"/login-authentik\"", content, StringComparison.Ordinal);
        Assert.Contains("rateldesk-mark.webp", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Automation Platform", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<header", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Developer account tools", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_Portal_Shows_EndUser_Actions_And_Admin_Link()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Welcome to RatelDesk", content, StringComparison.Ordinal);
        Assert.Contains("Submit a New Ticket", content, StringComparison.Ordinal);
        Assert.Contains("View Existing Tickets", content, StringComparison.Ordinal);
        Assert.Contains("RatelDesk", content, StringComparison.Ordinal);
        Assert.Contains("rateldesk-mark.webp", content, StringComparison.Ordinal);
        Assert.Contains("href=\"/login\"", content, StringComparison.Ordinal);
        Assert.Contains("Admin sign-in", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<header", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authenticated_User_Visiting_Login_Redirects_To_Home()
    {
        using var factory = CreateFactory(enableTestAuth: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/home", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Legacy_Account_Login_Redirects_To_Login()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Api_Shorthand_Redirects_To_WebHosted_Scalar_Docs()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/api/docs/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Scalar_reference_loads_the_document_and_uses_the_web_proxy_for_try_it()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/docs/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/openapi/v1.json", html, StringComparison.Ordinal);
        Assert.Contains("\"servers\":[{\"url\":\"/api\"}]", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_Authentik_Challenges_Authentik_Oidc()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/login-authentik");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("https://id.example.com/application/o/rateldesk/authorize",
            response.Headers.Location!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_Mode_Login_Page_Uses_The_Local_Credentials_Form()
    {
        using var factory = CreateFactory(localAuthentication: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/login");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/local-login\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Authenticator or recovery code", content, StringComparison.Ordinal);
        var inputNames = ReadNativeInputNames(content);
        Assert.DoesNotContain("twoFactorCode", inputNames);
        Assert.DoesNotContain("code", inputNames);
        Assert.DoesNotContain("href=\"/activate\"", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email", inputNames);
        Assert.Contains("password", inputNames);
        Assert.Contains("Sign in to RatelDesk", content, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/login-authentik\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_password_form_forwards_only_credentials_then_copies_the_authentication_cookie()
    {
        string? submitted = null;
        using var factory = CreateFactory(localAuthentication: true, loginApi: async (request, cancellationToken) =>
        {
            submitted = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.Add("Set-Cookie", "RatelDesk.Local=authenticated-ticket; path=/; httponly");
            return response;
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await ReadFormTokenAsync(client, "/login");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "  admin@example.test  ",
            ["password"] = "a strong test password",
            ["rememberMe"] = "true"
        });
        var response = await client.PostAsync("/local-login", form);
        Assert.Equal("/home", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("RatelDesk.Local=", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(submitted!);
        Assert.Equal("admin@example.test", payload.RootElement.GetProperty("email").GetString());
        Assert.True(payload.RootElement.GetProperty("rememberMe").GetBoolean());
        Assert.False(payload.RootElement.TryGetProperty("twoFactorCode", out _));
    }

    [Theory]
    [InlineData(401, "Sign-in%20failed")]
    [InlineData(404, "service-unavailable")]
    [InlineData(503, "service-unavailable")]
    [InlineData(429, "rate-limited")]
    public async Task Local_login_distinguishes_invalid_credentials_from_api_service_failures(int status, string expectedStatus)
    {
        using var factory = CreateFactory(localAuthentication: true,
            loginApi: (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await ReadFormTokenAsync(client, "/login");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["email"] = "admin@example.test", ["password"] = "test password"
        });
        var response = await client.PostAsync("/local-login", form);
        Assert.Equal("/login?status=" + expectedStatus, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Second_factor_form_requires_a_valid_password_challenge(bool forgedCookie)
    {
        using var factory = CreateFactory(localAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (forgedCookie)
            client.DefaultRequestHeaders.Add("Cookie", "RatelDesk.LocalChallenge=forged");
        var response = await client.GetAsync("/login/two-factor");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?status=verification-expired", new Uri(client.BaseAddress!, response.Headers.Location!).PathAndQuery);
    }

    [Fact]
    public async Task Enrolled_account_uses_a_separate_code_form_with_only_the_protected_challenge_forwarded()
    {
        string? protectedChallenge = null;
        string? verificationJson = null;
        string? verificationCookie = null;
        using var factory = CreateFactory(localAuthentication: true, loginApi: async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/two-factor", StringComparison.Ordinal))
            {
                verificationJson = await request.Content!.ReadAsStringAsync(cancellationToken);
                verificationCookie = string.Join(";", request.Headers.GetValues("Cookie"));
                var complete = new HttpResponseMessage(HttpStatusCode.NoContent);
                complete.Headers.Add("Set-Cookie", "RatelDesk.Local=authenticated-ticket; path=/; httponly");
                complete.Headers.Add("Set-Cookie", "RatelDesk.LocalChallenge=; path=/; max-age=0; httponly");
                return complete;
            }
            var challenge = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new { requiresTwoFactor = true })
            };
            challenge.Headers.Add("Set-Cookie", $"RatelDesk.LocalChallenge={protectedChallenge}; path=/; max-age=300; httponly; samesite=strict");
            return challenge;
        });
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LocalSignInChallenge.ProtectionPurpose).ToTimeLimitedDataProtector();
        protectedChallenge = protector.Protect(JsonSerializer.Serialize(new LocalSignInChallenge("account-id", "stamp", 1, false)), LocalSignInChallenge.Lifetime);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await ReadFormTokenAsync(client, "/login");
        using var password = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["email"] = "admin@example.test", ["password"] = "test password"
        });
        var first = await client.PostAsync("/local-login", password);
        Assert.Equal("/login/two-factor", first.Headers.Location?.OriginalString);
        var page = await client.GetAsync("/login/two-factor");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Verify your sign-in", html, StringComparison.Ordinal);
        var inputNames = ReadNativeInputNames(html);
        Assert.Contains("code", inputNames);
        Assert.DoesNotContain("password", inputNames);
        Assert.DoesNotContain("email", inputNames);
        Assert.DoesNotContain("test password", html, StringComparison.Ordinal);
        using var verification = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractFormToken(html), ["code"] = "123456"
        });
        var second = await client.PostAsync("/local-login/two-factor", verification);
        Assert.Equal("/home", second.Headers.Location?.OriginalString);
        Assert.Equal("RatelDesk.LocalChallenge=" + protectedChallenge, verificationCookie);
        using var payload = JsonDocument.Parse(verificationJson!);
        Assert.Equal("123456", payload.RootElement.GetProperty("code").GetString());
        Assert.False(payload.RootElement.TryGetProperty("password", out _));
    }

    // HTML attribute names are case-insensitive. MudBlazor preserves the casing
    // of unmatched attributes when it renders them on the native input element.
    private static string[] ReadNativeInputNames(string html) =>
        Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase)
            .Select(input => Regex.Match(input.Value, "\\bname\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
            .Where(attribute => attribute.Success)
            .Select(attribute => WebUtility.HtmlDecode(attribute.Groups[1].Value))
            .ToArray();

    private static async Task<string> ReadFormTokenAsync(HttpClient client, string route) =>
        ExtractFormToken(await client.GetStringAsync(route));

    private static string ExtractFormToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "The sign-in form must have an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Fact]
    public async Task Local_Mode_Authentik_Login_Route_Returns_To_Local_Login()
    {
        using var factory = CreateFactory(localAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/login-authentik");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Hybrid_Mode_Offers_Local_And_Oidc_Login()
    {
        using var factory = CreateFactory(authenticationMode: "Hybrid");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var login = await client.GetAsync("/login");
        var loginContent = await login.Content.ReadAsStringAsync();
        var oidc = await client.GetAsync("/login-authentik");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("action=\"/local-login\"", loginContent, StringComparison.Ordinal);
        Assert.Contains("href=\"/login-authentik\"", loginContent, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, oidc.StatusCode);
        Assert.StartsWith("https://id.example.com/application/o/rateldesk/authorize", oidc.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Login_Page_Includes_The_Published_Blazor_Bootstrap_Asset()
    {
        var component = File.ReadAllText(Path.Combine(
            TestEnvironment.RepositoryRoot,
            "src",
            "HelpDesk.NewWeb",
            "Components",
            "App.razor"));
        var project = File.ReadAllText(Path.Combine(
            TestEnvironment.RepositoryRoot,
            "src",
            "HelpDesk.NewWeb",
            "HelpDesk.NewWeb.csproj"));

        Assert.Contains("<script src=@Assets[\"_framework/blazor.web.js\"] autostart=\"false\"></script>", component, StringComparison.Ordinal);
        var componentInterop = component.IndexOf("_content/MudBlazor/MudBlazor.min.js", StringComparison.Ordinal);
        var circuitStart = component.IndexOf("Blazor.start();", StringComparison.Ordinal);
        Assert.True(componentInterop >= 0 && circuitStart > componentInterop);
        Assert.Contains("<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void WebHost_Fails_Startup_When_Authentik_Client_Is_Not_Configured()
    {
        using var factory = CreateFactory(useAzureFallback: true);

        var ex = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(ex.Failures, failure => failure.Contains("Authentication:Authentik:ClientId", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, failure => failure.Contains("AUTHENTIK_CLIENT_SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anonymous_User_Is_Challenged_By_Authentik_For_Authorized_Page()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/auth");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("https://id.example.com/application/o/rateldesk/authorize",
            response.Headers.Location!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_User_Is_Challenged_By_Authentik_For_Email_Settings()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/admin/email-settings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("https://id.example.com/application/o/rateldesk/authorize",
            response.Headers.Location!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_User_Can_Open_Authorized_Page()
    {
        using var factory = CreateFactory(enableTestAuth: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.GetAsync("/auth");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Authentication Dashboard", content, StringComparison.Ordinal);
        Assert.Contains("stub-access-token", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpdeskAdmin_Can_Open_Email_Settings_Page()
    {
        using var factory = CreateFactory(enableTestAuth: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.GetAsync("/admin/email-settings");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Email Settings / Mailbox Configuration", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_Detail_Does_Not_Call_Api_During_Production_Prerender()
    {
        using var factory = CreateFactory(enableTestAuth: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.GetAsync("/changes/change-1");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("blazor.web.js", content, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred while processing your request", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_List_Does_Not_Throw_When_Lifecycle_Query_Is_Absent()
    {
        using var factory = CreateFactory(enableTestAuth: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.GetAsync("/changes");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Changes", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Querystring values cannot be parsed", content, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred while processing your request", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_User_Without_Admin_Role_Gets_Forbidden_For_Admin_Page()
    {
        using var factory = CreateFactory(enableTestAuth: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "Technician");

        var response = await client.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_User_Without_Admin_Role_Gets_Forbidden_For_Email_Settings()
    {
        using var factory = CreateFactory(enableTestAuth: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "Technician");

        var response = await client.GetAsync("/admin/email-settings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static WebApplicationFactory<TokenService> CreateFactory(
        bool enableTestAuth = false,
        bool useAzureFallback = false,
        bool localAuthentication = false,
        string? authenticationMode = null,
        bool omitAuthenticationMode = false,
        bool stubApi = true,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? loginApi = null)
    {
        return new WebApplicationFactory<TokenService>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Production");
            builder.UseEnvironment("Production");
            var selectedAuthenticationMode = authenticationMode ?? (localAuthentication ? "Local" : null);
            builder.UseSetting("Authentication:Mode", omitAuthenticationMode ? "" : selectedAuthenticationMode ?? "Oidc");
            if (selectedAuthenticationMode is not null)
                builder.UseSetting("Authentication:AllowInsecureLocalhost", "true");

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = useAzureFallback
                    ? new Dictionary<string, string?>
                    {
                        ["AZURE_CLIENT_SECRET"] = "azure-client-secret",
                        ["Authentication:Authentik:Authority"] = "https://id.example.com/application/o/rateldesk/",
                        ["Authentication:Azure:Authority"] = "https://login.microsoftonline.com/tenant/v2.0",
                        ["Authentication:Azure:ClientId"] = "azure-client-id",
                        ["Authentication:Azure:ApiScope"] = "api://helpdesk/access_as_user",
                        ["Authentication:Azure:CallbackPath"] = "/signin-azure",
                        ["Authentication:Azure:SignedOutCallbackPath"] = "/signout-azure",
                        ["ApiBaseUrl"] = "http://127.0.0.1:9/"
                    }
                    : new Dictionary<string, string?>
                    {
                        ["AUTHENTIK_CLIENT_SECRET"] = "client-secret",
                        ["Authentication:Authentik:Authority"] = "https://id.example.com/application/o/rateldesk/",
                        ["Authentication:Authentik:ClientId"] = "client-id",
                        ["Authentication:Authentik:ApiScope"] = "helpdesk-api",
                        ["Authentication:Authentik:CallbackPath"] = "/signin-authentik",
                        ["Authentication:Authentik:SignedOutCallbackPath"] = "/signout-authentik",
                        ["ApiBaseUrl"] = "http://127.0.0.1:9/"
                    };

                if (selectedAuthenticationMode is not null)
                {
                    settings["Authentication:Mode"] = selectedAuthenticationMode;
                    settings["Authentication:AllowInsecureLocalhost"] = "true";
                }

                if (omitAuthenticationMode)
                {
                    settings["Authentication:Mode"] = "";
                    settings["Authentication:Authentik:ClientId"] = "";
                    settings["AUTHENTIK_CLIENT_SECRET"] = "";
                }
                cfg.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                if (stubApi)
                {
                    foreach (var clientName in new[] { "HelpdeskApi", "HelpdeskApiStreaming", "SystemApi", "SystemApiNoAuth" })
                        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => new BrandingApiHandler(loginApi));
                }

                services.PostConfigure<OpenIdConnectOptions>("Authentik", options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        AuthorizationEndpoint = "https://id.example.com/application/o/rateldesk/authorize",
                        EndSessionEndpoint = "https://id.example.com/application/o/rateldesk/end-session"
                    };

                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });

                if (!enableTestAuth)
                    return;

                services.AddSingleton<ITokenService, StubTokenService>();
                services.AddSingleton<ISystemNotificationApiClient, StubSystemNotificationApiClient>();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, NewWebTestAuthHandler>("Test", _ => { });
            });
        });
    }

    private sealed class BrandingApiHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? loginApi) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.StartsWith("/api/v1/local-auth/", StringComparison.Ordinal) == true && loginApi is not null)
                return loginApi(request, cancellationToken);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri?.AbsolutePath == "/api/v1/setup/status"
                    ? JsonContent.Create(new { state = "Ready" })
                    : JsonContent.Create(new { applicationName = "RatelDesk", compactLogoUrl = "/branding/rateldesk-mark.webp", faviconUrl = "/favicon.ico" })
            });
        }
    }

    private sealed class StubTokenService : ITokenService
    {
        public Task<string?> GetValidAccessTokenAsync() => Task.FromResult<string?>("stub-access-token");

        public Task<bool> TryRefreshSessionAsync(HttpContext ctx, AuthenticateResult auth, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class StubSystemNotificationApiClient : ISystemNotificationApiClient
    {
        public Task<PagedResponse<NotificationDto>> GetPagedNotificationsAsync(
            int page = 1,
            int pageSize = 10,
            string? searchTerm = null,
            NotificationSeverity? severity = null,
            string? source = null,
            string? category = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            string? sortBy = null,
            string? sortDir = null,
            CancellationToken ct = default)
            => Task.FromResult(new PagedResponse<NotificationDto> { Page = page, PageSize = pageSize });

        public Task<List<NotificationDto>> GetNotificationsAsync(
            int page = 1,
            int pageSize = 10,
            string? searchTerm = null,
            NotificationSeverity? severity = null,
            string? source = null,
            string? category = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null)
            => Task.FromResult(new List<NotificationDto>());

        public Task<List<NotificationDto>> GetUnreadNotificationsAsync(int page = 1, int pageSize = 10)
            => Task.FromResult(new List<NotificationDto>());

        public Task<List<NotificationDto>> GetUnreadErrorNotificationsAsync(int take = 20)
            => Task.FromResult(new List<NotificationDto>());

        public Task<List<NotificationDto>> GetTimelineAsync(
            string? reference = null,
            string? correlationId = null,
            string? tenantId = null,
            int take = 200)
            => Task.FromResult(new List<NotificationDto>());

        public Task<NotificationDto?> GetByIdAsync(Guid id)
            => Task.FromResult<NotificationDto?>(null);

        public Task<NotificationSummaryDto> GetSummaryAsync()
            => Task.FromResult(new NotificationSummaryDto());

        public Task<NotificationSummaryDto> GetErrorSummaryAsync()
            => Task.FromResult(new NotificationSummaryDto());

        public Task MarkReadAsync(Guid id) => Task.CompletedTask;

        public Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids) => Task.FromResult(0);

        public Task<int> PurgeAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NewWebTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public NewWebTestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder) : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
            {
                return Task.FromResult(AuthenticateResult.Fail("No authorization header"));
            }

            var role = header.ToString().Contains("HelpdeskAdmin", StringComparison.OrdinalIgnoreCase)
                ? "HelpdeskAdmin"
                : "Technician";

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, "Test User"),
                new(ClaimTypes.Role, role),
                new("preferred_username", "test.user@example.com")
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
