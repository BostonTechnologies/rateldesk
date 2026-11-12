extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Helpdesk.Shared.DTOs.Organization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using DeleteDialog = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Organization.DeleteOrganizationDialog;

namespace Helpdesk.Tests.NewWeb;

public sealed class OrganizationDeletionDialogTests
{
    [Theory]
    [InlineData(403, "Your access does not permit this action")]
    [InlineData(404, "no longer exists")]
    [InlineData(409, "Retained history needs review")]
    [InlineData(500, "could not complete the action")]
    [InlineData(0, "result could not be confirmed")]
    public async Task Failed_deletion_stays_open_with_safe_feedback_and_no_false_success(int status, string message)
    {
        using var api = new Api { DeleteStatus = status };
        await using var context = Context(api);
        var host = context.Render<MudDialogProvider>();
        var dialog = await OpenAsync(context, host);
        var page = host.FindComponent<DeleteDialog>();
        page.WaitForAssertion(() => Assert.False(page.FindAll("button").Single(x => x.TextContent == "Delete").HasAttribute("disabled")));
        page.FindAll("button").Single(x => x.TextContent == "Delete").Click();
        page.WaitForAssertion(() => Assert.Contains(message, page.Find("[role='alert']").TextContent));
        Assert.False(dialog.Result.IsCompleted); Assert.Equal(1, api.Deletes);
        Assert.DoesNotContain("private backend detail", page.Markup);
    }

    [Fact]
    public async Task Retained_revoked_identity_offers_explicit_disabled_retention_without_delete()
    {
        using var api = new Api { Retained = true };
        await using var context = Context(api);
        var host = context.Render<MudDialogProvider>(); var dialog = await OpenAsync(context, host);
        var page = host.FindComponent<DeleteDialog>();
        page.WaitForAssertion(() => Assert.Contains("revoked credential records", page.Markup));
        Assert.True(page.FindAll("button").Single(x => x.TextContent == "Delete").HasAttribute("disabled"));
        page.Find("input[type=checkbox]").Change(true);
        page.FindAll("button").Single(x => x.TextContent == "Retain disabled").Click();
        page.WaitForAssertion(() => Assert.True(dialog.Result.IsCompleted));
        Assert.Equal(1, api.Retirements); Assert.Equal(0, api.Deletes);
    }

    private static async Task<IDialogReference> OpenAsync(BunitContext context, IRenderedComponent<MudDialogProvider> host)
    {
        IDialogReference? dialog = null;
        await host.InvokeAsync(async () => dialog = await context.Services.GetRequiredService<IDialogService>().ShowAsync<DeleteDialog>("Delete organization",
            new DialogParameters { [nameof(DeleteDialog.Organization)] = new OrganizationDto { Id = "synthetic-org", Name = "Synthetic organization" } }));
        return dialog!;
    }
    private static BunitContext Context(Api api)
    {
        var context = new BunitContext(); context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(api); return context;
    }
    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public bool Retained { get; set; }
        public int DeleteStatus { get; set; } = 204;
        public int Deletes { get; private set; }
        public int Retirements { get; private set; }
        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("https://fixture.example/") };
        private OrganizationDeletionAssessmentDto Assessment => new(!Retained, true, false, Retained ? [new("principal_retention", 1, "Retained revoked credential records", "Keep the organization disabled.", null, false)] : []);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Assessment) });
            if (request.Method == HttpMethod.Post) { Retirements++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            Deletes++;
            if (DeleteStatus == 0) throw new HttpRequestException("private backend detail");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)DeleteStatus) { Content = JsonContent.Create(new { code = "dependency", message = "Retained history needs review", assessment = Assessment }) });
        }
    }
}
