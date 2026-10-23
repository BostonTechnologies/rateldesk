using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;

internal static partial class HelpdeskCli
{
    private static ResolvedOutput ResolveOutput(InvocationContext ctx, GlobalOptions globals, string defaultView)
    {
        var output = ctx.ParseResult.GetValueForOption(globals.Output)?.Trim().ToLowerInvariant();
        if (output is not null && output is not "text" and not "json")
        {
            throw new CliValidationException("--output must be 'text' or 'json'.");
        }

        var json = ctx.ParseResult.GetValueForOption(globals.Json);
        var view = ctx.ParseResult.GetValueForOption(globals.View)?.Trim().ToLowerInvariant();
        if (view is not null && view is not "summary" and not "detail" and not "raw" and not "aggregate")
        {
            throw new CliValidationException("--view must be summary, detail, raw, or aggregate.");
        }

        var format = json || output == "json" ? "json" : "text";
        view ??= format == "json" ? "raw" : defaultView;
        var fields = ParseFields(ctx.ParseResult.GetValueForOption(globals.Fields));
        if (view == "raw" && fields is not null)
        {
            throw new CliValidationException("--fields is only supported with summary, detail, or aggregate views. Use --view summary or remove --fields for raw output.");
        }

        return new ResolvedOutput(format, view, fields);
    }

    private static async Task SendAsync(CliRuntime runtime, GlobalOptions globals, InvocationContext ctx, HttpMethod method, string path, string? body = null)
    {
        var response = await SendStringAsync(runtime, globals, ctx, method, path, body).ConfigureAwait(false);
        if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
        {
            await WriteResponseBodyAsync(runtime, response.Body, response.StatusCode, ctx, globals).ConfigureAwait(false);
        }
    }

    private static async Task<ApiStringResponse> SendStringAsync(CliRuntime runtime, GlobalOptions globals, InvocationContext ctx, HttpMethod method, string path, string? body)
    {
        var loaded = LoadConfig(ctx, globals);
        var result = await SendRawAsync(runtime, loaded.Resolved!, method, path, authenticated: true, body, ctx.GetCancellationToken()).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var message = result.StatusCode == 405
                ? $"Helpdesk API request failed with HTTP 405 for {method.Method} {path}. The CLI route may not match the API route table."
                : $"Helpdesk API request failed with HTTP {result.StatusCode} for {method.Method} {path}.";
            if (string.IsNullOrWhiteSpace(result.Body))
            {
                message += " The API returned an empty response body; check the route and server logs.";
            }

            throw new CliRemoteException("api_request_failed", message, result.StatusCode, result.Body);
        }

