extern alias NewWeb;
using System.Reflection;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using Editor = NewWeb::HelpDesk.NewWeb.Components.Shared.McpIntegrationPolicyEditor;

namespace Helpdesk.Tests.NewWeb;

public sealed class McpIntegrationPolicyEditorTests
{
    private static readonly McpPolicyOperation[] Operations =
    [
        new("helpdesk_incidents.get", "incidents", "Read incident", "read"),
        new("helpdesk_incidents.create", "incidents", "Create incident", "write")
    ];

    [Fact]
    public async Task Read_only_preset_keeps_a_now_out_of_scope_deny_visible_until_explicitly_removed()
    {
        var expiry = DateTime.UtcNow.AddDays(1);
        var value = new McpPolicyScopeDraft("Example base", "organization", "org-a", "Allow", ["Incident.Read"],
            Operations.Select(x => x.Key).ToArray(), expiry,
            [new("Deny creation", "Deny", ["helpdesk_incidents.create"], expiry)]);
        using var services = Services();
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference { Value = value };
        var view = await RenderAsync(renderer, reference);
        await InvokeAsync(renderer, reference.Editor!, "ReadOnlyAsync");
        Assert.Equal(["helpdesk_incidents.get"], reference.Value.Operations);
        Assert.Equal(["Incident.Read", "Incident.Write"], reference.Value.Permissions);
        Assert.Equal(["helpdesk_incidents.create"], Assert.Single(reference.Value.Exceptions).Operations);
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("outside base selection", html);
        Assert.Contains("helpdesk_incidents.create", html);
        await InvokeAsync(renderer, reference.Editor!, "RemoveExceptionAsync", 0);
        Assert.Empty(reference.Value.Exceptions);
    }

    [Fact]
    public async Task All_access_preset_restores_the_reviewed_permission_ceiling_and_keeps_deny_exceptions()
    {
        var expiry = DateTime.UtcNow.AddDays(1);
        var reference = new Reference { Value = new("Example", "organization", "org-a", "Deny", ["Incident.Read"],
            ["helpdesk_incidents.get"], expiry, [new("No creation", "Deny", ["helpdesk_incidents.create"], expiry)]) };
        using var services = Services();
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        await RenderAsync(renderer, reference);
        await InvokeAsync(renderer, reference.Editor!, "AllAccessAsync");
        Assert.Equal("Allow", reference.Value.Effect);
        Assert.Equal(["Incident.Read", "Incident.Write"], reference.Value.Permissions);
        Assert.Equal(Operations.Select(x => x.Key).Order(), reference.Value.Operations);
        Assert.Single(reference.Value.Exceptions);
    }

    [Fact]
    public async Task Invalid_exact_expiry_is_emitted_as_invalid_while_the_typed_draft_remains_visible()
    {
        var reference = new Reference { Value = new("Example", "organization", "org-a", "Allow", ["Incident.Read"],
            ["helpdesk_incidents.get"], DateTime.UtcNow.AddDays(1), []) };
        using var services = Services();
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await RenderAsync(renderer, reference);
        await InvokeAsync(renderer, reference.Editor!, "ExpiryChangedAsync", "unfinished UTC date");
        Assert.Equal(DateTime.MinValue, reference.Value.ExpiresUtc);
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("unfinished UTC date", html);
        Assert.Contains("Use an exact UTC time", html);
    }

    [Fact]
    public async Task An_unavailable_saved_permission_stays_visible_until_explicitly_removed()
    {
        var reference = new Reference { Value = new("Example", "organization", "org-a", "Allow", ["Incident.Read", "Request.Read"],
            ["helpdesk_incidents.get"], DateTime.UtcNow.AddDays(1), []) };
        using var services = Services();
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await RenderAsync(renderer, reference);
        await InvokeAsync(renderer, reference.Editor!, "PermissionsChangedAsync", (object)new[] { "Incident.Read", "Incident.Write" });
        Assert.Contains("Request.Read", reference.Value.Permissions);
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Request.Read", html);
        Assert.Contains("no longer granted", html);
        await InvokeAsync(renderer, reference.Editor!, "RemoveUnavailablePermissionAsync", "Request.Read");
        Assert.DoesNotContain("Request.Read", reference.Value.Permissions);
        Assert.Contains("Incident.Write", reference.Value.Permissions);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddMudServices();
        services.AddSingleton(Substitute.For<IJSRuntime>());
        services.AddSingleton<NavigationManager>(new Navigation());
        return services.BuildServiceProvider();
    }
    private static Task<HtmlRootComponent> RenderAsync(Renderer renderer, Reference reference) => renderer.Dispatcher.InvokeAsync(() =>
        renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
    private static Task InvokeAsync(Renderer renderer, Editor editor, string method, params object?[] args) => renderer.Dispatcher.InvokeAsync(() =>
        ((IHandleEvent)editor).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(async () =>
        { if (typeof(Editor).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, args) is Task task) await task; })), null));
    private sealed class Navigation : NavigationManager
    { public Navigation() => Initialize("https://desk.example/", "https://desk.example/admin/integration"); }
    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggers) : StaticHtmlRenderer(services, loggers)
    {
        public async Task<HtmlRootComponent> RenderAsync<T>(ParameterView parameters) where T : IComponent
        { var root = BeginRenderingComponent(typeof(T), parameters); await root.QuiescenceTask; return root; }
    }
    public sealed class Reference
    {
        public Editor? Editor { get; set; }
        public McpPolicyScopeDraft Value { get; set; } = default!;
    }
    public sealed class Harness : ComponentBase
    {
        [Parameter] public Reference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<Editor>(0);
            builder.AddAttribute(1, nameof(Editor.Value), Reference.Value);
            builder.AddAttribute(2, nameof(Editor.ValueChanged), EventCallback.Factory.Create<McpPolicyScopeDraft>(this, value => Reference.Value = value));
            builder.AddAttribute(3, nameof(Editor.Operations), Operations);
            builder.AddAttribute(4, nameof(Editor.EligiblePermissions), new[] { "Incident.Read", "Incident.Write" });
            builder.AddAttribute(5, nameof(Editor.CredentialExpiry), DateTime.UtcNow.AddDays(2));
            builder.AddAttribute(6, nameof(Editor.ScopeLabel), "Example organization");
            builder.AddComponentReferenceCapture(7, value => Reference.Editor = (Editor)value);
            builder.CloseComponent();
        }
    }
}
