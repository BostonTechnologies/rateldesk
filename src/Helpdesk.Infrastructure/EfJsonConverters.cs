using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Helpdesk.Infrastructure.Persistence;

public static class EfJsonConverters
{
    // Converted mutable lists need an independent snapshot. Reference equality
    // otherwise misses in-place listener, approver and calendar edits when a
    // tracked entity is saved without marking every unrelated column modified.
    public static ValueComparer<List<T>> JsonListComparer<T>() => new(
        (left, right) => SerializeList(left) == SerializeList(right),
        value => SerializeList(value).GetHashCode(StringComparison.Ordinal),
        value => SnapshotList(value));

    private static string SerializeList<T>(List<T>? value) =>
        JsonSerializer.Serialize(value, (JsonSerializerOptions?)null);

    private static List<T> SnapshotList<T>(List<T> value) =>
        JsonSerializer.Deserialize<List<T>>(SerializeList(value), (JsonSerializerOptions?)null)!;

    public static ValueConverter<List<T>, string> JsonList<T>() where T : class, new()
    {
        return new ValueConverter<List<T>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v)
                ? new List<T>()
                : JsonSerializer.Deserialize<List<T>>(v, (JsonSerializerOptions?)null) ?? new());
    }

    public static ValueConverter<float[], string> FloatArrayToJson { get; } =
        new(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<float[]>(v, (JsonSerializerOptions?)null) ?? Array.Empty<float>());

    public static ValueConverter<List<string>, string> StringListToJson { get; } =
        new(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new());

    public static ValueConverter<List<T>, string> EnumListToJson<T>() where T : struct, Enum =>
        new(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v) ? new List<T>() : JsonSerializer.Deserialize<List<T>>(v, (JsonSerializerOptions?)null) ?? new());

    public static ValueConverter<JsonDocument, string> JsonDocumentToString { get; } =
        new(
            v => v.RootElement.GetRawText(),
            v => ParseJsonDocument(v));

    private static JsonDocument ParseJsonDocument(string? value)
    {
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value!, new JsonDocumentOptions());
    }
}
