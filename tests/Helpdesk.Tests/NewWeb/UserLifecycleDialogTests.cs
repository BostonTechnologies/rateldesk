extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Helpdesk.Shared.DTOs.User;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using DeleteDialog = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.User.DeleteUserDialog;
using SignInDialog = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.User.ChangeUserSignInDialog;

namespace Helpdesk.Tests.NewWeb;

public sealed class UserLifecycleDialogTests
{
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
}
