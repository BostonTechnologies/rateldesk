using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Helpdesk.Mcp;

/// <summary>
/// Registers one transport-neutral Helpdesk MCP capability set. A transport host
/// owns its listener, authentication, and configuration surface.
/// </summary>
public static class HelpdeskMcpCoreServiceCollectionExtensions
{
    public static IServiceCollection AddHelpdeskMcpCore(
        this IServiceCollection services,
        AgentClientConfiguration configuration,
        IHelpdeskMcpConfigurationSurface configurationSurface,
        HelpdeskMcpHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configurationSurface);
        ArgumentNullException.ThrowIfNull(hostContext);

        services.AddSingleton(configuration);
        services.AddSingleton(configurationSurface);
        services.AddSingleton(hostContext);
        services.AddTransient<HelpdeskTools>();
        services.AddSingleton(new HelpdeskMcpTarget(hostContext.Instance, new Uri(hostContext.CanonicalApiBaseUrl)));
        var resolvedConfiguration = configuration.Resolve();
        services.AddSingleton<IValidateOptions<AgentClientOutboundOptions>, AgentClientOutboundOptionsValidator>();
        services.AddOptions<AgentClientOutboundOptions>().Configure(options =>
        {
            options.ApiBaseUrl = configuration.ApiBaseUrl;
            options.CredentialMode = configuration.CredentialMode;
            options.IntegrationCredential = configuration.IntegrationCredential;
            options.IntegrationCredentialFile = configuration.IntegrationCredentialFile;
            options.AuthentikTokenUrl = configuration.AuthentikTokenUrl;
            options.AuthentikClientId = configuration.AuthentikClientId;
            options.AuthentikUsername = configuration.AuthentikUsername;
            options.AuthentikAppPassword = configuration.AuthentikAppPassword;
            options.AuthentikAppPasswordFile = configuration.AuthentikAppPasswordFile;
            options.AuthentikScope = configuration.AuthentikScope;
            options.AgentUserEmail = configuration.AgentUserEmail;
            options.CredentialTarget = configuration.CredentialTarget;
        }).ValidateOnStart();
        services.AddHttpClient(HelpdeskAgentClient.ApiHttpClientName, client =>
        {
            client.BaseAddress = resolvedConfiguration.ApiBaseUrl;
            client.Timeout = TimeSpan.FromSeconds(60);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient(HelpdeskAgentClient.AuthHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IHelpdeskAgentClient>(provider => new HelpdeskAgentClient(
            configuration,
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()));
        return services;
    }

    public static IServiceCollection AddHelpdeskMcpCore(
        this IServiceCollection services,
        AgentClientConfiguration configuration,
        IHelpdeskMcpConfigurationSurface configurationSurface,
        HelpdeskMcpTarget target,
        string transport,
        string resourceUri = HelpdeskMcpHostContext.DefaultResourceUri)
        => services.AddHelpdeskMcpCore(configuration, configurationSurface, target.ToHostContext(transport, resourceUri));
}
