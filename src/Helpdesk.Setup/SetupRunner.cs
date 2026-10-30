using Helpdesk.AgentClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Termina;
using Termina.Hosting;
using Termina.Clipboard;
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
            if (options.VerificationDirectory is not null)
            {
                if (runtime.ViewModel.VerificationResult is { } result)
                    await Console.Out.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { checks = result.Checks, cleanupPending = result.CleanupPending },
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            }
            // Shutdown can discard a queued final redraw. Keep the outcome visible
            // after Termina restores the primary terminal screen.
            if (options.VerificationDirectory is not null || runtime.ViewModel.ExitMessage is not null)
                await error.WriteLineAsync(runtime.ViewModel.ExitMessage ?? runtime.ViewModel.Status.Value);
            return runtime.ViewModel.ExitCode;
        }
        catch (AgentClientValidationException exception)
        {
            await error.WriteLineAsync(TerminalText.Sanitize(exception.Message));
            return ConfigurationError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            await error.WriteLineAsync("Setup configuration could not be read securely. Check the selected private configuration and durable storage, then retry setup.");
            return ConfigurationError;
        }
    }

    internal static SetupRuntime CreateHost(SetupOptions options, ISetupWorkflow workflow,
        VirtualInputSource? virtualInput = null, IAnsiTerminal? terminal = null,
        Func<string, IHostDeploymentWorkflow>? hostWorkflowFactory = null,
        Func<string, IHostVerificationWorkflow>? verificationFactory = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var model = new SetupViewModel(workflow, options, hostWorkflowFactory, verificationFactory);
        if (terminal is not null) { builder.Services.AddSingleton(terminal); model.TerminalWidth.Value = terminal.Width; model.TerminalHeight.Value = terminal.Height; }
        else { try { model.TerminalWidth.Value = Console.WindowWidth; model.TerminalHeight.Value = Console.WindowHeight; } catch { model.TerminalWidth.Value = 80; } }
        if (virtualInput is not null) builder.Services.AddTerminaVirtualInput(virtualInput);
        builder.Services.AddTermina("/setup", termina =>
        {
            termina.ConfigureRuntime(runtime => { runtime.PreferRawInput = true; runtime.CtrlCHandlingMode = CtrlCHandlingMode.Disabled; });
            termina.RegisterRoute<SetupPage, SetupViewModel>("/setup", services => new SetupPage(options, services.GetRequiredService<IClipboardService>()), _ => model);
        });
        // Keep Termina's input, routing and rendering registrations, replacing
        // only its runner so host cancellation cannot race the loop's disposal.
        var terminalRunner = builder.Services.Single(service => service.ServiceType == typeof(IHostedService)
            && service.ImplementationType?.FullName == "Termina.Hosting.TerminaHostedService");
        builder.Services.Remove(terminalRunner);
        builder.Services.AddHostedService<SetupTerminalHostedService>();
        var host = builder.Build();
        return new(host, model, host.Services.GetRequiredService<TerminaApplication>());
    }
}

internal sealed record SetupRuntime(IHost Host, SetupViewModel ViewModel, TerminaApplication Application);
