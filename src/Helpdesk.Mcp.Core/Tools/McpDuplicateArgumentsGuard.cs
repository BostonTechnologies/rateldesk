using System.Text.Json;

namespace Helpdesk.Mcp.Tools;

/// <summary>
/// Moves a duplicate tools/call params.arguments property to the envelope
/// validator's already supported duplicate-operation path. The SDK binds
/// params before invoking tools and otherwise turns this malformed request
/// into an internal transport error.
/// </summary>
public static class McpDuplicateArgumentsGuard
{
    public static bool TryNormalize(ReadOnlyMemory<byte> raw, out byte[] normalized)
    {
        normalized = [];
        JsonDocument document;
        try { document = JsonDocument.Parse(raw); }
        catch (JsonException) { return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
                method.GetString() != "tools/call" ||
                !root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
                parameters.EnumerateObject().Count(property => property.NameEquals("arguments")) < 2)
                return false;

            using var buffer = new MemoryStream(raw.Length);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.NameEquals("params"))
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName("params");
                    writer.WriteStartObject();
                    var wroteArguments = false;
                    foreach (var parameter in property.Value.EnumerateObject())
                    {
                        if (!parameter.NameEquals("arguments"))
                        {
                            parameter.WriteTo(writer);
                            continue;
                        }

                        if (wroteArguments) continue;
                        wroteArguments = true;
                        writer.WritePropertyName("arguments");
                        writer.WriteStartObject();
                        writer.WriteString("operation", "__duplicate_envelope__");
                        writer.WriteString("operation", "__duplicate_envelope__");
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            normalized = buffer.ToArray();
            return true;
        }
    }
}
