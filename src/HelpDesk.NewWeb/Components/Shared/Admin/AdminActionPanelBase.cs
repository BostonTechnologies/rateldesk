using Microsoft.AspNetCore.Components;

namespace HelpDesk.NewWeb.Components.Shared.Admin;

/// <summary>Keeps the owning context fixed throughout a focused editor and its save.</summary>
public abstract class AdminActionPanelBase : ComponentBase, IDisposable
{
    [Parameter] public EventCallback<bool> BusyChanged { get; set; }
    protected bool IsBusy { get; private set; }
    protected bool IsDisposed { get; private set; }

    protected async Task RunActionAsync(Func<Task> action)
    {
        if (IsBusy || IsDisposed) return;
        IsBusy = true;
        await BusyChanged.InvokeAsync(true);
        try { await action(); }
        finally
        {
            IsBusy = false;
            if (!IsDisposed) await BusyChanged.InvokeAsync(false);
        }
    }

    public void Dispose() => IsDisposed = true;
}
