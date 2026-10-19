using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Helpdesk.Mcp.Tools;

/// <summary>Closed executable schemas for compiled operator adapters and their canonical DTOs.</summary>
internal static class OperatorRequestSchemas
{
    private const int MaximumCollectionItems = 100;
    private const int MaximumTextLength = 262144;

    public static JsonObject Create(OperatorOperation operation)
    {
        var schema = ObjectSchema();
        schema["description"] = "All operation-specific fields belong inside this request object. Put API mutation fields inside request.body; route and query selectors stay alongside body.";
        if (operation.QueryType is not null) AddProperties(schema, operation.QueryType);
        foreach (var name in McpRequestSchemas.Identifiers(operation.Path))
        {
            schema["properties"]![name] ??= name is "templateId" or "layoutId" or "brandingTenantId"
                ? new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue }
                : operation.Tool == "helpdesk_connections" && name == "mappingId" || name is "webhookId" or "namespaceId"
                    ? TypeSchema(typeof(Guid))
                : new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 };
            Require(schema, name);
        }
        if (operation.BodyType is not null)
        {
            var body = TypeSchema(operation.BodyType);
            foreach (var name in operation.RequiredBodyFields ?? []) Require(body, name);
            if (operation.BodyType == typeof(OperatorAttachmentUpload))
            {
                var fields = body["properties"]!.AsObject();
                fields["fileName"]!["pattern"] = @"^(?!\.{1,2}$)[^/\\\u0000-\u001F\u007F-\u009F]+$";
                fields["fileName"]!["examples"] = new JsonArray("fixture.png");
                fields["contentType"]!["pattern"] = operation.Operation is "upload_template_image" or "upload_branding_logo"
                    ? "^image/[A-Za-z0-9!#$&^_.+-]+$" : "^[A-Za-z0-9!#$&^_.+-]+/[A-Za-z0-9!#$&^_.+-]+$";
                fields["contentType"]!["examples"] = new JsonArray("image/png");
                fields["contentBase64"]!["pattern"] = "^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$";
                fields["contentBase64"]!["examples"] = new JsonArray("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jJZkAAAAASUVORK5CYII=");
                fields["contentBase64"]!["description"] = "Base64 file bytes, 1 byte through 1 MiB after decoding. No data-URL prefix or embedded whitespace.";
            }
            if (operation.Tool == "helpdesk_datasets" && operation.Operation == "upsert_rows")
            {
                body["x-maxUtf8Bytes"] = 1048576;
                body["description"] = "At most 1 MiB of compact JSON after wire serialization; at most 100 rows. deleteMissing must be explicit: false changes only submitted rows, true is a complete replacement.";
                var row = body["properties"]!["rows"]!["items"]!.AsObject();
                row["properties"]!["externalKey"]!["maxLength"] = 200;
                row["properties"]!["data"]!["x-maxUtf8Bytes"] = 65536;
                row["properties"]!["data"]!["description"] = "User-defined JSON columns, at most 100 keys and 64 KiB after compact wire serialization, with bounded nesting. Keys identify dataset columns, not API routes.";
                Require(row, "data");
            }
            schema["properties"]!["body"] = body;
            Require(schema, "body");
        }
        OperatorTicketOperations.RefineRequestSchema(operation, schema);
        OperatorIncidentOperations.RefineRequestSchema(operation, schema);
        return schema;
    }

    public static string? Validate(OperatorOperation operation, JsonElement? request)
    {
        if (operation.Definition.RequestContract == "None" && request is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) return null;
        if (request is null) return "request must be an object.";
        if (request.Value.GetRawText().Length > 1600000) return "request exceeds the 1.6 MB encoded request limit.";
        return ValidateNode(Create(operation), request.Value, "request", 0) ?? OperatorTicketOperations.ValidateRequest(operation, request);
    }

    public static JsonObject Normalize(OperatorOperation operation, JsonElement? request)
        => request is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
            ? new JsonObject()
            : NormalizeNode(Create(operation), request.Value).AsObject();

    private static JsonNode NormalizeNode(JsonObject schema, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var field in value.EnumerateObject())
            {
                var child = schema["properties"]?[field.Name] as JsonObject ?? schema["additionalProperties"] as JsonObject;
                result[field.Name] = child is null ? JsonNode.Parse(field.Value.GetRawText()) : NormalizeNode(child, field.Value);
            }
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array && schema["items"] is JsonObject itemSchema)
            return new JsonArray(value.EnumerateArray().Select(item => NormalizeNode(itemSchema, item)).ToArray());
        if (value.ValueKind == JsonValueKind.Number &&
            (schema["type"]?.ToString() == "integer" || schema["type"] is JsonArray types && types.Any(item => item?.ToString() == "integer")) && TryGetInteger(value, out var integer))
            return JsonValue.Create(integer)!;
        return JsonNode.Parse(value.GetRawText())!;
    }

    // JSON Schema integers include 1.0 and 1e0. Canonicalize exact integral lexemes
    // without decimal underflow turning a tiny non-integer into zero.
    private static bool TryGetInteger(JsonElement value, out long integer)
    {
        if (value.TryGetInt64(out integer)) return true;
        var text = value.GetRawText();
        var exponentIndex = text.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        var negative = mantissa.StartsWith('-');
        var dot = mantissa.IndexOf('.');
        var fractionalDigits = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) { integer = 0; return true; }
        var exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(text.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return false;
        var significant = digits.TrimEnd('0');
        var scale = (long)exponent - fractionalDigits + digits.Length - significant.Length;
        if (scale < 0 || scale + significant.Length > 19) return false;
        var normalized = (negative ? "-" : "") + significant + new string('0', (int)scale);
        return long.TryParse(normalized, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer);
    }

    private static string? ValidateNode(JsonObject schema, JsonElement value, string path, int depth)
    {
        if (depth > 20) return $"{path} exceeds the supported nesting depth.";
        if (schema["enum"] is JsonArray enumValues && !enumValues.Any(item =>
            value.ValueKind == JsonValueKind.Number && TryGetInteger(value, out var integer)
                ? item is JsonValue candidate && candidate.TryGetValue<long>(out var expected) && expected == integer || item?.ToJsonString() == integer.ToString(CultureInfo.InvariantCulture)
                : JsonNode.DeepEquals(item, JsonNode.Parse(value.GetRawText()))))
            return $"{path} must be a declared enum value.";
        var types = schema["type"] switch
        {
            JsonArray array => array.Select(item => item!.GetValue<string>()).ToArray(),
            JsonValue item => [item.GetValue<string>()],
            _ => Array.Empty<string>()
        };
        var matchesType = types.Length == 0 || types.Any(type => type switch
        {
            "null" => value.ValueKind == JsonValueKind.Null,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && TryGetInteger(value, out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            _ => false
        });
        if (!matchesType) return $"{path} has an unsupported value type.";
        if (schema["x-maxUtf8Bytes"] is JsonValue maximumBytes &&
            Encoding.UTF8.GetByteCount(JsonNode.Parse(value.GetRawText())!.ToJsonString()) > maximumBytes.GetValue<int>())
            return $"{path} exceeds its encoded JSON byte limit.";
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema["properties"] as JsonObject ?? new JsonObject();
            var supplied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!supplied.Add(field.Name)) return $"{path}.{field.Name} is duplicated.";
                var child = properties[field.Name] as JsonObject ?? schema["additionalProperties"] as JsonObject;
                if (child is null) return $"{path}.{field.Name} is not supported for this operation.";
                if (ValidateNode(child, field.Value, path + "." + field.Name, depth + 1) is { } error) return error;
            }
            if (schema["required"] is JsonArray required)
                foreach (var name in required.Select(item => item!.GetValue<string>()))
                    if (!supplied.Contains(name))
                        return $"{path}.{name} is required.";
            if (supplied.Count > MaximumCollectionItems) return $"{path} contains too many fields.";
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if (count > (schema["maxItems"]?.GetValue<int>() ?? MaximumCollectionItems) || count < (schema["minItems"]?.GetValue<int>() ?? 0)) return $"{path} has an unsupported number of items.";
            if (schema["items"] is JsonObject itemSchema)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    if (ValidateNode(itemSchema, item, $"{path}[{index++}]", depth + 1) is { } error) return error;
            }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text.Length > (schema["maxLength"]?.GetValue<int>() ?? MaximumTextLength) || text.Length < (schema["minLength"]?.GetValue<int>() ?? 0)) return $"{path} has an unsupported length.";
            if (schema["format"]?.ToString() == "uuid" && (!Guid.TryParse(text, out var id) || id == Guid.Empty)) return $"{path} must be a non-empty UUID.";
            if (schema["format"]?.ToString() == "date-time" && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) return $"{path} must be an ISO date/time.";
            if (schema["format"]?.ToString() == "date" && !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return $"{path} must be an ISO calendar date.";
            if (schema["format"]?.ToString() == "time-span" && !TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out _)) return $"{path} must use the constant duration format, for example 09:00:00.";
            if (schema["format"]?.ToString() == "time-local" && !TimeOnly.TryParseExact(text, ["HH:mm:ss", "HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return $"{path} must use the time format HH:mm:ss with optional fractional seconds.";
            if (schema["pattern"] is { } pattern && !Regex.IsMatch(text, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return $"{path} does not match the supported format.";
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (schema["x-numberFormat"]?.ToString() == "decimal" && !value.TryGetDecimal(out _))
                return $"{path} is outside the supported decimal range.";
            foreach (var name in new[] { "minimum", "maximum" })
            {
                if (schema[name] is not { } bound) continue;
                var belowMinimum = name == "minimum";
                if (value.TryGetDecimal(out var precise) && decimal.TryParse(bound.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var preciseBound))
                {
                    if (belowMinimum ? precise < preciseBound : precise > preciseBound) return $"{path} is outside the supported range.";
                }
                else if (!value.TryGetDouble(out var approximate) || !double.IsFinite(approximate) ||
                    (belowMinimum ? approximate < double.Parse(bound.ToString(), CultureInfo.InvariantCulture) : approximate > double.Parse(bound.ToString(), CultureInfo.InvariantCulture)))
                    return $"{path} is outside the supported range.";
            }
        }
        return null;
    }

    private static JsonObject TypeSchema(Type declaredType)
    {
        var nullable = Nullable.GetUnderlyingType(declaredType);
        var type = nullable ?? declaredType;
        JsonObject result;
        if (type == typeof(JsonObject)) result = new() { ["type"] = "object", ["maxProperties"] = MaximumCollectionItems, ["additionalProperties"] = JsonContentSchema(0) };
        else if (type == typeof(JsonNode) || type == typeof(JsonElement)) result = JsonContentSchema(0);
        else if (type == typeof(string)) result = new() { ["type"] = "string", ["maxLength"] = MaximumTextLength };
        else if (type == typeof(TimeSpan)) result = new() { ["type"] = "string", ["maxLength"] = 26, ["format"] = "time-span", ["pattern"] = @"^-?(\d+\.)?([01]\d|2[0-3]):[0-5]\d:[0-5]\d(\.\d{1,7})?$", ["examples"] = new JsonArray("09:00:00") };
        else if (type == typeof(Guid)) result = new() { ["type"] = "string", ["format"] = "uuid", ["not"] = new JsonObject { ["const"] = Guid.Empty.ToString("D") } };
        else if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) result = new() { ["type"] = "string", ["format"] = "date-time" };
        else if (type == typeof(DateOnly)) result = new() { ["type"] = "string", ["format"] = "date" };
        else if (type == typeof(TimeOnly)) result = new() { ["type"] = "string", ["maxLength"] = 16, ["format"] = "time-local", ["pattern"] = "^([01]\\d|2[0-3]):[0-5]\\d:[0-5]\\d(\\.\\d{1,7})?$", ["examples"] = new JsonArray("09:00:00") };
        else if (type == typeof(bool)) result = new() { ["type"] = "boolean" };
        else if (type.IsEnum) result = new() { ["type"] = "integer", ["enum"] = new JsonArray(Enum.GetValues(type).Cast<object>().Select(value => (JsonNode?)JsonValue.Create(Convert.ToInt32(value))).ToArray()) };
        else if (type == typeof(int)) result = new() { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue };
        else if (type == typeof(long)) result = new() { ["type"] = "integer", ["minimum"] = long.MinValue, ["maximum"] = long.MaxValue };
        else if (type == typeof(short)) result = new() { ["type"] = "integer", ["minimum"] = short.MinValue, ["maximum"] = short.MaxValue };
        else if (type == typeof(byte)) result = new() { ["type"] = "integer", ["minimum"] = byte.MinValue, ["maximum"] = byte.MaxValue };
        else if (type == typeof(decimal)) result = new() { ["type"] = "number", ["minimum"] = decimal.MinValue, ["maximum"] = decimal.MaxValue, ["x-numberFormat"] = "decimal" };
        else if (type == typeof(double)) result = new() { ["type"] = "number", ["minimum"] = double.MinValue, ["maximum"] = double.MaxValue };
        else if (type == typeof(float)) result = new() { ["type"] = "number", ["minimum"] = float.MinValue, ["maximum"] = float.MaxValue };
        else if (type.IsArray) result = new() { ["type"] = "array", ["maxItems"] = MaximumCollectionItems, ["items"] = TypeSchema(type.GetElementType()!) };
        else if (type.IsGenericType && type.GetGenericArguments().Length == 2 && type.GetGenericArguments()[0] == typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            result = new() { ["type"] = "object", ["maxProperties"] = MaximumCollectionItems, ["additionalProperties"] = TypeSchema(type.GetGenericArguments()[1]) };
        else if (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            result = new() { ["type"] = "array", ["maxItems"] = MaximumCollectionItems, ["items"] = TypeSchema(type.GetGenericArguments()[0]) };
        else { result = ObjectSchema(); AddProperties(result, type); }
        if (nullable is not null) AllowNull(result);
        return result;
    }

    private static void AddProperties(JsonObject schema, Type type)
    {
        var nullability = new NullabilityInfoContext();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite && property.GetCustomAttribute<JsonIgnoreAttribute>() is null))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var field = TypeSchema(property.PropertyType);
            if (nullability.Create(property).ReadState == NullabilityState.Nullable) AllowNull(field);
            if (property.GetCustomAttribute<MaxLengthAttribute>() is { } max) field[property.PropertyType == typeof(string) ? "maxLength" : "maxItems"] = max.Length;
            if (property.GetCustomAttribute<MinLengthAttribute>() is { } min) field[property.PropertyType == typeof(string) ? "minLength" : "minItems"] = min.Length;
            if (property.GetCustomAttribute<RangeAttribute>() is { } range)
            {
                field["minimum"] = Convert.ToDecimal(range.Minimum, CultureInfo.InvariantCulture);
                field["maximum"] = Convert.ToDecimal(range.Maximum, CultureInfo.InvariantCulture);
            }
            if (property.GetCustomAttribute<AllowedValuesAttribute>() is { } allowed)
                field["enum"] = new JsonArray(allowed.Values.Select(value => JsonSerializer.SerializeToNode(value)).ToArray());
            if (property.GetCustomAttribute<RegularExpressionAttribute>() is { } expression) field["pattern"] = expression.Pattern;
            if (property.GetCustomAttribute<DataTypeAttribute>()?.DataType == DataType.Password)
            {
                field["writeOnly"] = true;
                field["description"] = "Secret input used only for this explicit configuration write. Never echoed in previews, audit records or ordinary read responses. Omit to retain the saved secret where the API supports it.";
            }
            schema["properties"]![name] = field;
            if (property.GetCustomAttribute<RequiredAttribute>() is { } required) Require(schema, name, !required.AllowEmptyStrings, disallowNull: true);
        }
    }

    private static void AllowNull(JsonObject schema)
    {
        if (schema["type"] is JsonValue type) schema["type"] = new JsonArray(type.GetValue<string>(), "null");
        if (schema["enum"] is JsonArray values && !values.Any(value => value is null)) values.Add((JsonNode?)null);
    }

    private static void Require(JsonObject schema, string name, bool nonempty = false, bool disallowNull = false)
    {
        var required = schema["required"]!.AsArray();
        if (!required.Any(item => item!.GetValue<string>() == name)) required.Add(name);
        if (schema["properties"]?[name] is JsonObject field)
        {
            if (disallowNull && field["type"] is JsonArray types) field["type"] = types.First(item => item!.GetValue<string>() != "null")!.DeepClone();
            if (nonempty && field["type"]?.ToString() == "string")
            {
                field["minLength"] = Math.Max(1, field["minLength"]?.GetValue<int>() ?? 0);
                field["pattern"] ??= "\\S";
            }
        }
    }

    private static JsonObject ObjectSchema() => new() { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JsonObject(), ["required"] = new JsonArray() };

    private static JsonObject JsonContentSchema(int depth)
    {
        var schema = new JsonObject
        {
            ["type"] = depth == 5 ? new JsonArray("string", "number", "boolean", "null") : new JsonArray("object", "array", "string", "number", "boolean", "null"),
            ["maxLength"] = MaximumTextLength
        };
        if (depth < 5)
        {
            var child = JsonContentSchema(depth + 1);
            schema["maxProperties"] = MaximumCollectionItems;
            schema["maxItems"] = MaximumCollectionItems;
            schema["additionalProperties"] = child;
            schema["items"] = child.DeepClone();
        }
        return schema;
    }
}
