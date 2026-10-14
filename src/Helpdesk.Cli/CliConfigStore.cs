using Helpdesk.AgentClient;

/// <summary>Compatibility adapter; all configuration persistence lives in AgentClient.</summary>
internal sealed class CliConfigStore
{
    private readonly AgentClientConfigurationStore store;
    public CliConfigStore(string? explicitPath = null) => store = new(explicitPath);
    public string Path => store.Path;
    public static string DefaultPath => AgentClientConfigurationStore.DefaultPath;
    public CliConfig Load()
    {
        try { return CliConfig.FromAgentConfiguration(store.Load()); }
        catch (AgentClientValidationException exception) { throw new CliValidationException(exception.Message); }
    }
    public void Save(CliConfig config)
    {
        try { store.SaveSecure(config.ToAgentConfiguration(), store.Revision ?? string.Empty); }
        catch (AgentClientValidationException exception) { throw new CliValidationException(exception.Message); }
    }
}
