using System.Text.Json;
using System.Text.Json.Serialization;
using Helpdesk.Application.AiAssistant.Chat;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

public sealed record OpenAiWorklogAction(string IncidentId, string Note)
{
    public const string Operation = "helpdesk_incidents.add_worklog";
    public const string Function = "request_incident_internal_note";

    internal static AgentFunction Definition => new(Function,
        "Request a RatelDesk operator's approval to add an internal note to an incident. It never notifies the customer.",
        JsonSerializer.Deserialize("""
            {"type":"object","properties":{"incident_id":{"type":"string"},"note":{"type":"string"}},
             "required":["incident_id","note"],"additionalProperties":false}
            """, OpenAiWorklogJson.Default.JsonElement));

    public static OpenAiWorklogAction? Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("incident_id", out var id) || id.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("note", out var note) || note.ValueKind != JsonValueKind.String)
            return null;
        var ticket = id.GetString();
        var text = note.GetString();
        if (!OpenAiChatProfiles.Identifier(ticket, 128) || string.IsNullOrWhiteSpace(text) || text.Length > 4000 ||
            text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return null;
        return new(ticket!, text);
    }

    internal static string CanonicalArguments(ProviderPendingAction action) => action.Kind switch
    {
        ProviderActionKind.Function => Read(action.Arguments) is { } note
            ? JsonSerializer.Serialize(note, OpenAiWorklogJson.Default.OpenAiWorklogAction) : "",
        ProviderActionKind.McpApproval => CanonicalizeMcpArguments(action.Arguments),
        _ => ""
    };

    private static string CanonicalizeMcpArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 16000) return "";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, arguments);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray()) WriteCanonical(writer, element);
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenAiWorklogAction))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class OpenAiWorklogJson : JsonSerializerContext;
