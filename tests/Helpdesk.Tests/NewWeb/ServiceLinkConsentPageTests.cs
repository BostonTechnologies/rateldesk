extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using ConsentPage = NewWeb::HelpDesk.NewWeb.Components.Pages.ServiceLinkConsent;
using LinksPanel = NewWeb::HelpDesk.NewWeb.Components.Shared.NetRatelServiceLinks;
using M2MPanel = NewWeb::HelpDesk.NewWeb.Components.Shared.NetRatelM2M;
using ClientMetadata = NewWeb::HelpDesk.NewWeb.Models.NetRatelServiceClientMetadata;
using ClientReveal = NewWeb::HelpDesk.NewWeb.Models.NetRatelServiceClientReveal;

namespace Helpdesk.Tests.NewWeb;

/// <summary>Real rendered page/event lifecycle with an explicit unit API fixture, not peer provisioning.</summary>
public sealed class ServiceLinkConsentPageTests
{
    [Fact]
    public async Task Responder_details_follow_the_selected_organization_customer_and_narrowed_permissions()
    {
        using var api = new ConsentApi(Status(withSummary: false));
        await using var services = Services(api, responder: true);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        var page = reference.Page!;

        await ChangeAsync(renderer, page, "OrganizationChanged", "organization-a");
        await ChangeAsync(renderer, page, "CustomerChanged", "customer-a");
        await ChangeAsync(renderer, page, "OrganizationChanged", "organization-b");
        var changedOrganization = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Organization: organization-b", VisibleText(changedOrganization));
        Assert.Contains("Customers: None granted", VisibleText(changedOrganization));

        await ChangeAsync(renderer, page, "CustomerChanged", "customer-b");
        await ChangeAsync(renderer, page, "ScopesChanged", new[] { "rateldesk.incident-receipts.read" });
        await ChangeAsync(renderer, page, "OutboundInvokeChanged", false);
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        var text = VisibleText(html);
        Assert.Contains("Caller tenant: 17 · target tenant: organization-b", text);
        Assert.Contains("Caller tenant: organization-b · target tenant: 17", text);
        Assert.Contains("Customers: customer-b", text);
        Assert.Contains("Resources: resource-pinned", text);
        Assert.Contains("Request definitions: definition-pinned", text);
        Assert.Equal(new[] { "Scopes: netratel.orchestration.read", "Scopes: rateldesk.incident-receipts.read" }, ScopeParagraphs(html));
        Assert.Contains("The receiver source namespace remains pending", text);
        Assert.DoesNotContain("Stable receiver source namespace:", text);
        Assert.Equal(new[] { "rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.orchestration.callback" }, api.Status.Descriptor.RequestedGrants[0].Scopes);
        Assert.Equal(new[] { "netratel.orchestration.read", "netratel.orchestration.invoke" }, api.Status.Descriptor.RequestedGrants[1].Scopes);
    }

    [Theory]
    [InlineData("OrganizationChanged")]
    [InlineData("CustomerChanged")]
    [InlineData("ScopesChanged")]
    [InlineData("OutboundInvokeChanged")]
    public async Task Changing_a_displayed_grant_requires_new_exact_consent(string change)
    {
        using var api = new ConsentApi(Status(withSummary: false));
        await using var services = Services(api, responder: true);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        var page = reference.Page!;
        var confirmation = typeof(ConsentPage).GetField("confirmed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)page).HandleEventAsync(
            new EventCallbackWorkItem((Action)(() => confirmation.SetValue(page, true))), null));
        var approvedHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Matches(@"\bchecked(?:=|\s|>)", ConfirmationInput(approvedHtml));

