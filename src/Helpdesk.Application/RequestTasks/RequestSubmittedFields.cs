using System.Text.Json;
using Helpdesk.Shared.DTOs.Request;

namespace Helpdesk.Application.RequestTasks;

public static class RequestSubmittedFields
{
    public static List<RequestSubmittedFieldDto> Read(string? payloadJson, bool includeBoundLabels)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return [new("payload", Format(payload, default))];
            }

            JsonElement labels = default;
            if (includeBoundLabels)
            {
                payload.TryGetProperty("_boundLabels", out labels);
            }

            var fields = new List<RequestSubmittedFieldDto>();
            foreach (var property in payload.EnumerateObject())
            {
                if (property.NameEquals("_boundLabels"))
                {
                    continue;
                }

                JsonElement label = default;
                if (labels.ValueKind == JsonValueKind.Object)
                {
                    labels.TryGetProperty(property.Name, out label);
                }

                fields.Add(new RequestSubmittedFieldDto(property.Name, Format(property.Value, label)));
            }

            return fields;
        }
        catch (JsonException)
        {
            return [new("payload", payloadJson)];
        }
    }

    private static string Format(JsonElement value, JsonElement label)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return label.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String
                ? LabelOrValue(label, value)
                : ScalarText(value);
        }

        if (value.GetArrayLength() == 0)
        {
            return "—";
        }

        var aligned = label.ValueKind == JsonValueKind.Array && label.GetArrayLength() == value.GetArrayLength();
        return string.Join(" · ", value.EnumerateArray().Select((item, index) => aligned ? LabelOrValue(label[index], item) : ScalarText(item)));
    }

    private static string LabelOrValue(JsonElement label, JsonElement value) =>
        label.ValueKind == JsonValueKind.String && label.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text : ScalarText(value);

    private static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => value.GetRawText()
    };
}