        return result;
    }

    private static async Task<ApiStringResponse> SendRawAsync(CliRuntime runtime, ResolvedCliConfig config, HttpMethod method, string path, bool authenticated, string? body, CancellationToken ct)
    {
        using var client = authenticated
            ? await runtime.CreateAuthenticatedClientAsync(config, ct).ConfigureAwait(false)
            : runtime.CreateHttpClient(config.ApiBaseUrl);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        return new ApiStringResponse((int)response.StatusCode, response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static LoadedConfig LoadConfig(InvocationContext ctx, GlobalOptions globals, bool requireAuth = true, bool allowMissingFile = false)
    {
        var selectedPath = SelectedConfigurationPath(ctx, globals);
        var overrides = new Helpdesk.AgentClient.AgentClientConfiguration(
            ctx.ParseResult.GetValueForOption(globals.ApiBaseUrl),
            ctx.ParseResult.GetValueForOption(globals.TokenUrl),
            ctx.ParseResult.GetValueForOption(globals.ClientId),
            ctx.ParseResult.GetValueForOption(globals.Username),
            ctx.ParseResult.GetValueForOption(globals.AppPassword),
            ctx.ParseResult.GetValueForOption(globals.Scope),
            ctx.ParseResult.GetValueForOption(globals.AgentUserEmail));
        try
        {
            var effective = Helpdesk.AgentClient.AgentClientConfigurationResolver.ResolveEffective(Helpdesk.AgentClient.ConfigurationSurface.Cli, selectedPath, overrides, validate: requireAuth, allowMissingFile: allowMissingFile, allowLegacySecretCommandOptions: true);
            var merged = CliConfig.FromAgentConfiguration(effective.Configuration);
            return new LoadedConfig(CliConfig.Empty, merged, requireAuth ? merged.Resolve() : null);
        }
        catch (Helpdesk.AgentClient.AgentClientValidationException exception) { throw new CliValidationException(exception.Message); }
    }

    private static string? SelectedConfigurationPath(InvocationContext ctx, GlobalOptions globals)
    {
        var profile = ctx.ParseResult.GetValueForOption(globals.Profile);
        var path = ctx.ParseResult.GetValueForOption(globals.ConfigPath);
        if (!string.IsNullOrWhiteSpace(profile) && !string.IsNullOrWhiteSpace(path)) throw new CliValidationException("Choose either --profile or --config.");
        return string.IsNullOrWhiteSpace(profile) ? path : new Helpdesk.AgentClient.ConfigurationProfiles().PathFor(profile);
    }

    private static async Task WriteResponseBodyAsync(CliRuntime runtime, string body, int statusCode, InvocationContext ctx, GlobalOptions globals)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            await WriteJsonAsync(runtime, new { status = statusCode }, ctx, globals).ConfigureAwait(false);
            return;
        }

        if (ctx.ParseResult.GetValueForOption(globals.Output) == "text" && WasSpecified(ctx.ParseResult, globals.Output))
        {
            await runtime.Out.WriteLineAsync(body).ConfigureAwait(false);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            await runtime.Out.WriteLineAsync(JsonSerializer.Serialize(doc.RootElement, ctx.ParseResult.GetValueForOption(globals.Pretty) ? PrettyJsonOptions : JsonOptions)).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(runtime, new { status = statusCode, body }, ctx, globals).ConfigureAwait(false);
        }
    }

    private static Task WriteRawBodyAsync(CliRuntime runtime, string body, InvocationContext ctx, GlobalOptions globals)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return runtime.Out.WriteLineAsync(JsonSerializer.Serialize(doc.RootElement, ctx.ParseResult.GetValueForOption(globals.Pretty) ? PrettyJsonOptions : JsonOptions));
        }
        catch (JsonException)
        {
            return runtime.Out.WriteLineAsync(body);
        }
    }

    private static Task WriteJsonPayloadAsync(CliRuntime runtime, JsonObject payload, InvocationContext ctx, GlobalOptions globals)
        => runtime.Out.WriteLineAsync(payload.ToJsonString(ctx.ParseResult.GetValueForOption(globals.Pretty) ? PrettyJsonOptions : JsonOptions));

    private static async Task WriteJsonAsync(CliRuntime runtime, object payload, InvocationContext ctx, GlobalOptions globals)
    {
        if (ctx.ParseResult.GetValueForOption(globals.Output) == "text" && WasSpecified(ctx.ParseResult, globals.Output))
        {
            await runtime.Out.WriteLineAsync(payload.ToString()).ConfigureAwait(false);
            return;
        }

        await runtime.Out.WriteLineAsync(JsonSerializer.Serialize(payload, ctx.ParseResult.GetValueForOption(globals.Pretty) ? PrettyJsonOptions : JsonOptions)).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(CliRuntime runtime, string code, string message, int exitCode, string? responseBody = null, JsonObject? details = null)
        => runtime.Error.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = new { code, message, exitCode, responseBody, details } }, JsonOptions));

    private static (string Code, string Message, int ExitCode, string? ResponseBody, JsonObject? Details) MapException(Exception exception) => exception switch
    {
        CliValidationException ex => ("validation_error", ex.Message, CliExitCodes.ValidationError, null, ex.Details),
        CliRemoteException ex => (ex.Code, ex.Message, ex.StatusCode is 401 or 403 ? CliExitCodes.AuthError : CliExitCodes.RemoteError, ex.ResponseBody, null),
        HttpRequestException ex => ("remote_request_failed", ex.Message, CliExitCodes.RemoteError, null, null),
        TaskCanceledException => ("remote_timeout", "Remote request timed out.", CliExitCodes.RemoteError, null, null),
        _ => ("unexpected_error", exception.Message, CliExitCodes.RemoteError, null, null)
    };

    private static string Query(InvocationContext ctx, params (Option option, string queryName)[] options)
        => Query(options.Select(option => (option.queryName, ValueToString(ctx.ParseResult.GetValueForOption(option.option)))).ToArray());

    private static bool WasSpecified(CliParseResult parseResult, Option option)
        => parseResult.WasSpecified(option);

    private static string Query(params (string name, string? value)[] values)
    {
        var items = values.Where(x => !string.IsNullOrWhiteSpace(x.value)).Select(x => $"{Escape(x.name)}={Escape(x.value!)}").ToArray();
        return items.Length == 0 ? string.Empty : "?" + string.Join("&", items);
    }

    private static int? ResolvePageSize(int? pageSize, int? limit, int? take, int? defaultValue)
        => pageSize ?? limit ?? take ?? defaultValue;

    private static IReadOnlySet<string>? ParseFields(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static JsonObject FilterFields(JsonObject row, IReadOnlySet<string>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return row;
        }

        var unknown = fields.Where(field => !row.ContainsKey(field)).ToArray();
        if (unknown.Length > 0)
        {
            throw new CliValidationException($"Unknown field(s): {string.Join(", ", unknown)}. Supported fields: {string.Join(", ", row.Select(x => x.Key).OrderBy(x => x))}.");
        }

        var filtered = new JsonObject();
        foreach (var field in fields)
        {
            filtered[field] = CloneNode(row[field]);
        }

        return filtered;
    }

    private static void AddEnum(JsonObject target, JsonObject source, string field, Type enumType)
    {
        var value = GetInt(source, field);
        if (!value.HasValue)
        {
            return;
        }

        var info = EnumInfo(enumType, value.Value);
        target[field] = value.Value;
        target[field + "Name"] = info.Name;
        target[field + "Label"] = info.Label;
    }

    private static void AddEnumFlexible(JsonObject target, JsonObject source, string field, Type enumType)
    {
        var value = GetInt(source, field);
        string? name = null;
        if (!value.HasValue)
        {
            name = GetString(source, field);
            if (!string.IsNullOrWhiteSpace(name) && Enum.TryParse(enumType, name, ignoreCase: true, out var parsed))
            {
                value = Convert.ToInt32(parsed, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (!value.HasValue)
        {
            return;
        }

        var info = EnumInfo(enumType, value.Value);
        target[field] = value.Value;
        target[field + "Name"] = string.IsNullOrWhiteSpace(name) ? info.Name : info.Name;
        target[field + "Label"] = info.Label;
    }

    private static EnumDisplay EnumInfo(Type enumType, int value)
    {
        var name = Enum.GetName(enumType, value) ?? value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var member = enumType.GetMember(name).FirstOrDefault();
        var label = member?.GetCustomAttribute<DisplayAttribute>()?.Name ?? SplitPascalCase(name);
        return new EnumDisplay(name, label);
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsWhiteSpace(value[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static JsonArray CountByEnum(IEnumerable<JsonObject> items, string valueField, string nameField, string labelField)
    {
        var groups = items
            .Where(item => GetInt(item, valueField).HasValue)
            .GroupBy(item => new
            {
                Value = GetInt(item, valueField)!.Value,
                Name = GetString(item, nameField) ?? GetInt(item, valueField)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Label = GetString(item, labelField) ?? GetString(item, nameField) ?? GetInt(item, valueField)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
            .OrderBy(group => group.Key.Value);

        var result = new JsonArray();
        foreach (var group in groups)
        {
            result.Add(new JsonObject
            {
                [valueField] = group.Key.Value,
                [nameField] = group.Key.Name,
                [labelField] = group.Key.Label,
                ["count"] = group.Count()
            });
        }

        return result;
    }

    private static JsonArray CountByString(IEnumerable<JsonObject> items, string field, int count)
    {
        var result = new JsonArray();
        foreach (var group in items
            .Select(item => GetString(item, field))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(count))
        {
            result.Add(new JsonObject
            {
                ["name"] = group.Key,
                ["count"] = group.Count()
            });
        }

        return result;
    }

    private static string RenderTable(string? title, IReadOnlyList<Dictionary<string, string?>> rows, string? footer)
    {
        if (rows.Count == 0)
        {
            return string.IsNullOrWhiteSpace(title) ? "(no rows)" : title + Environment.NewLine + Environment.NewLine + "(no rows)";
        }

        var columns = rows[0].Keys.ToArray();
        var widths = columns.ToDictionary(
            column => column,
            column => Math.Min(32, Math.Max(column.Length, rows.Max(row => Truncate(row.GetValueOrDefault(column), 48).Length))));
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            lines.Add(title);
            lines.Add(string.Empty);
        }

        lines.Add(string.Join("  ", columns.Select(column => column.PadRight(widths[column]))));
        foreach (var row in rows)
        {
            lines.Add(string.Join("  ", columns.Select(column => Truncate(row.GetValueOrDefault(column), widths[column]).PadRight(widths[column]))));
        }

        if (!string.IsNullOrWhiteSpace(footer))
        {
            lines.Add(string.Empty);
            lines.Add(footer);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static Dictionary<string, string?> ProjectFieldsForTable(JsonObject row)
        => row.ToDictionary(
            item => item.Key.ToUpperInvariant(),
            item => item.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<int>(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean.ToString(),
                _ => item.Value.ToJsonString(JsonOptions)
            });

    private static string Truncate(string? value, int length)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? "-" : value.ReplaceLineEndings(" ").Trim();
        if (clean.Length <= length)
        {
            return clean;
        }

        return length <= 1 ? clean[..length] : clean[..(length - 1)] + "…";
    }

    private static string? BuildPageFooter(JsonObject envelope)
        => $"Page {GetInt(envelope, "page") ?? 1}  Page size {GetInt(envelope, "pageSize") ?? 0}  Total {GetInt(envelope, "totalCount") ?? 0}";

    private static string? FormatDate(string? value)
        => ParseDate(value)?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string? FormatPriority(string? value) => value switch
    {
        "Critical" => "P0",
        "High" => "P1",
        "Medium" => "P2",
        "Low" => "P3",
        _ => value
    };

    private static void AddCountLines(List<string> lines, JsonArray? items, string labelField)
    {
        if (items is null || items.Count == 0)
        {
            lines.Add("  -");
            return;
        }

        foreach (var item in items.OfType<JsonObject>())
        {
            lines.Add($"  {GetString(item, labelField) ?? "-"}: {GetInt(item, "count") ?? 0}");
        }
    }

    private static void AddNameCountLines(List<string> lines, JsonArray? items)
    {
        if (items is null || items.Count == 0)
        {
            lines.Add("  -");
            return;
        }

        foreach (var item in items.OfType<JsonObject>())
        {
            lines.Add($"  {GetString(item, "name") ?? "-"}: {GetInt(item, "count") ?? 0}");
        }
    }

    private static JsonNode? CloneNode(JsonNode? node)
        => node is null ? null : JsonNode.Parse(node.ToJsonString(JsonOptions));

    private static JsonObject CloneObject(JsonObject source)
        => JsonNode.Parse(source.ToJsonString(JsonOptions)) as JsonObject ?? new JsonObject();

    private static JsonArray ToJsonArray(this IEnumerable<JsonObject> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray ToJsonArray(this IEnumerable<JsonNode?> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static int CountArray(JsonNode? node)
        => node is JsonArray array ? array.Count : 0;

    private static JsonNode ParseBodyOrStatus(ApiStringResponse response)
    {
        try
        {
            return JsonNode.Parse(response.Body) ?? new JsonObject { ["status"] = response.StatusCode, ["ok"] = response.IsSuccess };
        }
        catch (JsonException)
        {
            return new JsonObject { ["status"] = response.StatusCode, ["ok"] = response.IsSuccess, ["body"] = response.Body };
        }
    }

    private static string? GetString(JsonNode? node, string property)
        => node is JsonObject obj && obj[property] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static int? GetInt(JsonNode? node, string property)
    {
        if (node is not JsonObject obj || obj[property] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetValue<string>(out var stringValue) && int.TryParse(stringValue, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? GetBool(JsonNode? node, string property)
        => node is JsonObject obj && obj[property] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;

    private static T? GetClientOption<T>(InvocationContext ctx, IEnumerable<(Option option, string queryName)> options, string queryName)
    {
        var option = options.LastOrDefault(item => string.Equals(item.queryName, queryName, StringComparison.Ordinal)).option;
        return option is null ? default : (T?)ctx.ParseResult.GetValueForOption(option);
    }

    private static bool MatchesClientQuery(JsonObject item, string query)
    {
        foreach (var value in item.Select(kvp => kvp.Value).OfType<JsonValue>())
        {
            if (value.TryGetValue<string>(out var text) && text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildBodyFromOptions(InvocationContext ctx, IEnumerable<(Option<string?> option, string jsonName)> fields)
    {
        var obj = new JsonObject();
        foreach (var (option, jsonName) in fields)
        {
            var value = ctx.ParseResult.GetValueForOption(option);
            if (value is null)
            {
                continue;
            }

            obj[jsonName] = ParseJsonValue(value);
        }

        return obj.ToJsonString(JsonOptions);
    }

    private static JsonNode? ParseJsonValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) || decimal.TryParse(trimmed, out _))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // Treat malformed JSON-looking values as strings so agent commands still return validation from the API.
            }
        }

        return JsonValue.Create(value);
    }

    private static string NormalizeKnownBodyEnums(string content)
    {
        var node = JsonNode.Parse(content);
        if (node is not JsonObject obj)
        {
            return content;
        }

        NormalizeKnownBodyEnums(obj);
        return obj.ToJsonString(JsonOptions);
    }

    private static void NormalizeKnownBodyEnums(JsonObject obj)
    {
        NormalizeBodyEnum(obj, "priority", typeof(TicketPriority));
        NormalizeBodyEnum(obj, "state", typeof(TicketState));
        NormalizeBodyEnum(obj, "newState", typeof(TicketState));
        NormalizeBodyEnum(obj, "lifecycleState", typeof(ChangeLifecycleState));
    }

    private static void NormalizeBodyEnum(JsonObject obj, string field, Type enumType)
    {
        if (obj[field] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            obj[field] = numeric;
            return;
        }

        foreach (var raw in Enum.GetValues(enumType))
        {
            numeric = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
            var info = EnumInfo(enumType, numeric);
            if (string.Equals(text, info.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, info.Label, StringComparison.OrdinalIgnoreCase))
            {
                obj[field] = numeric;
                return;
            }
        }

        var values = Enum.GetValues(enumType)
            .Cast<object>()
            .Select(raw =>
            {
                var value = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
                var info = EnumInfo(enumType, value);
                return $"{value}/{info.Name}";
            });
        throw new CliValidationException($"Unknown {field} value '{text}'. Supported values: {string.Join(", ", values)}.");
    }

    private static string? ReadBody(string? body, string? bodyFile, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(bodyFile))
        {
            return File.ReadAllText(bodyFile);
        }

        return body ?? fallback;
    }

    private static string Fill(string template, string? id)
        => template.Replace("{id}", Escape(id ?? throw new CliValidationException("id is required.")), StringComparison.Ordinal)
            .Replace("{ordinal}", "1", StringComparison.Ordinal);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static HttpMethod ResolveMethod(string method) => method.ToLowerInvariant() switch
    {
        "get" => HttpMethod.Get,
        "post" => HttpMethod.Post,
        "put" => HttpMethod.Put,
        "delete" => HttpMethod.Delete,
        _ => throw new CliValidationException("raw method must be get, post, put, or delete.")
    };

    private static void EnsureAllowedRawPath(string path)
    {
        var allowed = new[]
        {
            "/api/v1/auth/ai-agent/status",
            "/api/v1/auth/me",
            "/api/v1/ops/ai-agent/logs",
            "/api/v1/incidents",
            "/api/v1/requests",
            "/api/v1/request-tasks",
            "/api/v1/requestTasks",
            "/api/v1/changes",
            "/api/v1/tickets",
            "/api/v1/organizations",
            "/api/v1/customers",
            "/api/v1/users",
            "/api/v1/admin/role-definitions",
            "/api/v1/categories",
            "/api/v1/services",
            "/api/v1/service-items",
            "/api/v1/request-forms",
            "/api/v1/self-service",
            "/api/v1/notifications",
            "/api/v1/global-search",
            "/api/v1/dashboard",
            "/api/v1/email",
            "/api/v1/email-settings",
            "/api/v1/email-templates",
            "/api/v1/email-layouts",
            "/api/v1/slas",
            "/api/v1/sla",
            "/api/v1/resources",
            "/api/v1/kb",
            "/api/v1/ai/providers",
            "/api/v1/admin/orchestration",
            "/api/v1/admin/tenants",
            "/api/v1/system",
            "/health"
        };

        if (!allowed.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CliValidationException($"Raw path '{path}' is not in the Helpdesk CLI operator allow-list.");
        }
    }

    private static long? ExtractNextSince(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("nextSince", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ResolveAgentUserIdAsync(CliRuntime runtime, GlobalOptions globals, InvocationContext ctx, string? explicitEmail)
    {
        var loaded = LoadConfig(ctx, globals);
        var email = string.IsNullOrWhiteSpace(explicitEmail)
            ? loaded.Resolved!.AgentUserEmail
            : explicitEmail;
        if (string.IsNullOrWhiteSpace(email))
            throw new CliValidationException("An agent user email is required for assign-self.");
        var response = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/api/v1/users/by-email/" + Escape(email), authenticated: true, null, ctx.GetCancellationToken()).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new CliValidationException($"Agent user '{email}' could not be resolved for assign-self.");
        }

        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
        {
            return id.GetString()!;
        }

        throw new CliValidationException($"Agent user '{email}' response did not include an id.");
    }

    private static JsonArray ParseIds(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var node = JsonNode.Parse(trimmed) as JsonArray;
            return node ?? throw new CliValidationException("--ids must be a JSON array or comma-separated list.");
        }

        var array = new JsonArray();
        foreach (var item in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            array.Add(item);
        }

        return array;
    }

    private static string? ValueToString(object? value) => value switch
    {
        null => null,
        string s when string.IsNullOrWhiteSpace(s) => null,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };

    private static CliConfig Redact(CliConfig config) => config with
    {
        AuthentikAppPassword = string.IsNullOrWhiteSpace(config.AuthentikAppPassword) && string.IsNullOrWhiteSpace(config.AuthentikAppPasswordFile) ? null : "***",
        IntegrationCredential = string.IsNullOrWhiteSpace(config.IntegrationCredential) && string.IsNullOrWhiteSpace(config.IntegrationCredentialFile) ? null : "***",
        AuthentikAppPasswordFile = string.IsNullOrWhiteSpace(config.AuthentikAppPasswordFile) ? null : "***",
        IntegrationCredentialFile = string.IsNullOrWhiteSpace(config.IntegrationCredentialFile) ? null : "***"
    };

    private static string? GetConfigValue(CliConfig config, string key, bool redact) => NormalizeConfigKey(key) switch
    {
        "apiBaseUrl" => config.ApiBaseUrl,
        "authentikTokenUrl" => config.AuthentikTokenUrl,
        "authentikClientId" => config.AuthentikClientId,
        "authentikUsername" => config.AuthentikUsername,
        "authentikAppPassword" => redact && !string.IsNullOrWhiteSpace(config.AuthentikAppPassword) ? "***" : config.AuthentikAppPassword,
        "credentialMode" => config.CredentialMode,
        "integrationCredential" => redact && !string.IsNullOrWhiteSpace(config.IntegrationCredential) ? "***" : config.IntegrationCredential,
        "integrationCredentialFile" => redact && !string.IsNullOrWhiteSpace(config.IntegrationCredentialFile) ? "***" : config.IntegrationCredentialFile,
        "authentikAppPasswordFile" => redact && !string.IsNullOrWhiteSpace(config.AuthentikAppPasswordFile) ? "***" : config.AuthentikAppPasswordFile,
        "authentikScope" => config.AuthentikScope,
        "agentUserEmail" => config.AgentUserEmail,
        _ => throw new CliValidationException("Unknown configuration key.")
    };

    private static CliConfig SetConfigValue(CliConfig config, string key, string? value) => NormalizeConfigKey(key) switch
    {
        "apiBaseUrl" => config with { ApiBaseUrl = value },
        "authentikTokenUrl" => config with { AuthentikTokenUrl = value },
        "authentikClientId" => config with { AuthentikClientId = value },
        "authentikUsername" => config with { AuthentikUsername = value },
        "authentikAppPassword" => config with { AuthentikAppPassword = value },
        "credentialMode" => config with { CredentialMode = value },
        "integrationCredential" => config with { IntegrationCredential = value },
        "integrationCredentialFile" => config with { IntegrationCredentialFile = value, IntegrationCredential = null },
        "authentikAppPasswordFile" => config with { AuthentikAppPasswordFile = value, AuthentikAppPassword = null },
        "authentikScope" => config with { AuthentikScope = value },
        "agentUserEmail" => config with { AgentUserEmail = value },
        _ => throw new CliValidationException("Unknown configuration key.")
    };

    private static string NormalizeConfigKey(string key) => key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "apibaseurl" or "bthelpdeskapibaseurl" => "apiBaseUrl",
        "authentiktokenurl" or "btauthentiktokenurl" => "authentikTokenUrl",
        "authentikclientid" or "btauthentikclientid" => "authentikClientId",
        "authentikusername" or "btauthentikusername" => "authentikUsername",
        "authentikapppassword" or "btauthentikapppassword" => "authentikAppPassword",
        "authentikapppasswordfile" => "authentikAppPasswordFile",
        "integrationcredentialfile" => "integrationCredentialFile",
        "authentikscope" or "btauthentikscope" => "authentikScope",
        "agentuseremail" or "bthelpdeskagentuseremail" => "agentUserEmail",
        _ => key
    };

    private sealed record LoadedConfig(CliConfig File, CliConfig Overrides, ResolvedCliConfig? Resolved);
    private sealed record ApiStringResponse(int StatusCode, bool IsSuccess, string Body)
    {
        public object ToHealth(string name) => new { name, status = StatusCode, ok = IsSuccess, body = Body };
    }
    private sealed record ResolvedOutput(string Format, string View, IReadOnlySet<string>? Fields);
    private sealed record EnumDisplay(string Name, string Label);
    private sealed record IncidentCustomerResolution(string CustomerId, string OrganizationId, string Email);
    private sealed record SchemaEntry(
        string Group,
        string Command,
        string Description,
        string Safety,
        string Method,
        string Path,
        IReadOnlyList<string> Options,
        IReadOnlyList<string> Views,
        string DefaultView,
        IReadOnlyList<string> EnumFields,
        IReadOnlyList<string> Examples,
        IReadOnlyList<string>? RouteCaveats = null)
    {
        public IReadOnlyList<string> RouteCaveats { get; } = RouteCaveats ?? [];
    }
    private sealed record SchemaField(
        string Name,
        string Option,
        string Type,
        bool Required,
        string Notes,
        string? EnumName = null,
        Type? EnumType = null);
    private sealed record BodyOutputOptions(bool IncludeBody, bool IncludeHtml, string BodyFormat, int BodyLines, int Truncate)
    {
        public static BodyOutputOptions From(InvocationContext ctx, GlobalOptions globals)
        {
            var bodyFormat = ctx.ParseResult.GetValueForOption(globals.BodyFormat)?.Trim().ToLowerInvariant();
            if (bodyFormat is not null && bodyFormat is not "none" and not "text" and not "html")
            {
                throw new CliValidationException("--body-format must be none, text, or html.");
            }

            var includeHtml = ctx.ParseResult.GetValueForOption(globals.IncludeHtml) || bodyFormat == "html";
            var includeBody = ctx.ParseResult.GetValueForOption(globals.IncludeBody) || bodyFormat is "text" or "html";
            return new BodyOutputOptions(
                includeBody,
                includeHtml,
                bodyFormat ?? "text",
                Math.Max(1, ctx.ParseResult.GetValueForOption(globals.BodyLines) ?? DefaultBodyLines),
                Math.Max(16, ctx.ParseResult.GetValueForOption(globals.Truncate) ?? DefaultTruncate));
        }
    }

    private sealed class GlobalOptions
    {
        public Option<string?> ApiBaseUrl { get; } = new("--api-base-url") { Description = "Helpdesk API base URL." };
        public Option<string?> TokenUrl { get; } = new("--token-url") { Description = "Authentik token URL." };
        public Option<string?> ClientId { get; } = new("--client-id") { Description = "Authentik client id." };
        public Option<string?> Username { get; } = new("--username") { Description = "Authentik service username." };
        public Option<string?> AppPassword { get; } = new("--app-password") { Description = "Authentik service-user app password." };
        public Option<string?> Scope { get; } = new("--scope") { Description = "Authentik OAuth scope." };
        public Option<string?> AgentUserEmail { get; } = new("--agent-user-email") { Description = "Helpdesk user email for assign-self." };
        public Option<string?> ConfigPath { get; } = new("--config") { Description = "CLI config file path." };
        public Option<string?> Profile { get; } = new("--profile") { Description = "Explicit CLI profile name; isolated from MCP profiles." };
        public Option<string> Output { get; } = new("--output") { Description = "Output format: text or json.", DefaultValueFactory = _ => "text" };
        public Option<bool> Json { get; } = new("--json") { Description = "Alias for --output json --view raw unless --view is supplied." };
        public Option<string?> View { get; } = new("--view") { Description = "Output view: summary, detail, raw, or aggregate." };
        public Option<string?> Fields { get; } = new("--fields") { Description = "Comma-separated projected fields for summary/detail views." };
        public Option<bool> IncludeBody { get; } = new("--include-body") { Description = "Include long plain-text body fields in projected detail output." };
        public Option<bool> IncludeHtml { get; } = new("--include-html") { Description = "Include HTML fields in projected detail output." };
        public Option<string?> BodyFormat { get; } = new("--body-format") { Description = "Body format: none, text, or html." };
        public Option<int?> BodyLines { get; } = new("--body-lines") { Description = "Maximum body lines for projected text output." };
        public Option<int?> Truncate { get; } = new("--truncate") { Description = "Maximum characters for projected long fields." };
        public Option<bool> Pretty { get; } = new("--pretty") { Description = "Pretty-print JSON output." };
        public Option<bool> Quiet { get; } = new("--quiet") { Description = "Suppress normal output." };

        public void AddTo(Command command)
        {
            foreach (var option in new Option[] { ApiBaseUrl, TokenUrl, ClientId, Username, AppPassword, Scope, AgentUserEmail, ConfigPath, Profile, Output, Json, View, Fields, IncludeBody, IncludeHtml, BodyFormat, BodyLines, Truncate, Pretty, Quiet })
            {
                command.AddGlobalOption(option);
            }
        }
    }
}
