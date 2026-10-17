using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Helpdesk.AgentClient;

namespace Helpdesk.Setup;

/// <summary>Explicit command dispatch shared by the stdio and HTTP distributions.</summary>
public static class SetupCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool IsCommand(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--profile" or "--config") { index++; continue; }
            if (argument == "--json" || argument.StartsWith("--profile=", StringComparison.Ordinal) || argument.StartsWith("--config=", StringComparison.Ordinal)) continue;
            return argument is "setup" or "host" or "status" or "config-validate" or "doctor" or "profile" or "config" or "auth";
        }
        return false;
    }

    public static Command CreateSetupCommand(SetupTransport defaultTransport, Option<string?> profile, Option<string?> configuration,
        TextWriter error, string? stdioEntryAssembly = null, Func<System.CommandLine.ParseResult, AgentClientConfiguration>? seed = null)
    {
        var command = new Command("setup", "Interactive client setup, unchanged HTTP MCP host verification, or reviewed configuration changes.");
        var target = new Option<string?>("--target") { Description = "cli, stdio, http-client, or http-host." };
        target.AcceptOnlyFromAmong("cli", "stdio", "http-client", "http-host");
        var mode = new Option<string?>("--mode") { Description = "host or client; compatible alias for --target http-host or the executable's client transport." };
        mode.AcceptOnlyFromAmong("host", "client");
        var url = new Option<string?>("--url", "--web-url") { Description = "RatelDesk Web HTTPS root or /mcp URL; seeds the first screen." };
        var authentication = new Option<string?>("--authentication") { Description = "Explicit client authentication choice; seeds Advanced." };
        authentication.AcceptOnlyFromAmong("browser", "existing", "manual", "authentik", "oauth");
        var instance = new Option<string?>("--instance") { Description = "Existing MCP instance label; arbitrary valid labels are preserved." };
        var directory = new Option<string?>("--export-directory") { Description = "Private persistent host checkpoint/candidate storage; deployment sources remain owned by their manager." };
        var monochrome = new Option<bool>("--monochrome") { Description = "Use the terminal's default colors." };
        var executable = new Option<string?>("--stdio-executable") { Description = "Explicit rateldesk-mcp apphost or assembly path for bounded transport checks." };
        command.Options.Add(target); command.Options.Add(mode); command.Options.Add(url); command.Options.Add(authentication); command.Options.Add(instance);
        command.Options.Add(directory); command.Options.Add(monochrome); command.Options.Add(executable);
        command.SetAction(async (parse, ct) =>
        {
            if (parse.GetValue(profile) is not null && parse.GetValue(configuration) is not null)
            { await error.WriteLineAsync("Choose either --profile or --config."); return SetupRunner.ConfigurationError; }
            var selected = parse.GetValue(target);
            var selectedMode = parse.GetValue(mode);
            if (selected is not null && selectedMode is not null && (selected == "http-host") != (selectedMode == "host"))
            { await error.WriteLineAsync("--mode conflicts with --target. Choose one setup surface."); return SetupRunner.ConfigurationError; }
            var transport = selected switch { "cli" => SetupTransport.Cli, "stdio" => SetupTransport.Stdio, "http-client" => SetupTransport.Http, _ => defaultTransport };
            return await SetupRunner.RunAsync(new(transport, parse.GetValue(profile) ?? "default", parse.GetValue(configuration), selected == "http-host" || selectedMode == "host",
                parse.GetValue(monochrome) || Environment.GetEnvironmentVariable("NO_COLOR") is not null,
                parse.GetValue(directory), parse.GetValue(executable), stdioEntryAssembly,
                parse.GetValue(url), parse.GetValue(authentication), parse.GetValue(instance), seed?.Invoke(parse), parse.GetValue(profile) is not null), error, ct);
        });
        return command;
    }

    public static async Task<int> InvokeAsync(string[] args, SetupTransport defaultTransport, TextWriter? output = null, TextWriter? error = null,
        string? stdioEntryAssembly = null, CancellationToken cancellationToken = default)
    {
        output ??= Console.Out; error ??= Console.Error;
        var root = new RootCommand("RatelDesk explicit setup and redacted configuration commands. Normal serving starts without a wizard.");
        var profile = new Option<string?>("--profile") { Description = "Explicit named profile.", Recursive = true };
        var configuration = new Option<string?>("--config") { Description = "Explicit configuration file.", Recursive = true };
        var json = new Option<bool>("--json") { Description = "Machine-readable redacted output (the default for diagnostics).", Recursive = true };
        root.Options.Add(profile); root.Options.Add(configuration); root.Options.Add(json);
        HostDeploymentCommands.Register(root, output, error);
        root.Subcommands.Add(CreateSetupCommand(defaultTransport, profile, configuration, error, stdioEntryAssembly));
        var surface = defaultTransport == SetupTransport.Cli ? ConfigurationSurface.Cli : ConfigurationSurface.Mcp;
        ConfigurationProfiles Profiles() => new(rootPath: defaultTransport == SetupTransport.Http ? SetupInput.HttpClientProfileRoot : null, surface: surface);
        ConfigurationResolution Resolve(string? path) => AgentClientConfigurationResolver.ResolveEffective(surface, path,
            environment: defaultTransport == SetupTransport.Http ? _ => null : null);
        string? Selected(System.CommandLine.ParseResult parse)
        {
            if (parse.GetValue(profile) is { } name)
            {
                if (parse.GetValue(configuration) is not null) throw new AgentClientValidationException("Choose either --profile or --config.");
                return Profiles().PathFor(name);
            }
            return parse.GetValue(configuration) ?? (defaultTransport == SetupTransport.Http ? Profiles().PathFor("default") : null);
        }
        async Task<int> Safe(Func<Task<int>> operation)
        {
            try { return await operation(); }
            catch (AgentClientValidationException exception) { await error.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = TerminalText.Sanitize(exception.Message) }, Json)); return SetupRunner.ConfigurationError; }
        }
        Task Print(object value) => output.WriteLineAsync(JsonSerializer.Serialize(value, Json));
        var status = new Command("status", "Show redacted effective configuration and source provenance.");
        status.SetAction((parse, ct) => Safe(async () => { var value = Resolve(Selected(parse)); await Print(value.Redacted()); return 0; }));
        root.Subcommands.Add(status);
        Command Validate(string name)
        {
            var command = new Command(name, "Validate the selected configuration without network I/O.");
            command.SetAction((parse, ct) => Safe(async () => { var report = await ConfigurationDoctor.CheckAsync(Resolve(Selected(parse)), ct: ct); await Print(report); return report.ConfigurationValid ? 0 : SetupRunner.ConfigurationError; }));
            return command;
        }
        root.Subcommands.Add(Validate("config-validate"));
        var config = new Command("config", "Redacted configuration inspection."); config.Subcommands.Add(Validate("validate"));
        var show = new Command("show", "Show the selected configuration with all secrets redacted.");
        show.SetAction((parse, ct) => Safe(async () => { await Print(Resolve(Selected(parse)).Redacted()); return 0; }));
        config.Subcommands.Add(show); root.Subcommands.Add(config);
        var doctor = new Command("doctor", "Run explicitly requested bounded read-only checks.");
        var network = new Option<bool>("--network") { Description = "Check the configured endpoint." };
        var authenticate = new Option<bool>("--authenticate") { Description = "Check current credential access; HTTP profiles also initialize and call a read-only tool." };
        doctor.Options.Add(network); doctor.Options.Add(authenticate);
        doctor.SetAction((parse, ct) => Safe(async () =>
        {
            var resolution = Resolve(Selected(parse));
            if (resolution.Configuration.ClientTransport == "http" && parse.GetValue(authenticate))
            {
                using var workflow = new SetupWorkflow(new(SetupTransport.Http, ConfigurationPath: resolution.ProfilePath));
                var checks = await workflow.CheckAsync(resolution, SetupTransport.Http, ct);
                await Print(new { checks }); return checks.Any(check => check.Status == "failed") ? 1 : 0;
            }
            var report = await ConfigurationDoctor.CheckAsync(resolution, parse.GetValue(network), parse.GetValue(authenticate), ct);
            await Print(report); return report.Checks.All(check => check.Ok) ? 0 : 1;
        }));
        root.Subcommands.Add(doctor);
        var profiles = new Command("profile", "Manage explicitly isolated local profiles. Removal does not revoke server credentials.");
        var list = new Command("list", "List local profile names."); list.SetAction((parse, ct) => Safe(async () => { await Print(new { surface = surface.ToString().ToLowerInvariant(), profiles = Profiles().List() }); return 0; })); profiles.Subcommands.Add(list);
        var nameArgument = new Argument<string>("name");
        var inspect = new Command("show", "Show one redacted named profile."); inspect.Arguments.Add(nameArgument);
        inspect.SetAction((parse, ct) => Safe(async () => { await Print(Resolve(Profiles().PathFor(parse.GetValue(nameArgument)!)).Redacted()); return 0; })); profiles.Subcommands.Add(inspect);
        var removeName = new Argument<string>("name"); var confirm = new Option<bool>("--confirm");
        var remove = new Command("remove", "Remove a local profile only; server credential revocation is separate."); remove.Arguments.Add(removeName); remove.Options.Add(confirm);
        remove.SetAction((parse, ct) => Safe(async () =>
        {
            if (!parse.GetValue(confirm)) { await Print(new { removed = false, requiresConfirmation = true, message = "Review the local profile, then repeat with --confirm. Server credentials are not revoked." }); return 64; }
            var store = Profiles(); store.Remove(parse.GetValue(removeName)!);
            await Print(new { removed = true, serverCredentialRevoked = false }); return 0;
        })); profiles.Subcommands.Add(remove); root.Subcommands.Add(profiles);
        var auth = new Command("auth", "Inspect or remove a local authentication reference; revocation stays in account credential management.");
        var authStatus = new Command("status", "Show redacted local credential status.");
        authStatus.SetAction((parse, ct) => Safe(async () => { await Print(Resolve(Selected(parse)).Redacted()); return 0; })); auth.Subcommands.Add(authStatus);
        var logout = new Command("logout", "Remove local authentication references without revoking any server consumer.");
        logout.SetAction((parse, ct) => Safe(async () =>
        {
            var resolution = Resolve(Selected(parse));
            if (resolution.ProfilePath is null || resolution.Settings.Any(setting => setting.Managed && setting.IsSecret)) throw new AgentClientValidationException("Authentication is externally managed; change the owning environment or secret mount.");
            var store = new AgentClientConfigurationStore(resolution.ProfilePath);
            var saved = store.Load() with { IntegrationCredential = null, IntegrationCredentialFile = null, AuthentikAppPassword = null, AuthentikAppPasswordFile = null };
            store.SaveSecure(saved, resolution.Revision ?? "", ct);
            await Print(new { loggedOutLocally = true, serverCredentialRevoked = false }); return 0;
        })); auth.Subcommands.Add(logout); root.Subcommands.Add(auth);
        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false, Output = output, Error = error }, cancellationToken);
    }
}
