extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.ServiceLink;
using Helpdesk.Shared.ServiceIdentity;
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
        Assert.Contains("Customers: customer-b", VisibleText(changedOrganization));
        Assert.DoesNotContain("Customers: customer-a", VisibleText(changedOrganization));
        await ChangeAsync(renderer, page, "CustomerChanged", "");
        Assert.Contains("Customers: None granted", VisibleText(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));

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
        Assert.Contains("current account cannot perform", VisibleText(html));
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
        Assert.Contains("current account cannot perform", VisibleText(html));
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
        Assert.Contains("current account cannot perform", VisibleText(html));
    }

    [Theory]
    [InlineData("initiator", "awaiting_approval", "continue", false, "netratel-resume-approval")]
    [InlineData("responder", "awaiting_approval", "respond", false, "Resume approval")]
    [InlineData("initiator", "approved", "review", true, "Review and approve")]
    [InlineData("responder", "approved", "return", true, "Return to NetRatel")]
    [InlineData("responder", "prepared", "resume", true, "Complete connection")]
    [InlineData("responder", "expired", "none", false, "Reconnect with new approval")]
    public async Task Connection_list_uses_the_authorized_durable_approval_action(string role, string lifecycle, string action, bool summary, string expected)
    {
        var status = Status(summary) with { LocalRole = role, LifecycleState = lifecycle, AvailableAction = action, CanCancel = false, CanStartFresh = lifecycle == "expired" };
        using var api = new ConsentApi(status) { Links = [status] };
        await using var services = Services(api, responder: role == "responder");
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new LinksReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<LinksHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(LinksHarness.Reference)] = reference })));
        if (lifecycle == "expired")
        {
            Assert.DoesNotContain("netratel-link-status", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
            await LinksEventAsync(renderer, reference.Panel!, "ToggleHistory");
        }
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains(expected, html);
        if (action == "respond")
        {
            Assert.Contains("/link/respond/attempt-ui", html);
            Assert.DoesNotContain("/link/review/attempt-ui", html);
        }
        if (action is not ("continue" or "return")) Assert.DoesNotContain("netratel-resume-approval", html);
        if (action != "resume") Assert.DoesNotContain("Complete connection", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_form_stays_focused_and_never_loads_the_connection_list(bool finalReview)
    {
        using var api = new ConsentApi(Status(finalReview)) { Links = [Status(false) with { AttemptId = "unrelated-setup" }] };
        var listReads = 0;
        api.Intercept = (request, _) => { if (request.RequestUri!.AbsolutePath == "/api/v1/admin/service-links") listReads++; return Task.FromResult<HttpResponseMessage?>(null); };
        await using var services = Services(api, responder: !finalReview);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = new PageReference() })));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains(finalReview ? "service-link-final-approval" : "service-link-responder-approval", html);
        Assert.DoesNotContain("netratel-service-link-list", html);
        Assert.DoesNotContain("unrelated-setup", html);
        Assert.Equal(0, listReads);
    }

    [Fact]
    public async Task Current_setups_remain_visible_while_terminal_history_requires_explicit_expansion()
    {
        var current = Status(false);
        var past = current with { AttemptId = "past-setup", LifecycleState = "expired", AvailableAction = "none", CanStartFresh = true, CanCancel = false };
        using var api = new ConsentApi(current) { Links = [past, current] };
        await using var services = Services(api, responder: true);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new LinksReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<LinksHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(LinksHarness.Reference)] = reference })));
        var collapsed = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("data-attempt-id=\"attempt-ui\"", collapsed);
        Assert.DoesNotContain("data-attempt-id=\"past-setup\"", collapsed);
        Assert.Contains("aria-expanded=\"false\"", collapsed);
        await LinksEventAsync(renderer, reference.Panel!, "ToggleHistory");
        var expanded = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("data-attempt-id=\"attempt-ui\"", expanded);
        Assert.Contains("data-attempt-id=\"past-setup\"", expanded);
        Assert.Contains("Reconnect with new approval", expanded);
        await LinksEventAsync(renderer, reference.Panel!, "ToggleHistory");
        Assert.DoesNotContain("data-attempt-id=\"past-setup\"", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
    }

    [Fact]
    public async Task Only_pending_attempts_with_an_exact_established_relationship_move_under_other_setups()
    {
        var established = Status(true);
        var duplicate = Status(false) with { AttemptId = "matching-pending", LocalTenantId = established.LocalTenantId };
        var different = duplicate with { AttemptId = "different-peer-tenant", PeerTenantId = "another-peer-tenant" };
        var unknown = duplicate with { AttemptId = "unknown-peer-tenant", PeerTenantId = null };
        var incomplete = duplicate with { AttemptId = "active-without-summary", LifecycleState = "active" };
        using var api = new ConsentApi(established) { Links = [duplicate, different, unknown, incomplete, established] };
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new LinksReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<LinksHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(LinksHarness.Reference)] = reference })));
        var collapsed = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("data-attempt-id=\"attempt-ui\"", collapsed);
        Assert.Contains("data-attempt-id=\"different-peer-tenant\"", collapsed);
        Assert.Contains("data-attempt-id=\"unknown-peer-tenant\"", collapsed);
        Assert.Contains("data-attempt-id=\"active-without-summary\"", collapsed);
        Assert.DoesNotContain("data-attempt-id=\"matching-pending\"", collapsed);
        Assert.Contains("Other setups for these connections (1)", VisibleText(collapsed));
        await LinksEventAsync(renderer, reference.Panel!, "ToggleOtherSetups");
        var expanded = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("data-attempt-id=\"matching-pending\"", expanded);
        Assert.Contains("/link/respond/matching-pending", expanded);
        Assert.Contains("Cancel setup", VisibleText(expanded));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_result_offers_only_the_authorized_selected_setup_action(bool authorized)
    {
        var stored = Status(true) with { LocalRole = "responder", AvailableAction = "return" };
        using var api = new ConsentApi(stored) { Links = [stored with { AttemptId = "unrelated-history" }] };
        var listReads = 0;
        api.Intercept = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/admin/service-links") listReads++;
            return Task.FromResult<HttpResponseMessage?>(!authorized && request.RequestUri.AbsolutePath.Contains("/attempts/", StringComparison.Ordinal)
                ? new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { code = "not-authorized" }) } : null);
        };
        await using var services = Services(api, responder: false);
        ((ConsentNavigation)services.GetRequiredService<NavigationManager>()).SetResultRoute();
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(ConsentPage).GetProperty(nameof(ConsentPage.Status))!.SetValue(reference.Page, "relationship-already-exists");
            typeof(ConsentPage).GetProperty(nameof(ConsentPage.Stage))!.SetValue(reference.Page, "remote-approve");
            typeof(ConsentPage).GetProperty(nameof(ConsentPage.ResultAttemptId))!.SetValue(reference.Page, "attempt-ui");
            return reference.Page!.SetParametersAsync(ParameterView.Empty);
        });
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Equal(authorized, html.Contains("Return to NetRatel", StringComparison.Ordinal));
        Assert.Equal(authorized, html.Contains("netratel-resume-approval", StringComparison.Ordinal));
        Assert.DoesNotContain("unrelated-history", html);
        Assert.DoesNotContain("Correct setup and retry", html);
        Assert.Equal(0, listReads);
        if (!authorized) Assert.Contains("current account cannot perform", VisibleText(html));
    }

    [Fact]
    public async Task Connection_test_results_and_retry_errors_stay_with_the_selected_row()
    {
        var first = Status(true) with { LifecycleState = "active", Decision = "commit", LocalInboundActive = true, PeerActiveAcknowledged = true, LocalBusinessSenderEnabled = true, AvailableAction = "none" };
        var second = first with { AttemptId = "second-attempt", LinkId = "second-link" };
        using var api = new ConsentApi(first) { Links = [first, second] };
        var failFirst = false;
        api.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Method == HttpMethod.Post
            ? failFirst && request.RequestUri!.AbsolutePath.Contains("/link-ui/", StringComparison.Ordinal)
                ? new(HttpStatusCode.BadGateway) { Content = JsonContent.Create(new { code = "peer-unavailable", stage = "test" }) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServiceLinkTestResult(true, true, true, null)) } : null);
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new LinksReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<LinksHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(LinksHarness.Reference)] = reference })));
        var firstMarkup = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Equal(2, Regex.Matches(firstMarkup, "data-testid=\"netratel-connection-test-result\"").Count);
        failFirst = true;
        await LinksEventAsync(renderer, reference.Panel!, "TestAsync", first);
        var retried = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        var firstStart = retried.IndexOf("data-attempt-id=\"attempt-ui\"", StringComparison.Ordinal);
        var secondStart = retried.IndexOf("data-attempt-id=\"second-attempt\"", StringComparison.Ordinal);
        var firstRow = retried[firstStart..secondStart];
        var secondRow = retried[secondStart..];
        Assert.Contains("service-link-operation-error", firstRow);
        Assert.Contains("peer could not complete", VisibleText(firstRow));
        Assert.DoesNotContain("netratel-connection-test-result", firstRow);
        Assert.DoesNotContain("service-link-operation-error", secondRow);
        Assert.Contains("Connection verified. Incident delivery is ready.", VisibleText(secondRow));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Progressing_status_becomes_active_and_verifies_once_without_replaying_operations(bool focused)
    {
        var pending = Status(true) with { AvailableAction = "resume" };
        var active = pending with { LifecycleState = "active", Decision = "commit", LocalInboundActive = true, PeerActiveAcknowledged = true, LocalBusinessSenderEnabled = true, AvailableAction = "none", LinkRevision = 2 };
        using var api = new ConsentApi(pending) { Links = [pending] };
        var gets = 0;
        var posts = 0;
        api.Intercept = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/v1/admin/service-links/links/link-ui/test", request.RequestUri!.AbsolutePath);
                Interlocked.Increment(ref posts);
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServiceLinkTestResult(true, true, true, null)) });
            }
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/v1/admin/service-links", StringComparison.Ordinal))
            {
                var current = Interlocked.Increment(ref gets) > 1 ? active : pending;
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = focused ? JsonContent.Create(current) : JsonContent.Create(new[] { current, pending with { AttemptId = "other-pending", LinkId = null } }) });
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new LinksReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<LinksHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(LinksHarness.Reference)] = reference, [nameof(LinksHarness.AttemptId)] = focused ? "attempt-ui" : null })));
        Assert.Contains("Complete connection", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        var observed = await WaitForHtmlAsync(renderer, view, html => html.Contains("netratel-connection-test-result", StringComparison.Ordinal));
        if (focused) Assert.DoesNotContain("Complete connection", observed);
        Assert.DoesNotContain("service-link-final-approval", observed);
        Assert.Contains("Connection verified. Incident delivery is ready.", VisibleText(observed));
        if (focused) await Task.Delay(TimeSpan.FromSeconds(2.2));
        else await WaitForHtmlAsync(renderer, view, _ => Volatile.Read(ref gets) >= 3);
        Assert.Equal(focused ? 2 : 3, gets);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task Disposing_progress_observation_cancels_an_inflight_status_request()
    {
        var pending = Status(true) with { AvailableAction = "resume" };
        using var api = new ConsentApi(pending) { Links = [pending] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        api.Intercept = async (request, cancellation) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/admin/service-links" && Interlocked.Increment(ref reads) > 1)
            {
                using var registration = cancellation.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            return null;
        };
        await using var services = Services(api, responder: false);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new LinksReference();
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<LinksHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(LinksHarness.Reference)] = reference })));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.Dispatcher.InvokeAsync(reference.Panel!.Dispose);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_observation_preserves_unchanged_drafts_and_verifies_once_after_approval_advances(bool responder)
    {
        var pending = Status(!responder);
        var active = Status(true) with { LifecycleState = "active", Decision = "commit", LocalInboundActive = true, PeerActiveAcknowledged = true, LocalBusinessSenderEnabled = true, AvailableAction = "none", LinkRevision = 2, LocalRole = responder ? "responder" : "initiator" };
        using var api = new ConsentApi(pending);
        var reads = 0;
        var posts = 0;
        var advance = false;
        api.Intercept = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/v1/admin/service-links/links/link-ui/test", request.RequestUri!.AbsolutePath);
                Interlocked.Increment(ref posts);
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServiceLinkTestResult(true, true, true, null)) });
            }
            if (request.RequestUri!.AbsolutePath.Contains("/attempts/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = JsonContent.Create(advance ? active : pending) });
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await using var services = Services(api, responder);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        if (responder)
        {
            await ChangeAsync(renderer, reference.Page!, "OrganizationChanged", "organization-b");
            await ChangeAsync(renderer, reference.Page!, "CustomerChanged", "customer-b");
            await ChangeAsync(renderer, reference.Page!, "ScopesChanged", new[] { "rateldesk.incident-receipts.read" });
        }
        var confirmation = typeof(ConsentPage).GetField("confirmed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)reference.Page!).HandleEventAsync(
            new EventCallbackWorkItem((Action)(() => confirmation.SetValue(reference.Page, true))), null));
        var unchanged = await WaitForHtmlAsync(renderer, view, _ => Volatile.Read(ref reads) >= 2);
        Assert.Matches(@"\bchecked(?:=|\s|>)", ConfirmationInput(unchanged));
        if (responder)
        {
            Assert.Contains("Customers: customer-b", VisibleText(unchanged));
            Assert.Contains("value=\"organization-b\"", unchanged);
            Assert.Contains("Scopes: rateldesk.incident-receipts.read", VisibleText(unchanged));
        }
        advance = true;
        var observed = await WaitForHtmlAsync(renderer, view, html => html.Contains("netratel-connection-test-result", StringComparison.Ordinal));
        Assert.DoesNotContain("service-link-responder-approval", observed);
        Assert.DoesNotContain("service-link-final-approval", observed);
        Assert.False((bool)confirmation.GetValue(reference.Page)!);
        Assert.Equal(1, posts);
    }

    private static async Task<string> WaitForHtmlAsync(ConsentRenderer renderer, HtmlRootComponent view, Func<string, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        while (true)
        {
            var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
            if (predicate(html)) return html;
            await Task.Delay(25, timeout.Token);
        }
    }

    private static Task LinksEventAsync(ConsentRenderer renderer, LinksPanel panel, string method, params object[] values)
    {
        var handler = typeof(LinksPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)panel).HandleEventAsync(new EventCallbackWorkItem(
            (Func<Task>)(() => handler.Invoke(panel, values) as Task ?? Task.CompletedTask)), null));
    }

    [Theory]
    [InlineData("initiator", "continue", false)]
    [InlineData("initiator", "continue", true)]
    [InlineData("responder", "respond", false)]
    [InlineData("responder", "respond", true)]
    [InlineData("responder", "none", true)]
    public async Task Route_alone_cannot_offer_consent_and_no_summary_never_claims_saved_approval(string role, string action, bool responderRoute)
    {
        using var api = new ConsentApi(Status(false) with { LocalRole = role, AvailableAction = action });
        await using var services = Services(api, responderRoute);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = new PageReference() })));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Equal(role == "responder" && action == "respond" && responderRoute, html.Contains("service-link-responder-approval", StringComparison.Ordinal));
        Assert.DoesNotContain("Approval is saved", VisibleText(html));
        if (role != "responder" || action != "respond" || !responderRoute) Assert.Contains("No approval is saved", VisibleText(html));
        Assert.DoesNotContain("service-link-final-approval", html);
    }

    [Fact]
    public async Task Malformed_terminal_setup_offers_fresh_selection_without_reusing_the_invalid_organization()
    {
        var status = Status(false) with { LocalTenantId = "invalid-immutable-selection", LifecycleState = "expired", AvailableAction = "none", OrganizationBindingInvalid = true, CanCancel = true, CanStartFresh = true };
        using var api = new ConsentApi(status) { Links = [status] };
        await using var services = Services(api, responder: true);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = new PageReference() })));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Cancel setup", VisibleText(html));
        Assert.Contains("Reconnect with new approval", VisibleText(html));
        Assert.Contains("organization_id=&amp;customer_id=", html);
        Assert.DoesNotContain("service-link-responder-approval", html);
        Assert.DoesNotContain("netratel-resume-approval", html);
        Assert.DoesNotContain("Resume connection checks", html);
        Assert.DoesNotContain("organization_id=invalid-immutable-selection", html);
        Assert.DoesNotContain("Approval is saved", html);
    }

    [Theory]
    [InlineData("network-policy-rejected", "network policy")]
    [InlineData("unsupported-peer", "compatible peer")]
    [InlineData("organization-disabled", "enabled organization")]
    public async Task Protected_status_failures_keep_safe_actionable_diagnostics(string code, string guidance)
    {
        using var api = new ConsentApi(Status(false));
        api.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.Contains("/attempts/", StringComparison.Ordinal)
            ? new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = code, stage = "remote-review", correlationId = "0123456789abcdef0123456789abcdef", detail = "sensitive-peer-response" }) } : null);
        await using var services = Services(api, responder: true);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = new PageReference() })));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains(guidance, VisibleText(html));
        Assert.Contains("Stage: remote-review", VisibleText(html));
        Assert.Contains("0123456789abcdef0123456789abcdef", VisibleText(html));
        Assert.DoesNotContain("sensitive-peer-response", html);
        Assert.DoesNotContain("service-link-responder-approval", html);
        Assert.DoesNotContain("netratel-resume-approval", html);
    }

    [Fact]
    public async Task Pre_attempt_failure_offers_input_correction_without_resume_or_untrusted_diagnostics()
    {
        using var api = new ConsentApi(Status(false));
        await using var services = Services(api, responder: false);
        var navigation = (ConsentNavigation)services.GetRequiredService<NavigationManager>();
        navigation.SetResultRoute();
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new PageReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<PageHarness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(PageHarness.Reference)] = reference })));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(ConsentPage).GetProperty(nameof(ConsentPage.Status))!.SetValue(reference.Page, "https://sensitive-peer.invalid/error");
            typeof(ConsentPage).GetProperty(nameof(ConsentPage.Stage))!.SetValue(reference.Page, "start");
            typeof(ConsentPage).GetProperty(nameof(ConsentPage.CorrelationId))!.SetValue(reference.Page, "sensitive-session-proof");
            return reference.Page!.SetParametersAsync(ParameterView.Empty);
        });
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Correct setup and retry", VisibleText(html));
        Assert.Contains("View notifications", VisibleText(html));
        Assert.DoesNotContain("sensitive-", html);
        Assert.DoesNotContain("Resume", VisibleText(html));
        Assert.DoesNotContain("service-link-final-approval", html);
    }

    [Fact]
    public async Task Cancelling_a_pending_rotation_cannot_discard_its_one_time_reveal()
    {
        var client = new ClientMetadata(Guid.NewGuid(), "Synthetic manual client", "synthetic-manual", "organization-a", "synthetic-nr", "17",
            ["rateldesk.incident-receipts.read"], ["customer-a"], "active", "manual", false, 1, 1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        var replacement = new ClientReveal(client with { CredentialRevision = 2 }, "synthetic-unit-replacement", "https://rd.example.test",
            "https://rd.example.test/connect/token", "rateldesk.service", client.Scopes, "Synthetic fixture only");
        using var api = new ConsentApi(Status(withSummary: false)) { Clients = [client] };
        var selectors = new PermissionSelectorCapture();
        await using var services = Services(api, responder: false, selectors);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new M2MReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<M2MHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(M2MHarness.Reference)] = reference })));
        var panel = reference.Panel!;
        var taskSelection = selectors.Tasks.ValueChanged;
        var originalScopes = CurrentScopes(panel);
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
        async Task<string> ContinueMarkupAsync() => GuidedContinueButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
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
            await renderer.Dispatcher.InvokeAsync(() => taskSelection.InvokeAsync(true));
            Assert.Equal(originalScopes, CurrentScopes(panel));
            await EventAsync("CancelPendingAction");
        }
        finally { response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(replacement) }); }
        await rotation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("netratel-secret-reveal", await renderer.Dispatcher.InvokeAsync(view.ToHtmlString));
        var actual = Assert.IsType<ClientReveal>(typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
        Assert.Equal(replacement.ClientSecret, actual.ClientSecret);
        Assert.Equal(client.Id, actual.Client.Id);
        Assert.Equal(2, actual.Client.CredentialRevision);
        await renderer.Dispatcher.InvokeAsync(() => taskSelection.InvokeAsync(true));
        Assert.Equal(originalScopes, CurrentScopes(panel));
        Assert.Same(actual, typeof(M2MPanel).GetField("reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel));
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
        var retiredTaskSelection = selectors.Tasks.ValueChanged;
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
            var originalScopes = CurrentScopes(panel);
            await renderer.Dispatcher.InvokeAsync(panel.Dispose);
            await renderer.Dispatcher.InvokeAsync(() => retiredTaskSelection.InvokeAsync(true));
            Assert.Equal(originalScopes, CurrentScopes(panel));
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
    public async Task Retired_manual_permission_selectors_cannot_change_the_current_manual_grant()
    {
        using var api = new ConsentApi(Status(withSummary: false));
        var selectors = new PermissionSelectorCapture();
        await using var services = Services(api, responder: false, selectors);
        await using var renderer = new ConsentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new M2MReference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<M2MHarness>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(M2MHarness.Reference)] = reference })));
        var panel = reference.Panel!;
        var retiredTaskSelection = selectors.Tasks.ValueChanged;
        await M2MEventAsync(renderer, panel, "ShowManual");
        var retiredSelector = selectors.Current;
        var retiredSelection = retiredSelector.SelectedValuesChanged;
        await M2MEventAsync(renderer, panel, "ShowGuided");
        await M2MEventAsync(renderer, panel, "ShowManual");
        await SetValidManualDraftAsync(renderer, panel);
        var manualSelector = selectors.Current;
        var retiredManualSelection = manualSelector.SelectedValuesChanged;
        Assert.NotSame(retiredSelector, manualSelector);
        Assert.Equal(ManualIncidentScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", ManualCreateButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));

        await renderer.Dispatcher.InvokeAsync(() => retiredTaskSelection.InvokeAsync(true));
        Assert.Equal(ManualIncidentScopes, CurrentScopes(panel));
        await renderer.Dispatcher.InvokeAsync(() => retiredSelection.InvokeAsync(CallbackScopes));
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
    public async Task Retired_manual_permission_selectors_cannot_remove_the_current_guided_callback_grant()
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
        var retiredManualSelection = manualSelector.SelectedValuesChanged;
        await M2MEventAsync(renderer, panel, "ShowGuided");
        Assert.Equal(ManualIncidentScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", GuidedContinueButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));
        var guidedTaskChoice = selectors.Tasks;
        var retiredGuidedTasks = guidedTaskChoice.ValueChanged;
        await renderer.Dispatcher.InvokeAsync(() => guidedTaskChoice.ValueChanged.InvokeAsync(true));

        await renderer.Dispatcher.InvokeAsync(() => retiredManualSelection.InvokeAsync(ManualIncidentScopes));
        Assert.Equal(CallbackScopes, CurrentScopes(panel));
        Assert.DoesNotContain(" disabled", GuidedContinueButton(await renderer.Dispatcher.InvokeAsync(view.ToHtmlString)));

        await M2MEventAsync(renderer, panel, "ShowManual");
        var replacementSelector = selectors.Current;
        var retiredReplacementSelection = replacementSelector.SelectedValuesChanged;
        Assert.NotSame(manualSelector, replacementSelector);
        await M2MEventAsync(renderer, panel, "ShowGuided");
        Assert.NotSame(guidedTaskChoice, selectors.Tasks);
        await renderer.Dispatcher.InvokeAsync(() => selectors.Tasks.ValueChanged.InvokeAsync(true));
        await renderer.Dispatcher.InvokeAsync(() => retiredGuidedTasks.InvokeAsync(false));
        await renderer.Dispatcher.InvokeAsync(() => retiredReplacementSelection.InvokeAsync(ManualIncidentScopes));
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
    private static string GuidedContinueButton(string html) => ButtonWithText(Assert.Single(Regex.Matches(html,
        "<form\\b[^>]*data-testid=\"netratel-link-start\"[^>]*>.*?</form>", RegexOptions.Singleline).Cast<Match>()).Value, "Connect NetRatel");
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
        return new("attempt-ui", withSummary ? "link-ui" : null, 1, withSummary ? "approved" : "awaiting_approval", withSummary ? "organization-approved" : "", "nr-install", "17",
            "undecided", null, null, descriptor, withSummary ? new() { Grants = grants } : null, false, false, false, false, false, null, false, [])
        { LocalRole = withSummary ? "initiator" : "responder", AvailableAction = withSummary ? "review" : "respond", CanCancel = true };
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
                "/api/v1/admin/service-clients/public-settings" => new ServicePublicSettingsDto(true, true,
                    "https://rd.example.test", "https://rd-api.example.test", "https://rd-api.example.test/services",
                    "rateldesk.service", "rd-install", 1, [], []),
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
        private readonly List<MudCheckBox<bool>> taskChoices = [];
        public MudSelect<string> Current => selectors.Last(selector => selector.Label == "NetRatel → RatelDesk permissions");
        public MudCheckBox<bool> Tasks => taskChoices.Last(choice => choice.Label == "Also run approved tasks (optional)");
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is MudSelect<string> selector) selectors.Add(selector);
            if (component is MudCheckBox<bool> choice) taskChoices.Add(choice);
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
        [Parameter] public string? AttemptId { get; set; }
        [Parameter] public LinksReference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<LinksPanel>(0);
            builder.AddAttribute(1, nameof(LinksPanel.AttemptId), AttemptId);
            builder.AddComponentReferenceCapture(2, component => Reference.Panel = (LinksPanel)component);
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
