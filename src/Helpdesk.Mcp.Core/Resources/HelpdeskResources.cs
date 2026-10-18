using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using ModelContextProtocol.Server;

namespace Helpdesk.Mcp.Resources;

[McpServerResourceType]
public sealed class HelpdeskResources(Func<IHelpdeskAgentClient> clientResolver, HelpdeskMcpHostContext hostContext)
{
    private readonly Func<IHelpdeskAgentClient> _clientResolver = clientResolver ?? throw new ArgumentNullException(nameof(clientResolver));

    public HelpdeskResources(IHelpdeskAgentClient client, HelpdeskMcpHostContext hostContext)
        : this(() => client, hostContext)
    {
    }

    public HelpdeskResources(IHelpdeskAgentClient client, HelpdeskMcpTarget target)
        : this(client, target.ToHostContext("stdio"))
    {
    }

    [McpServerResource(UriTemplate = "helpdesk://server/status", Name = "Helpdesk MCP server status", MimeType = "application/json")]
    public string ServerStatus() => JsonSerializer.Serialize(new { server = "Helpdesk.Mcp", instance = hostContext.Instance, target = hostContext.ToTargetInfo(), hostContext, catalogRevision = hostContext.CatalogRevision, transport = hostContext.Transport, resourceUri = hostContext.ResourceUri, apiBaseUrl = hostContext.CanonicalApiBaseUrl, configuredIdentity = ConfiguredIdentity(), mutationsEnabled = true, mutationConfirmationRequired = true, obsoleteMutationProofToolExposed = false });

    [McpServerResource(UriTemplate = "helpdesk://health", Name = "Helpdesk health", MimeType = "application/json")]
    public async Task<string> Health(CancellationToken cancellationToken = default)
    {
        var checks = await _clientResolver().GetHealthAsync(cancellationToken).ConfigureAwait(false);
        foreach (var check in checks.OfType<JsonObject>())
        {
            check["target"] = JsonSerializer.SerializeToNode(hostContext.ToTargetInfo());
            check["hostContext"] = JsonSerializer.SerializeToNode(hostContext);
        }

        return checks.ToJsonString();
    }

    [McpServerResource(UriTemplate = "helpdesk://config/redacted", Name = "Redacted Helpdesk configuration", MimeType = "application/json")]
    public string RedactedConfig()
    {
        var configuration = JsonSerializer.SerializeToNode(AgentClientConfigurationResolver.Redact(_clientResolver().Configuration))!.AsObject();
        configuration["instance"] = hostContext.Instance;
        configuration["target"] = JsonSerializer.SerializeToNode(hostContext.ToTargetInfo());
        configuration["hostContext"] = JsonSerializer.SerializeToNode(hostContext);
        return configuration.ToJsonString();
    }

    [McpServerResource(UriTemplate = "helpdesk://capabilities", Name = "Helpdesk MCP capabilities", MimeType = "application/json")]
    public string Capabilities() => JsonSerializer.Serialize(new { instance = hostContext.Instance, target = hostContext.ToTargetInfo(), hostContext, catalogRevision = hostContext.CatalogRevision, phase = 3, transport = hostContext.Transport, resourceUri = hostContext.ResourceUri, configuredIdentity = ConfiguredIdentity(), mutationToolsEnabled = true, mutationConfirmationRequired = true, rawEnabled = false, obsoleteMutationProofToolExposed = false });

    private JsonObject ConfiguredIdentity() => HelpdeskMcpIdentityMetadata.ToJson(hostContext, _clientResolver().Configuration);
}
