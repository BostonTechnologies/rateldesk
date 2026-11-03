using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Helpdesk.Mcp.Tools;

namespace Helpdesk.Mcp;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelpdeskToolResponse))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(object))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
