extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Helpdesk.Shared.DTOs.User;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Bunit.TestDoubles;
using Helpdesk.Shared.DTOs;
using DeleteDialog = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.User.DeleteUserDialog;
using SignInDialog = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.User.ChangeUserSignInDialog;
using TeamPage = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.User.UserManagement;
using AuthenticationMode = NewWeb::HelpDesk.NewWeb.Models.WebAuthenticationMode;

namespace Helpdesk.Tests.NewWeb;

public sealed class UserLifecycleDialogTests
{
    [Theory]
    [InlineData("Local", true)]
    [InlineData("Hybrid", true)]
    [InlineData("Oidc", false)]
    public async Task Team_respects_mode_route_availability_while_Delete_remains_distinct(string mode, bool localActions)
    {
        using var api = new DirectoryApi();
        await using var context = new BunitContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var authorization = context.AddAuthorization(); authorization.SetAuthorized("Synthetic administrator"); authorization.SetRoles("HelpdeskAdmin");
        context.Services.AddSingleton(new AuthenticationMode(mode));
        context.Services.AddSingleton<IHttpClientFactory>(api); context.Render<MudPopoverProvider>();
        var page = context.Render<TeamPage>();
        page.WaitForAssertion(() => Assert.Contains("1 team members", page.Markup));
        Assert.Single(page.FindAll("button[aria-label='Delete user']"));
        Assert.Equal(localActions ? 1 : 0, page.FindAll("button[aria-label='Manage local account']").Count);
        Assert.Equal(localActions ? 1 : 0, page.FindAll("button[aria-label='Disable sign-in']").Count);
        Assert.DoesNotContain(api.Paths, path => path.EndsWith("/status", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>untrusted raw error</html>")]
    [InlineData("[]")]
    public async Task Delete_is_explicit_keeps_error_open_and_retries_same_delete_without_status_or_disable_requests(string errorBody)
    {
        using var api = new Api { ErrorBody = errorBody };
        await using var context = Context(api);
        var host = context.Render<MudDialogProvider>();
        IDialogReference? dialog = null;
        await host.InvokeAsync(async () => dialog = await context.Services.GetRequiredService<IDialogService>().ShowAsync<DeleteDialog>("Delete user",
            new DialogParameters { [nameof(DeleteDialog.User)] = new UserDto("target", "Synthetic Person", "person@example.invalid", "User", false) }));
        var page = host.FindComponent<DeleteDialog>();
        Assert.Contains("permanently retires", page.Markup);
        Assert.Contains("Enable cannot restore", page.Markup);
        Assert.Empty(api.Requests);
        page.FindAll("button").Single(button => button.TextContent == "Delete user").Click();
        page.WaitForAssertion(() => Assert.Contains("HTTP 409", page.Find("[role=alert]").TextContent));
        Assert.False(dialog!.Result.IsCompleted);
        Assert.DoesNotContain("untrusted raw error", page.Markup);
        api.Reject = false;
        page.FindAll("button").Single(button => button.TextContent == "Delete user").Click();
        Assert.False((await dialog.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.Canceled);
        Assert.Equal(2, api.Requests.Count);
        Assert.All(api.Requests, request => Assert.Equal((HttpMethod.Delete, "/api/v1/users/target"), request));
    }

    [Fact]
    public async Task Enable_disable_is_a_separately_named_operation_using_the_projected_account_id()
    {
        using var api = new Api { Reject = false };
        await using var context = Context(api);
        var host = context.Render<MudDialogProvider>();
        IDialogReference? dialog = null;
        await host.InvokeAsync(async () => dialog = await context.Services.GetRequiredService<IDialogService>().ShowAsync<SignInDialog>("Disable sign-in",
            new DialogParameters { [nameof(SignInDialog.User)] = new UserDirectoryDto { Id = "domain-target", AccountId = "account-target", Name = "Synthetic Person", AccountState = "Enabled" } }));
        var page = host.FindComponent<SignInDialog>();
        Assert.Contains("reversible account setting", page.Markup);
        page.FindAll("button").Single(button => button.TextContent == "Disable sign-in").Click();
        Assert.False((await dialog!.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.Canceled);
        Assert.Equal((HttpMethod.Post, "/api/v1/local-auth/users/account-target/disable"), Assert.Single(api.Requests));
    }

    private static BunitContext Context(Api api)
    {
        var context = new BunitContext(); context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(api); context.Render<MudPopoverProvider>(); return context;
    }
    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public bool Reject { get; set; } = true;
        public string ErrorBody { get; set; } = string.Empty;
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://synthetic.example.invalid/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(Reject ? HttpStatusCode.Conflict : HttpStatusCode.NoContent)
            {
                Content = new StringContent(ErrorBody)
            });
        }
    }

    private sealed class DirectoryApi : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Paths { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://synthetic.example.invalid/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResult<UserDirectoryDto>
            {
                Page = 1, PageSize = 25, Total = 1, Items = [new() { Id = "profile", AccountId = "account", Name = "Synthetic Person", Email = "person@example.invalid", AccountState = "Enabled", AccountSignInEnabled = true, CanManageAccount = true, CanChangeSignIn = true, CanDelete = true }]
            }) });
        }
    }
}
