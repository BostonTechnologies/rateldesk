extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Activate = NewWeb::HelpDesk.NewWeb.Components.Pages.Activate;

namespace Helpdesk.Tests.NewWeb;

public sealed class ActivationPageTests
{
    [Theory]
    [InlineData("", "", "", "", "Enter your email.")]
    [InlineData("invalid", "synthetic-token", "123456789012345", "123456789012345", "Enter a valid email address.")]
    [InlineData("person@example.test", "synthetic-token", "12345678901234", "12345678901234", "Use at least 15 characters.")]
    [InlineData("person@example.test", "synthetic-token", "123456789012345", "different passphrase", "The passphrases do not match.")]
    public async Task Invalid_local_fields_render_actionable_errors_without_a_request(string email, string token, string password, string confirmation, string message)
    {
        using var api = new Api(); await using var context = Context(api);
        var page = context.Render<Activate>(); Fill(page, email, token, password, confirmation);
        page.FindAll("button").Single(button => button.TextContent.Contains("Activate account", StringComparison.Ordinal)).Click();
        page.WaitForAssertion(() => Assert.Contains(message, page.Markup));
        Assert.Equal(0, api.Calls);
    }

    [Theory]
    [InlineData(400, "activation_failed", "ask your administrator for a new token")]
    [InlineData(400, "password_policy", "at least 15 characters")]
    [InlineData(429, "", "Too many attempts")]
    [InlineData(500, "", "Try signing in before requesting a new token")]
    [InlineData(0, "", "Try signing in before requesting a new token")]
    public async Task Rejections_are_distinct_safe_and_never_automatically_replayed(int status, string code, string message)
    {
        using var api = new Api { Status = status, Error = code }; await using var context = Context(api);
        var page = context.Render<Activate>(); Fill(page, "person@example.test", "synthetic-token", "123456789012345", "123456789012345");
        page.FindAll("button").Single(button => button.TextContent.Contains("Activate account", StringComparison.Ordinal)).Click();
        page.WaitForAssertion(() => Assert.Contains(message, page.Find("[role='alert']").TextContent));
        Assert.Equal(1, api.Calls);
        Assert.DoesNotContain("private backend detail", page.Markup);
        Assert.DoesNotContain("synthetic-token", page.Find("[role='alert']").TextContent);
    }

    [Fact]
    public async Task Valid_boundary_submission_redirects_to_normal_login()
    {
        using var api = new Api { Status = 204 }; await using var context = Context(api);
        var page = context.Render<Activate>(); Fill(page, "person@example.test", "synthetic-token", "123456789012345", "123456789012345");
        page.FindAll("button").Single(button => button.TextContent.Contains("Activate account", StringComparison.Ordinal)).Click();
        page.WaitForAssertion(() => Assert.EndsWith("/login", context.Services.GetRequiredService<NavigationManager>().Uri));
        Assert.Equal(1, api.Calls);
    }

    private static void Fill(IRenderedComponent<Activate> page, params string[] values)
    { for (var i = 0; i < values.Length; i++) page.FindAll("input")[i].Change(values[i]); }
    private static BunitContext Context(Api api)
    {
        var context = new BunitContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(api); context.SetRendererInfo(new RendererInfo("Server", true)); return context;
    }
    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public int Status { get; set; } = 400;
        public string Error { get; set; } = "activation_failed";
        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("https://fixture.example/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Status == 0) throw new HttpRequestException("private backend detail");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status) { Content = JsonContent.Create(new { error = Error, minimumLength = 15 }) });
        }
    }
}
