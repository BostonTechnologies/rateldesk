using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helpdesk.AgentClient;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentClientConfiguration))]
[JsonSerializable(typeof(Dictionary<string, string?>), TypeInfoPropertyName = "StringSettings")]
internal sealed partial class AgentClientJsonContext : JsonSerializerContext;
