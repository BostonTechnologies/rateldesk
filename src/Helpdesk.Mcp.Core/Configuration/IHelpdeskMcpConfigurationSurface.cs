using Helpdesk.AgentClient;

namespace Helpdesk.Mcp.Configuration;

/// <summary>
/// Transport-owned surface for the two allowlisted persisted MCP settings.
/// Remote transports can provide a read-only surface; stdio keeps its existing
/// explicitly-confirmed file-backed behavior.
/// </summary>
public interface IHelpdeskMcpConfigurationSurface
{
    bool CanPersist { get; }

    AgentClientConfiguration Load();

    void Save(AgentClientConfiguration configuration);
}

public static class HelpdeskMcpConfigurationSurface
{
    public static IHelpdeskMcpConfigurationSurface Create(ConfigurationResolution resolution)
        => resolution.CanPersist && resolution.ProfilePath is not null
            ? new AgentClientConfigurationSurface(new AgentClientConfigurationStore(resolution.ProfilePath))
            : new ReadOnlyHelpdeskMcpConfigurationSurface(resolution.Configuration);
}

public sealed class AgentClientConfigurationSurface(AgentClientConfigurationStore store) : IHelpdeskMcpConfigurationSurface
{
    public bool CanPersist => true;

    public AgentClientConfiguration Load() => store.Load();

    public void Save(AgentClientConfiguration configuration) => store.Save(configuration);
}

public sealed class ReadOnlyHelpdeskMcpConfigurationSurface(AgentClientConfiguration configuration) : IHelpdeskMcpConfigurationSurface
{
    public bool CanPersist => false;

    public AgentClientConfiguration Load() => configuration;

    public void Save(AgentClientConfiguration configuration)
        => throw new InvalidOperationException("This Helpdesk MCP transport does not support persisted configuration writes.");
}
