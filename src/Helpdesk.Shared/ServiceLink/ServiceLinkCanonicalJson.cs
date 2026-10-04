using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helpdesk.Shared.ServiceLink;

/// <summary>
/// RFC 8785 JSON canonicalization for the restricted bostec.service-link.v1 schema.
/// Numbers are nonnegative safe integers. Sets must be sorted by the typed payload
/// builder; ordered arrays are deliberately preserved here.
/// </summary>
public static class ServiceLinkCanonicalJson
{
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    public const string HashContract = "bostec.service-link.hash.v1";

    public static JsonSerializerOptions Json { get; } = CreateOptions();

    public static string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ValidateUnicode(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var canonical = new StringBuilder();
            Write(document.RootElement, canonical, null);
            return canonical.ToString();
        }
        catch (JsonException error) when (error.GetType() != typeof(JsonException))
        {
            throw new JsonException("Invalid JSON contract payload.", error);
        }
        catch (InvalidOperationException error)
        {
            // JsonDocument defers unescaping property names. Invalid surrogate
            // names must fail as invalid input, not escape as an operation error.
            throw new JsonException("Invalid Unicode in JSON contract payload.", error);
        }
    }

    public static string Hash(string json) => Digest(Canonicalize(json));

    /// <summary>Excludes only the named top-level self-hash. Nested hashes remain bound.</summary>
    public static string HashObject<T>(T value, string? excludedTopLevelField = null)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        if (excludedTopLevelField is not null && element.ValueKind != JsonValueKind.Object)
            throw new JsonException("A hash projection must be an object.");
        var canonical = new StringBuilder();
        Write(element, canonical, excludedTopLevelField);
        return Digest(canonical.ToString());
    }

    public static T Deserialize<T>(string json)
    {
        // This also rejects duplicate properties, invalid Unicode and unsupported
        // number forms before the serializer could discard or coerce them.
        Canonicalize(json);
        return JsonSerializer.Deserialize<T>(json, Json)
               ?? throw new JsonException("A contract payload cannot be null.");
    }

    public static bool IsWholeSecondUtcTimestamp(string? value) =>
        value is { Length: 20 } &&
        DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    public static DateTimeOffset ParseWholeSecondUtcTimestamp(string value)
    {
        if (!IsWholeSecondUtcTimestamp(value))
            throw new JsonException("Contract timestamps must be whole-second UTC RFC3339 values.");
        return DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        options.Converters.Add(new StrictUnicodeStringConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static string Digest(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static void Write(JsonElement value, StringBuilder output, string? excludedProperty)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties)
                {
                    ValidateUnicode(property.Name);
                    if (!names.Add(property.Name))
                        throw new JsonException("Duplicate JSON property names are prohibited.");
                }
                output.Append('{');
                var first = true;
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (property.Name == excludedProperty)
                        continue;
                    if (!first)
                        output.Append(',');
                    first = false;
                    WriteString(property.Name, output);
                    output.Append(':');
                    Write(property.Value, output, null);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                        output.Append(',');
                    firstItem = false;
                    Write(item, output, null);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                string text;
                try
                {
                    text = value.GetString()!;
                }
                catch (InvalidOperationException error)
                {
                    throw new JsonException("Invalid Unicode in JSON string.", error);
                }
                WriteString(text, output);
                break;
            case JsonValueKind.Number:
                var numericText = value.GetRawText();
                if (numericText.Any(character => character is < '0' or > '9') ||
                    !ulong.TryParse(numericText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                    number > MaximumSafeInteger)
                    throw new JsonException("Contract numbers must be nonnegative safe integers.");
                output.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new JsonException("Unsupported JSON value.");
        }
    }

    private static void WriteString(string value, StringBuilder output)
    {
        ValidateUnicode(value);
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\t': output.Append("\\t"); break;
                case '\n': output.Append("\\n"); break;
                case '\f': output.Append("\\f"); break;
                case '\r': output.Append("\\r"); break;
                default:
                    if (character < 0x20)
                        output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        output.Append(character);
                    break;
            }
        }
        output.Append('"');
    }

    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                    throw new JsonException("Unpaired UTF-16 surrogate is prohibited.");
            }
            else if (char.IsLowSurrogate(value[index]))
                throw new JsonException("Unpaired UTF-16 surrogate is prohibited.");
        }
    }

    private sealed class StrictUnicodeStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            ValidateUnicode(value);
            writer.WriteStringValue(value);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            ValidateUnicode(value);
            writer.WritePropertyName(value);
        }

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString()!;
            ValidateUnicode(value);
            return value;
        }
    }
}