        object value = change switch
        {
            "OrganizationChanged" => "organization-b", "CustomerChanged" => "customer-b",
            "ScopesChanged" => new[] { "rateldesk.incident-receipts.read" }, _ => false
        };
        await ChangeAsync(renderer, page, change, value);
        Assert.False((bool)confirmation.GetValue(page)!);
        var changedHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.DoesNotMatch(@"\bchecked(?:=|\s|>)", ConfirmationInput(changedHtml));
    }

    [Fact]
    public async Task Denied_consent_reload_clears_the_previous_grant_and_approval_form()
    {
        using var api = new ConsentApi(Status(withSummary: true));
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        Assert.Contains("service-link-final-approval", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        api.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.Contains("/attempts/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null);
        await renderer.Dispatcher.InvokeAsync(() => reference.Page!.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ConsentPage.AttemptId)] = "attempt-ui" })));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.DoesNotContain("service-link-final-approval", html);
        Assert.DoesNotContain("organization-approved", VisibleText(html));
        Assert.Contains("no longer authorized", VisibleText(html));
    }

    [Fact]
    public async Task Old_consent_load_cannot_populate_a_new_route_or_clear_its_current_state()
    {
        using var api = new ConsentApi(Status(withSummary: true));
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<HttpResponseMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Intercept = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempt-old", StringComparison.Ordinal))
            {
                entered.TrySetResult(); return oldResponse.Task;
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var navigation = (ConsentNavigation)services.GetRequiredService<NavigationManager>();
        await renderer.Dispatcher.InvokeAsync(() => navigation.SetRoute("attempt-old"));
        var oldLoad = renderer.Dispatcher.InvokeAsync(() => reference.Page!.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ConsentPage.AttemptId)] = "attempt-old" })));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.Dispatcher.InvokeAsync(() => navigation.SetRoute("attempt-new"));
        await renderer.Dispatcher.InvokeAsync(() => reference.Page!.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ConsentPage.AttemptId)] = "attempt-new" })));
        var currentHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("value=\"attempt-new\"", currentHtml);
        oldResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(api.ForAttempt("attempt-old")) });
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(currentHtml, await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
    }

    [Fact]
    public async Task Result_route_does_not_reload_or_reopen_the_retained_review_attempt()
    {
        using var api = new ConsentApi(Status(withSummary: true));
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        Assert.Contains("service-link-final-approval", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        var attemptsRead = 0;
        api.Intercept = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/attempts/", StringComparison.Ordinal)) ++attemptsRead;
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var navigation = (ConsentNavigation)services.GetRequiredService<NavigationManager>();
        await renderer.Dispatcher.InvokeAsync(navigation.SetResultRoute);

        await renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(ConsentPage).GetProperty(nameof(ConsentPage.Status))!.SetValue(reference.Page, "not-authorized");
            return reference.Page!.SetParametersAsync(ParameterView.Empty);
        });

        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.DoesNotContain("service-link-final-approval", html);
        Assert.DoesNotContain("service-link-consent-details", html);
        Assert.Contains("requires current administrator authority", VisibleText(html));
        Assert.Equal(0, attemptsRead);
        Assert.Equal("attempt-ui", reference.Page!.AttemptId);
    }

    [Fact]
    public async Task Denied_connection_refresh_removes_stale_actionable_rows()
    {
        using var api = new ConsentApi(Status(withSummary: true));
        api.Links = [api.Status];
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new LinksReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<LinksHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(LinksHarness.Reference)] = reference })));
        Assert.Contains("netratel-link-status", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        api.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath == "/api/v1/admin/service-links" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null);
        var method = typeof(LinksPanel).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)reference.Panel!).HandleEventAsync(
            new EventCallbackWorkItem((Func<Task>)(() => (Task)method.Invoke(reference.Panel, null)!)), null));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.DoesNotContain("netratel-link-status", html);
        Assert.Contains("status is unavailable", VisibleText(html));
    }

    [Fact]
    public async Task Cancelling_a_pending_rotation_cannot_discard_its_one_time_reveal()
    {
        var client = new ClientMetadata(Guid.NewGuid(), "Synthetic manual client", "synthetic-manual", "organization-a", "synthetic-nr", "17",
            ["rateldesk.incident-receipts.read"], ["customer-a"], "active", "manual", false, 1, 1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        var replacement = new ClientReveal(client with { CredentialRevision = 2 }, "synthetic-unit-replacement", "https://rd.example.test",
            "https://rd.example.test/connect/token", "rateldesk.service", client.Scopes, "Synthetic fixture only");
        using var api = new ConsentApi(Status(withSummary: false)) { Clients = [client] };
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new M2MReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<M2MHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(M2MHarness.Reference)] = reference })));
        var panel = reference.Panel!;
        Task EventAsync(string method, params object[] values)
        {
            var target = typeof(M2MPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)panel).HandleEventAsync(new EventCallbackWorkItem(
                (Func<Task>)(() => target.Invoke(panel, values) as Task ?? Task.CompletedTask)), null));
        }
        await renderer.Dispatcher.InvokeAsync(() => typeof(M2MPanel).GetField("peerWebBaseUrl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, "https://nr.example.test"));
        await EventAsync("OrganizationChanged", "organization-a");
        await EventAsync("CustomerChanged", "customer-a");
        async Task<string> ContinueMarkupAsync() => Assert.Single(Regex.Matches(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString),
            @"<button\b[^>]*>[\s\S]*?</button>").Cast<Match>(), match => match.Value.Contains("Continue to NetRatel", StringComparison.Ordinal)).Value;
        Assert.DoesNotContain(" disabled", await ContinueMarkupAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Intercept = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/rotate", request.RequestUri!.AbsolutePath); entered.TrySetResult(); return response.Task;
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await EventAsync("PrepareClientAction", client.Id, "rotate");
        var rotation = EventAsync("ClientActionAsync", client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Contains(" disabled", await ContinueMarkupAsync());
            await EventAsync("CancelPendingAction");
        }
        finally { response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(replacement) }); }
        await rotation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("netratel-secret-reveal", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        var actual = Assert.IsType<ClientReveal>(typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
        Assert.Equal(replacement.ClientSecret, actual.ClientSecret);
        Assert.Equal(client.Id, actual.Client.Id);
        Assert.Equal(2, actual.Client.CredentialRevision);
        await EventAsync("ShowGuided");
        await EventAsync("ShowManual");
        await EventAsync("PrepareClientAction", client.Id, "revoke");
        await EventAsync("ClientActionAsync", client);
        Assert.Same(actual, typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
        await EventAsync("ClearReveal", actual);
        Assert.Null(typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));

        var secondReplacement = replacement with
        {
            Client = actual.Client with { CredentialRevision = 3 },
            ClientSecret = "synthetic-unit-second-replacement"
        };
        api.Clients = [actual.Client];
        await EventAsync("RefreshClientsAsync");
        var secondIssuances = 0;
        api.Intercept = async (request, ct) =>
        {
            if (request.Method != HttpMethod.Post) return null;
            Assert.EndsWith("/rotate", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(2, body.GetProperty("expectedCredentialRevision").GetInt64());
            ++secondIssuances;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(secondReplacement) };
        };
        await EventAsync("PrepareClientAction", actual.Client.Id, "rotate");
        await EventAsync("ClientActionAsync", actual.Client).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, secondIssuances);
        var secondActual = Assert.IsType<ClientReveal>(typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
        Assert.Equal(secondReplacement.ClientSecret, secondActual.ClientSecret);
        Assert.Equal(client.Id, secondActual.Client.Id);
        Assert.Equal(3, secondActual.Client.CredentialRevision);

        await EventAsync("ClearReveal", actual);
        Assert.Same(secondActual, typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
        Assert.Contains("netratel-secret-reveal", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        await EventAsync("ClearReveal", secondActual);
        Assert.Null(typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
        Assert.DoesNotContain("netratel-secret-reveal", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rotate")]
    public async Task Disposed_manual_issuance_cannot_restore_its_secret_or_run_queued_actions(string action)
    {
        var producer = Guid.Parse("00000000-0000-0000-0000-000000000117");
        var client = new ClientMetadata(Guid.NewGuid(), "Synthetic disposal client", "synthetic-disposal", "organization-a", "synthetic-nr", "17",
            ["rateldesk.incident-receipts.read"], ["customer-a"], "active", "manual", false, 1, 1, producer, Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        var issued = new ClientReveal(client with { CredentialRevision = action == "rotate" ? 2 : 1 }, "synthetic-unit-disposal-secret",
            "https://rd.example.test", "https://rd.example.test/connect/token", "rateldesk.service", client.Scopes, "Synthetic fixture only");
        using var api = new ConsentApi(Status(withSummary: false)) { Clients = action == "rotate" ? [client] : [] };
        var selectors = new PermissionSelectorCapture();
        await using var services = Services(api, responder: false, selectors);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new M2MReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<M2MHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(M2MHarness.Reference)] = reference })));
        var panel = reference.Panel!;
        Task EventAsync(string method, params object[] values)
        {
            var target = typeof(M2MPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)panel).HandleEventAsync(new EventCallbackWorkItem(
                (Func<Task>)(() => target.Invoke(panel, values) as Task ?? Task.CompletedTask)), null));
        }
        if (action == "create")
        {
            await EventAsync("ShowManual");
            await EventAsync("OrganizationChanged", "organization-a");
            await EventAsync("CustomerChanged", "customer-a");
            await renderer.Dispatcher.InvokeAsync(() => selectors.Current.SelectedValuesChanged.InvokeAsync(client.Scopes));
            await renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)panel).HandleEventAsync(new EventCallbackWorkItem((Action)(() =>
            {
                foreach (var draft in new Dictionary<string, string>
                {
                    ["name"] = client.Name, ["peerInstanceId"] = client.PeerInstanceId,
                    ["peerTenantId"] = client.PeerTenantId, ["sourceInstanceId"] = producer.ToString("D")
                })
                    typeof(M2MPanel).GetField(draft.Key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, draft.Value);
            })), null));
        }
        else await EventAsync("PrepareClientAction", client.Id, "rotate");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        api.Intercept = async (request, ct) =>
        {
            if (request.Method != HttpMethod.Post) return null;
            ++posts;
            Assert.Equal(action == "create" ? "/api/v1/admin/service-clients" : $"/api/v1/admin/service-clients/{client.Id:D}/rotate", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (action == "create")
            {
                Assert.Equal(client.Name, body.GetProperty("name").GetString());
                Assert.Equal(client.OrganizationId, body.GetProperty("organizationId").GetString());
                Assert.Equal(client.PeerInstanceId, body.GetProperty("peerInstanceId").GetString());
                Assert.Equal(client.PeerTenantId, body.GetProperty("peerTenantId").GetString());
                Assert.Equal(producer.ToString("D"), body.GetProperty("sourceInstanceId").GetString());
                Assert.Equal(client.Scopes, body.GetProperty("scopes").EnumerateArray().Select(value => Assert.IsType<string>(value.GetString())).ToArray());
                Assert.Equal(client.CustomerIds, body.GetProperty("customerIds").EnumerateArray().Select(value => Assert.IsType<string>(value.GetString())).ToArray());
            }
            else Assert.Equal(client.CredentialRevision, body.GetProperty("expectedCredentialRevision").GetInt64());
            entered.TrySetResult();
            return await response.Task;
        };
        var issuance = action == "create" ? EventAsync("CreateManualAsync") : EventAsync("ClientActionAsync", client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await renderer.Dispatcher.InvokeAsync(panel.Dispose);
            Assert.Null(typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
            Assert.Null(typeof(M2MPanel).GetField("pendingActionClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
            await EventAsync("ShowGuided");
            await EventAsync("ShowManual");
            await EventAsync("CancelPendingAction");
            await EventAsync("PrepareClientAction", client.Id, "revoke");
            await EventAsync("ClientActionAsync", client);
            await EventAsync("CreateManualAsync");
            Assert.Equal(1, posts);
        }
        finally { response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(issued) }); }
        await issuance.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
        Assert.DoesNotContain("netratel-secret-reveal", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        await EventAsync("PrepareClientAction", client.Id, "rotate");
        await EventAsync("ClientActionAsync", client);
        await EventAsync("CreateManualAsync");
        await EventAsync("ClearReveal", issued);
        Assert.Equal(1, posts);
        Assert.Null(typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
        Assert.Null(typeof(M2MPanel).GetField("pendingActionClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
    }

    [Fact]
    public async Task Retired_guided_and_manual_permission_selectors_cannot_change_the_current_manual_grant()
    {
        using var api = new ConsentApi(Status(withSummary: false));
        var selectors = new PermissionSelectorCapture();
        await using var services = Services(api, responder: false, selectors);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new M2MReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<M2MHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(M2MHarness.Reference)] = reference })));
        var panel = reference.Panel!;
        var guidedSelector = selectors.Current;
        var retiredGuidedSelection = guidedSelector.SelectedValuesChanged;

        await M2MEventAsync(renderer, panel, "ShowManual");
        await SetValidManualDraftAsync(renderer, panel);
        var manualSelector = selectors.Current;
        var retiredManualSelection = manualSelector.SelectedValuesChanged;
        Assert.NotSame(guidedSelector, manualSelector);
        Assert.Equal(ManualIncidentScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", ManualCreateButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));

        await renderer.Dispatcher.InvokeAsync(() => retiredGuidedSelection.InvokeAsync(CallbackScopes));
        Assert.Equal(ManualIncidentScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", ManualCreateButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));
        Assert.Equal("Synthetic manual grant", DraftField(panel, "name"));
        Assert.Equal("synthetic-nr", DraftField(panel, "peerInstanceId"));
        Assert.Equal("17", DraftField(panel, "peerTenantId"));
        Assert.Equal("00000000-0000-0000-0000-000000000117", DraftField(panel, "sourceInstanceId"));

        await M2MEventAsync(renderer, panel, "ShowGuided");
        await M2MEventAsync(renderer, panel, "ShowManual");
        Assert.NotSame(manualSelector, selectors.Current);
        await renderer.Dispatcher.InvokeAsync(() => retiredManualSelection.InvokeAsync(CallbackScopes));
        Assert.Equal(ManualIncidentScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", ManualCreateButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));
    }

    [Fact]
    public async Task Retired_manual_and_guided_permission_selectors_cannot_remove_the_current_guided_callback_grant()
    {
        using var api = new ConsentApi(Status(withSummary: false));
        var selectors = new PermissionSelectorCapture();
        await using var services = Services(api, responder: false, selectors);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new M2MReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<M2MHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(M2MHarness.Reference)] = reference })));
        var panel = reference.Panel!;
        await M2MEventAsync(renderer, panel, "ShowManual");
        await SetValidManualDraftAsync(renderer, panel);
        var retiredManualSelection = selectors.Current.SelectedValuesChanged;
        await M2MEventAsync(renderer, panel, "ShowGuided");
        var guidedSelector = selectors.Current;
        var retiredGuidedSelection = guidedSelector.SelectedValuesChanged;

        await renderer.Dispatcher.InvokeAsync(() => retiredManualSelection.InvokeAsync(ManualIncidentScopes));
        Assert.Equal(CallbackScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", GuidedContinueButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));

        await M2MEventAsync(renderer, panel, "ShowManual");
        await M2MEventAsync(renderer, panel, "ShowGuided");
        Assert.NotSame(guidedSelector, selectors.Current);
        await renderer.Dispatcher.InvokeAsync(() => retiredGuidedSelection.InvokeAsync(ManualIncidentScopes));
        Assert.Equal(CallbackScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", GuidedContinueButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));
    }

    [Fact]
    public async Task Current_manual_permission_selector_preserves_chosen_callbacks_and_requires_recorded_request_and_task_ids()
    {
        using var api = new ConsentApi(Status(withSummary: false));
        var selectors = new PermissionSelectorCapture();
        await using var services = Services(api, responder: false, selectors);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new M2MReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<M2MHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(M2MHarness.Reference)] = reference })));
        var panel = reference.Panel!;
        await M2MEventAsync(renderer, panel, "ShowManual");
        await SetValidManualDraftAsync(renderer, panel);
        var manualSelector = selectors.Current;
        await renderer.Dispatcher.InvokeAsync(() => manualSelector.SelectedValuesChanged.InvokeAsync(CallbackScopes));
        Assert.Equal(CallbackScopes, CurrentScopes(panel));
        var callbackHtml = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Approved callback request IDs", VisibleText(callbackHtml));
        Assert.Contains("Approved callback task IDs", VisibleText(callbackHtml));
        Assert.Contains(" disabled", ManualCreateButton(callbackHtml));

        await M2MEventAsync(renderer, panel, "ShowManual");
        Assert.Same(manualSelector, selectors.Current);
        Assert.Equal(CallbackScopes, CurrentScopes(panel));
        Assert.Contains(" disabled", ManualCreateButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));
        await SetM2MDraftAsync(renderer, panel, new()
        {
            ["callbackRequestIds"] = "00000000-0000-0000-0000-000000000221"
        });
        Assert.Contains(" disabled", ManualCreateButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));
        await SetM2MDraftAsync(renderer, panel, new()
        {
            ["callbackTaskIds"] = "00000000-0000-0000-0000-000000000222"
        });
        Assert.Equal(CallbackScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", ManualCreateButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));
    }

    private static readonly string[] ManualIncidentScopes =
        ["rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.incidents.create"];
    private static string[] CallbackScopes => [.. ManualIncidentScopes, "rateldesk.orchestration.callback"];
    private static string[] CurrentScopes(M2MPanel panel) => ((IEnumerable<string>)typeof(M2MPanel)
        .GetField("scopes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!).Order(StringComparer.Ordinal).ToArray();
    private static string DraftField(M2MPanel panel, string name) => (string)typeof(M2MPanel)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
    private static string ManualCreateButton(string html) => ButtonWithText(html, "Create and reveal service client");
    private static string GuidedContinueButton(string html) => ButtonWithText(html, "Continue to NetRatel");
    private static string ButtonWithText(string html, string text) => Assert.Single(Regex.Matches(html,
        @"<button\b[^>]*>[\s\S]*?</button>").Cast<Match>(), match => match.Value.Contains(text, StringComparison.Ordinal)).Value;
    private static Task M2MEventAsync(ConsentRenderer renderer, M2MPanel panel, string method, params object[] values)
    {
        var target = typeof(M2MPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)panel).HandleEventAsync(new EventCallbackWorkItem(
            (Func<Task>)(() => target.Invoke(panel, values) as Task ?? Task.CompletedTask)), null));
    }
    private static async Task SetValidManualDraftAsync(ConsentRenderer renderer, M2MPanel panel)
    {
        await M2MEventAsync(renderer, panel, "OrganizationChanged", "organization-a");
        await M2MEventAsync(renderer, panel, "CustomerChanged", "customer-a");
        await SetM2MDraftAsync(renderer, panel, new()
        {
            ["name"] = "Synthetic manual grant", ["peerInstanceId"] = "synthetic-nr", ["peerTenantId"] = "17",
            ["sourceInstanceId"] = "00000000-0000-0000-0000-000000000117", ["peerWebBaseUrl"] = "https://nr.example.test"
        });
    }
    private static Task SetM2MDraftAsync(ConsentRenderer renderer, M2MPanel panel, Dictionary<string, string> values) =>
        renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)panel).HandleEventAsync(new EventCallbackWorkItem((Action)(() =>
        {
            foreach (var value in values)
                typeof(M2MPanel).GetField(value.Key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, value.Value);
        })), null));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Initiator_proposal_and_stored_summaries_retain_their_exact_grant_details(bool withSummary, bool responder)
    {
        using var api = new ConsentApi(Status(withSummary));
        await using var services = Services(api, responder);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        await ChangeAsync(renderer, reference.Page!, "OrganizationChanged", "organization-b");
        await ChangeAsync(renderer, reference.Page!, "CustomerChanged", "customer-b");
        await ChangeAsync(renderer, reference.Page!, "ScopesChanged", new[] { "rateldesk.incident-receipts.read" });
        var text = VisibleText(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        Assert.Contains("Organization: organization-approved", text);
        Assert.Contains("Customers: customer-approved", text);
        Assert.Contains("Resources: resource-pinned", text);
        Assert.Contains("netratel.orchestration.invoke", text);
        Assert.DoesNotContain("Organization: organization-b", text);
        Assert.DoesNotContain("Customers: customer-b", text);
    }

    private static Task ChangeAsync(ConsentRenderer renderer, ConsentPage page, string method, object value)
    {
        var handler = typeof(ConsentPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = (Action)(() => handler.Invoke(page, [value]));
        return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem(callback), null));
    }

    private static string VisibleText(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ");
    private static string ConfirmationInput(string html) => Assert.Single(Regex.Matches(html, "<input\\b[^>]*name=\"confirmed\"[^>]*>").Cast<Match>()).Value;
    private static string[] ScopeParagraphs(string html) => Regex.Matches(html, @"<p\b[^>]*>(.*?)</p>", RegexOptions.Singleline)
        .Select(match => VisibleText(match.Groups[1].Value).Trim()).Where(text => text.StartsWith("Scopes: ", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    private static ServiceLinkAdminStatus Status(bool withSummary)
    {
        ServiceLinkGrant[] grants =
        [
            new() { DirectionId = "initiator_to_responder", CallerProduct = "netratel", CallerTenantId = "17", TargetProduct = "rateldesk", TargetTenantId = "organization-approved",
                Scopes = ["rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.orchestration.callback"],
                ResourceConstraints = new() { OrganizationId = "organization-approved", CustomerIds = ["customer-approved"] } },
            new() { DirectionId = "responder_to_initiator", CallerProduct = "rateldesk", CallerTenantId = "organization-approved", TargetProduct = "netratel", TargetTenantId = "17",
                Scopes = ["netratel.orchestration.read", "netratel.orchestration.invoke"],
                ResourceConstraints = new() { TenantId = "17", ResourceIds = ["resource-pinned"], RequestDefinitionIds = ["definition-pinned"] } }
        ];
        var descriptor = new ServiceLinkRequestDescriptor
        {
            AttemptId = "attempt-ui", RequestedGrants = grants,
            InitiatorEndpointSnapshot = new() { Product = "netratel", WebBaseUrl = "https://nr.example.test", InstanceId = "nr-install" },
            ResponderEndpointSnapshot = new() { Product = "rateldesk", WebBaseUrl = "https://rd.example.test", InstanceId = "rd-install" }
        };
        return new("attempt-ui", withSummary ? "link-ui" : null, 1, withSummary ? "approved" : "awaiting_approval", "organization-approved", "nr-install", "17",
            "undecided", null, null, descriptor, withSummary ? new() { Grants = grants } : null, false, false, false, false, false, null, false, []);
    }

    private static ServiceProvider Services(ConsentApi api, bool responder, PermissionSelectorCapture? selectors = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IHttpClientFactory>(api);
        services.AddSingleton<NavigationManager>(new ConsentNavigation(responder));
        services.AddSingleton(Substitute.For<IJSRuntime>());
        services.AddSingleton<AntiforgeryStateProvider>(new UnitAntiforgeryState());
        if (selectors is not null) services.AddSingleton<IComponentActivator>(selectors);
        return services.BuildServiceProvider();
    }

    private sealed class ConsentApi(ServiceLinkAdminStatus status) : HttpMessageHandler, IHttpClientFactory
    {
        public ServiceLinkAdminStatus Status { get; } = status;
        public ServiceLinkAdminStatus[] Links { get; set; } = [];
        public ClientMetadata[] Clients { get; set; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }
        public ServiceLinkAdminStatus ForAttempt(string attempt) => Status with { AttemptId = attempt, Descriptor = Status.Descriptor with { AttemptId = attempt } };
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://rd.example.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Intercept is not null && await Intercept(request, ct) is { } intercepted) return intercepted;
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/v1/admin/service-links/attempts/", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(ForAttempt(Uri.UnescapeDataString(request.RequestUri.Segments[^1]))) };
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/admin/service-links" => Links,
                "/api/v1/admin/service-clients" => Clients,
                "/api/v1/organizations" => new[] { new OrganizationDto { Id = "organization-a", Name = "First", IsEnabled = true }, new OrganizationDto { Id = "organization-b", Name = "Selected", IsEnabled = true } },
                "/api/v1/customers" => new[] { new CustomerDto { Id = "customer-a", OrganizationId = "organization-a", Name = "First", IsEnabled = true }, new CustomerDto { Id = "customer-b", OrganizationId = "organization-b", Name = "Selected", IsEnabled = true } },
                _ => throw new InvalidOperationException("Unexpected unit-fixture API path.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) };
        }
    }
    private sealed class ConsentNavigation : NavigationManager
    {
        public ConsentNavigation(bool responder) => Initialize("https://rd.example.test/", "https://rd.example.test/account/integration-credentials/link/" + (responder ? "respond" : "review") + "/attempt-ui");
        public void SetRoute(string attempt) => Uri = BaseUri + "account/integration-credentials/link/review/" + attempt;
        public void SetResultRoute() => Uri = BaseUri + "account/integration-credentials/link/result?status=not-authorized";
    }
    private sealed class UnitAntiforgeryState : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new("synthetic-unit-token", "__RequestVerificationToken");
    }
    private sealed class ConsentRenderer(IServiceProvider services, ILoggerFactory loggers) : StaticHtmlRenderer(services, loggers)
    {
        protected override IComponent ResolveComponentForRenderMode(Type type, int? parent, IComponentActivator activator, IComponentRenderMode mode) => activator.CreateInstance(type);
        public async Task<HtmlRootComponent> RenderAsync<T>(ParameterView parameters) where T : IComponent
        {
            var root = BeginRenderingComponent(typeof(T), parameters);
            await root.QuiescenceTask;
            return root;
        }
    }
    private sealed class PermissionSelectorCapture : IComponentActivator
    {
        private readonly List<MudSelect<string>> selectors = [];
        public MudSelect<string> Current => selectors.Last(selector => selector.Label == "NetRatel → RatelDesk permissions");
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is MudSelect<string> selector) selectors.Add(selector);
            return component;
        }
    }
    public sealed class PageReference { public ConsentPage? Page { get; set; } }
    public sealed class LinksReference { public LinksPanel? Panel { get; set; } }
    public sealed class M2MReference { public M2MPanel? Panel { get; set; } }
    public sealed class M2MHarness : ComponentBase
    {
        [Parameter] public M2MReference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<M2MPanel>(0);
            builder.AddComponentReferenceCapture(1, component => Reference.Panel = (M2MPanel)component);
            builder.CloseComponent();
        }
    }
    public sealed class LinksHarness : ComponentBase
    {
        [Parameter] public LinksReference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<LinksPanel>(0);
            builder.AddComponentReferenceCapture(1, component => Reference.Panel = (LinksPanel)component);
            builder.CloseComponent();
        }
    }
    public sealed class PageHarness : ComponentBase
    {
        [Parameter] public PageReference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ConsentPage>(0);
            builder.AddAttribute(1, nameof(ConsentPage.AttemptId), "attempt-ui");
            builder.AddComponentReferenceCapture(2, component => Reference.Page = (ConsentPage)component);
            builder.CloseComponent();
        }
    }
}
