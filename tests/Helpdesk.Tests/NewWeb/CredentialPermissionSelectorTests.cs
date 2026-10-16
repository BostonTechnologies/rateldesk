extern alias NewWeb;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Selector = NewWeb::HelpDesk.NewWeb.Components.Shared.CredentialPermissionSelector;

namespace Helpdesk.Tests.NewWeb;

public sealed class CredentialPermissionSelectorTests
{
    [Fact]
    public async Task Filtered_selection_and_group_actions_send_exact_eligible_ids_and_keep_hidden_selection_visible()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Harness>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("0 selected / 4 available", html);
        Assert.DoesNotContain("HelpdeskAdmin", html);
        await InvokeAsync(renderer, reference, "SearchChanged", new ChangeEventArgs { Value = "Incident.Read" });
        await InvokeAsync(renderer, reference, "SelectDisplayedAsync");
        Assert.Equal(["Incident.Read"], reference.Values);
        await InvokeAsync(renderer, reference, "SearchChanged", new ChangeEventArgs { Value = "Request" });
        html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Selected (including search-hidden): Incident.Read", html);
        Assert.Contains("Select 2 displayed eligible permissions", html);
        await InvokeAsync(renderer, reference, "SelectGroupAsync", "Request");
        Assert.Equal(["Incident.Read", "Request.Read", "Request.Write"], reference.Values);
        await InvokeAsync(renderer, reference, "ClearGroupAsync", "Request");
        Assert.Equal(["Incident.Read"], reference.Values);
        await InvokeAsync(renderer, reference, "ClearAsync");
        Assert.Empty(reference.Values);
    }

    [Fact]
    public async Task Removed_authority_never_renders_or_submits_an_old_selection()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var view = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<Selector>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(Selector.EligiblePermissions)] = new[] { "Incident.Read" },
            [nameof(Selector.SelectedValues)] = new[] { "Incident.Read", "Incident.Delete", "*", "HelpdeskAdmin" },
            [nameof(Selector.FormFieldName)] = "permissions"
        })));
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("1 selected / 1 available", html);
        Assert.Contains("name=\"permissions\" value=\"Incident.Read\"", html);
        Assert.DoesNotContain("Incident.Delete", html);
        Assert.DoesNotContain("HelpdeskAdmin", html);
        Assert.DoesNotContain("value=\"*\"", html);
    }

    private static Task InvokeAsync(Renderer renderer, Reference reference, string name, object? arg = null) =>
        renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)reference.Selector!).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(async () =>
        {
            var result = typeof(Selector).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(reference.Selector, arg is null ? null : [arg]);
            if (result is Task task) await task;
        })), null));
    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggers) : StaticHtmlRenderer(services, loggers)
    {
        public async Task<HtmlRootComponent> RenderAsync<T>(ParameterView parameters) where T : IComponent
        { var root = BeginRenderingComponent(typeof(T), parameters); await root.QuiescenceTask; return root; }
    }
    public sealed class Reference { public Selector? Selector { get; set; } public string[] Values { get; set; } = []; }
    public sealed class Harness : ComponentBase
    {
        [Parameter] public Reference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<Selector>(0);
            builder.AddAttribute(1, nameof(Selector.EligiblePermissions), new[] { "Incident.Read", "Incident.Write", "Request.Read", "Request.Write", "HelpdeskAdmin" });
            builder.AddAttribute(2, nameof(Selector.SelectedValues), Reference.Values);
            builder.AddAttribute(3, nameof(Selector.SelectedValuesChanged), EventCallback.Factory.Create<IEnumerable<string>>(this, values => { Reference.Values = values.ToArray(); StateHasChanged(); }));
            builder.AddComponentReferenceCapture(4, c => Reference.Selector = (Selector)c);
            builder.CloseComponent();
        }
    }
}
