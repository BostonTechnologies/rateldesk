using Helpdesk.AgentClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;

namespace Helpdesk.Setup;

public static class SetupRunner
{
    public const int ConfigurationError = 78;
    public static async Task<int> RunAsync(SetupOptions options, TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        error ??= Console.Error;
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            await error.WriteLineAsync("Interactive setup requires a TTY. Use a private JSON profile/secret-file reference with config-validate and doctor for automation; normal serving never launches setup.");
            return ConfigurationError;
        }
        try
        {
            using var workflow = new SetupWorkflow(options);
            var runtime = CreateHost(options, workflow);
            using var host = runtime.Host;
            try { await host.RunAsync(cancellationToken); }
            finally { await runtime.ViewModel.CleanupAsync(); }
            return 0;
        }
        catch (AgentClientValidationException exception)
        {
            await error.WriteLineAsync(TerminalText.Sanitize(exception.Message));
            return ConfigurationError;
        }
    }

    internal static SetupRuntime CreateHost(SetupOptions options, ISetupWorkflow workflow,
        VirtualInputSource? virtualInput = null, IAnsiTerminal? terminal = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var model = new SetupViewModel(workflow, options);
        if (terminal is not null) { builder.Services.AddSingleton(terminal); model.TerminalWidth.Value = terminal.Width; }
        else { try { model.TerminalWidth.Value = Console.WindowWidth; } catch { model.TerminalWidth.Value = 80; } }
        if (virtualInput is not null) builder.Services.AddTerminaVirtualInput(virtualInput);
        builder.Services.AddTermina("/setup", termina =>
        {
            termina.ConfigureRuntime(runtime => { runtime.PreferRawInput = true; runtime.CtrlCHandlingMode = CtrlCHandlingMode.Disabled; });
            termina.RegisterRoute<SetupPage, SetupViewModel>("/setup", _ => new SetupPage(options), _ => model);
        });
        var host = builder.Build();
        return new(host, model, host.Services.GetRequiredService<TerminaApplication>());
    }
}

internal sealed record SetupRuntime(IHost Host, SetupViewModel ViewModel, TerminaApplication Application);
