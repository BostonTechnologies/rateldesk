using System.CommandLine;
using Helpdesk.AgentClient;
using Helpdesk.Setup;

internal static partial class HelpdeskCli
{
    private static Command BuildSetupCommand(CliRuntime runtime, GlobalOptions globals) => SetupCommands.CreateSetupCommand(
        SetupTransport.Cli, globals.Profile, globals.ConfigPath, runtime.Error, seed: parse => AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = parse.GetValue(globals.ApiBaseUrl), AuthentikTokenUrl = parse.GetValue(globals.TokenUrl),
            AuthentikClientId = parse.GetValue(globals.ClientId), AuthentikUsername = parse.GetValue(globals.Username),
            AuthentikScope = parse.GetValue(globals.Scope), AgentUserEmail = parse.GetValue(globals.AgentUserEmail)
        });
    private static ConfigurationResolution ResolveLocalConfiguration(InvocationContext context, GlobalOptions globals)
    {
        try
        {
            return AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Cli,
                SelectedConfigurationPath(context, globals), AgentClientConfiguration.Empty with
                {
                    ApiBaseUrl = context.ParseResult.GetValueForOption(globals.ApiBaseUrl),
                    AuthentikTokenUrl = context.ParseResult.GetValueForOption(globals.TokenUrl),
                    AuthentikClientId = context.ParseResult.GetValueForOption(globals.ClientId),
                    AuthentikUsername = context.ParseResult.GetValueForOption(globals.Username),
                    AuthentikScope = context.ParseResult.GetValueForOption(globals.Scope),
                    AgentUserEmail = context.ParseResult.GetValueForOption(globals.AgentUserEmail)
                });
        }
        catch (AgentClientValidationException exception) { throw new CliValidationException(exception.Message); }
    }
    private static Command BuildLocalStatusCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var command = new Command("status", "Show redacted effective configuration, sources and profile status.");
        command.SetHandler(context => WriteJsonAsync(runtime, ResolveLocalConfiguration(context, globals).Redacted(), context, globals));
        return command;
    }
    private static Command BuildLocalValidateCommand(CliRuntime runtime, GlobalOptions globals, string name)
    {
        var command = new Command(name, "Validate the selected profile without network I/O.");
        command.SetHandler(async context =>
        {
            var report = await ConfigurationDoctor.CheckAsync(ResolveLocalConfiguration(context, globals), ct: context.GetCancellationToken());
            context.ExitCode = report.ConfigurationValid ? 0 : CliExitCodes.ValidationError;
            await WriteJsonAsync(runtime, report, context, globals);
        });
        return command;
    }
    private static Command BuildDoctorCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var command = new Command("doctor", "Run explicitly requested bounded read-only endpoint/auth checks.");
        var network = new Option<bool>("--network");
        var authenticate = new Option<bool>("--authenticate");
        command.AddOption(network); command.AddOption(authenticate);
        command.SetHandler(async context =>
        {
            var resolution = ResolveLocalConfiguration(context, globals);
            if (resolution.Configuration.ClientTransport == "http" && context.ParseResult.GetValueForOption(authenticate))
            {
                using var workflow = new SetupWorkflow(new(SetupTransport.Http, ConfigurationPath: resolution.ProfilePath));
                var checks = await workflow.CheckAsync(resolution, SetupTransport.Http, context.GetCancellationToken());
                context.ExitCode = checks.Any(check => check.Status == "failed") ? CliExitCodes.RemoteError : 0;
                await WriteJsonAsync(runtime, new { checks }, context, globals);
            }
            else
            {
                var report = await ConfigurationDoctor.CheckAsync(resolution, context.ParseResult.GetValueForOption(network), context.ParseResult.GetValueForOption(authenticate), context.GetCancellationToken());
                context.ExitCode = report.Checks.All(check => check.Ok) ? 0 : CliExitCodes.ValidationError;
                await WriteJsonAsync(runtime, report, context, globals);
            }
        });
        return command;
    }
    private static Command BuildProfileCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var command = new Command("profile", "Inspect isolated named CLI profiles. Local removal does not revoke server credentials.");
        var list = new Command("list", "List explicitly saved profiles.");
        list.SetHandler(context => WriteJsonAsync(runtime, new { profiles = new ConfigurationProfiles().List() }, context, globals));
        command.AddCommand(list);
        var showName = new Argument<string>("name"); var show = new Command("show", "Show a named profile with secrets redacted.") { showName };
        show.SetHandler(context =>
        {
            try { return WriteJsonAsync(runtime, AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Cli, new ConfigurationProfiles().PathFor(RequiredArgument(context.ParseResult, showName))).Redacted(), context, globals); }
            catch (AgentClientValidationException exception) { throw new CliValidationException(exception.Message); }
        });
        command.AddCommand(show);
        var removeName = new Argument<string>("name"); var confirm = new Option<bool>("--confirm");
        var remove = new Command("remove", "Remove one local profile only. Revoke its server credential separately in account settings.") { removeName, confirm };
        remove.SetHandler(async context =>
        {
            if (!context.ParseResult.GetValueForOption(confirm))
            {
                context.ExitCode = CliExitCodes.ValidationError;
                await WriteJsonAsync(runtime, new { removed = false, requiresConfirmation = true, serverCredentialRevoked = false }, context, globals);
                return;
            }
            try { new ConfigurationProfiles().Remove(RequiredArgument(context.ParseResult, removeName)); }
            catch (AgentClientValidationException exception) { throw new CliValidationException(exception.Message); }
            await WriteJsonAsync(runtime, new { removed = true, serverCredentialRevoked = false }, context, globals);
        });
        command.AddCommand(remove);
        return command;
    }
}
