using Helpdesk.AgentClient;
using Helpdesk.Mcp;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Prompts;
using Helpdesk.Mcp.Resources;
using Helpdesk.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Helpdesk.Setup;
using Helpdesk.AgentClient.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Mcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (SetupCommands.IsCommand(args))
            return await SetupCommands.InvokeAsync(args, SetupTransport.Stdio, stdioEntryAssembly: typeof(Program).Assembly.Location);
        if (args is ["--help"] or ["-h"])
        {
            await Console.Out.WriteLineAsync("rateldesk-mcp - RatelDesk stdio MCP server\n\nExplicit commands: setup [--target cli|stdio|http-client|http-host] [--profile NAME|--config PATH] [--export-directory DIR] [--monochrome], status, config validate, doctor [--network] [--authenticate], profile list/show/remove, auth status/logout.\n\nConfigure RATELDESK_MCP_CONFIG and RATELDESK_MCP_INSTANCE before normal server mode. Logs are written to stderr; stdout is reserved for MCP protocol traffic.");
            return 0;
        }
        if (args is ["--version"])
        {
            await Console.Out.WriteLineAsync($"rateldesk-mcp {typeof(Program).Assembly.GetName().Version} ({typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion ?? "unknown"})");
            return 0;
        }
        try
        {
            var configurationPath = Environment.GetEnvironmentVariable(HelpdeskMcpTarget.ConfigurationEnvironmentVariable);
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Services.AddRatelDeskLoggingPolicy(builder.Configuration);
            var resolution = AgentClientConfigurationResolver.ResolveEffective(ConfigurationSurface.Mcp, configurationPath, configuration: builder.Configuration);
            var configuration = resolution.Configuration;
            var target = HelpdeskMcpTarget.Resolve(configuration, configurationPath,
                builder.Configuration["Helpdesk:Mcp:Instance"], builder.Configuration["Helpdesk:Mcp:ExpectedApiBaseUrl"]);
            var configurationSurface = HelpdeskMcpConfigurationSurface.Create(resolution);
            var hostContext = target.ToHostContext("stdio");
            IServiceProvider? applicationServices = null;
            var resourceTarget = new HelpdeskResources(
                () => applicationServices?.GetRequiredService<IHelpdeskAgentClient>()
                    ?? throw new InvalidOperationException("Helpdesk MCP resources were invoked before the application service provider was available."),
                hostContext);

            builder.Services.AddHelpdeskMcpCore(configuration, configurationSurface, hostContext);
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(new McpDuplicateArgumentsStream(Console.OpenStandardInput()), Console.OpenStandardOutput())
                .WithTools(HelpdeskToolDefinitions.Create())
                .WithResources(resourceTarget)
                .WithPrompts<HelpdeskPrompts>();

            var host = builder.Build();
            applicationServices = host.Services;
            host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Helpdesk.Mcp.Target")
                .LogInformation("Helpdesk MCP target validated for {Instance} at {ApiBaseUrl}", target.Instance, target.CanonicalApiBaseUrl);
            await host.RunAsync();
            return 0;
        }
        catch (AgentClientValidationException exception)
        {
            await Console.Error.WriteLineAsync($"Helpdesk MCP startup validation failed: {exception.Message}");
            return 78;
        }
        catch (OptionsValidationException)
        {
            await Console.Error.WriteLineAsync("Helpdesk MCP startup validation failed: invalid RatelDesk:Logging policy. Use named levels Trace, Debug, Information, Warning, Error, Critical or None and bounded category overrides.");
            return 78;
        }
    }
}
