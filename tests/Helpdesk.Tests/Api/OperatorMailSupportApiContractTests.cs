using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Helpdesk.API;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed class OperatorMailSupportApiContractTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Advertised_operator_routes_and_request_fields_match_registered_api_contracts()
    {
        using var application = factory.WithWebHostBuilder(builder =>
        {
            builder.UseIsolatedTestStorage();
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // The complete catalog includes both local-account and external identity actions.
                    ["Authentication:Mode"] = "Hybrid",
                    ["Jwt:Issuer"] = "test",
                    ["Jwt:Audience"] = "test",
                    ["Jwt:Key"] = "test-key-123456789012345678901234",
                    ["EmailIngestion:Enabled"] = "false"
                }));
        });
        using var client = application.CreateClient();
        var endpoints = application.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var operations = McpOperationCatalog.All.Where(operation => operation.RequestContract == "OperatorRequest" ||
            operation.Tool is "helpdesk_mail" or "helpdesk_email_settings" or "helpdesk_support" or "helpdesk_sla" or
            "helpdesk_datasets" or "helpdesk_automation" or "helpdesk_connections" or "helpdesk_attachments" or "helpdesk_tenant_admin" ||
            operation.Tool == "helpdesk_notifications" && operation.Operation == "error_summary" ||
            operation.Tool == "helpdesk_auth" && operation.Operation == "revoke_self").ToArray();
        Assert.NotEmpty(operations);

        foreach (var operation in operations)
        {
            var label = $"{operation.Tool}.{operation.Operation}";
            var matches = endpoints.Where(candidate =>
                Shape(candidate.RoutePattern.RawText ?? "") == Shape(operation.PathTemplate) &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(operation.HttpMethod) == true).ToArray();
            Assert.True(matches.Length == 1,
                $"{label} expects one canonical {operation.HttpMethod} {operation.PathTemplate} endpoint, but found {matches.Length}.");
            var endpoint = matches.Single();
            Assert.Contains(endpoint.Metadata, metadata => metadata is IAuthorizeData);
            Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous);
            if (operation.RequestContract == "None") continue;

            var schema = HelpdeskToolDefinitions.RequestSchema(operation);
            var properties = schema["properties"]!.AsObject();
            var method = endpoint.Metadata.GetMetadata<MethodInfo>();
            Assert.NotNull(method);
            var pathFields = Regex.Matches(operation.PathTemplate, "\\{([^}]+)\\}")
                .Select(match => match.Groups[1].Value).ToArray();
            var actualPathFields = endpoint.RoutePattern.Parameters;
            Assert.Equal(actualPathFields.Count, pathFields.Length);
            for (var index = 0; index < pathFields.Length; index++)
            {
                var parameter = method.GetParameters().Single(item => item.Name == actualPathFields[index].Name);
                AssertWireType(properties[pathFields[index]]!.AsObject(), parameter.ParameterType, label + "." + pathFields[index]);
            }

            var queries = QueryParameters(method, actualPathFields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal));
            if (endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "SearchTicketFilterOptions")
            {
                // The canonical filter endpoint deliberately reads repeated values directly
                // from HttpRequest.Query; they are real query fields, not method parameters.
                queries.Add("customerOrgId", new(typeof(string[]), false));
                queries.Add("selectedId", new(typeof(string[]), false));
            }
            foreach (var field in properties.Where(field => field.Key != "body" && !pathFields.Contains(field.Key)))
            {
                Assert.True(queries.TryGetValue(field.Key, out var query), $"{label} advertises unknown API query field {field.Key}.");
                AssertWireType(field.Value!.AsObject(), query!.Type, label + "." + field.Key);
            }
            foreach (var query in queries.Where(query => query.Value.Required))
            {
                Assert.Contains(query.Key, schema["required"]!.AsArray().Select(name => name!.GetValue<string>()));
            }

            if (properties["body"] is not JsonObject body) continue;
            var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
            if (operation.Operation is "upload_template_image" or "upload_branding_logo" || operation.Tool == "helpdesk_attachments" && operation.Operation == "upload")
            {
                Assert.True(accepts?.ContentTypes.Contains("multipart/form-data") == true, $"{label} requires canonical multipart content metadata.");
                var formParameter = method.GetParameters().SingleOrDefault(parameter => parameter.GetCustomAttribute<FromFormAttribute>() is not null);
                var formType = formParameter?.ParameterType ?? accepts!.RequestType;
                Assert.True(formType is not null, $"{label} requires a canonical bound or manually parsed form type.");
                var fileField = operation.Operation is "upload_template_image" or "upload_branding_logo" ? "File" : "Files";
                var fileProperty = formType!.GetProperty(fileField);
                Assert.True(fileProperty is not null, $"{label} canonical form {formType.Name} is missing {fileField}.");
                Assert.True(fileProperty!.PropertyType == typeof(IFormFile) || typeof(IEnumerable<IFormFile>).IsAssignableFrom(fileProperty.PropertyType),
                    $"{label}.{fileField} must accept the canonical uploaded file type.");
            }
            else
            {
                Assert.True(accepts?.RequestType is not null, $"{label} needs explicit canonical JSON request metadata.");
                if (operation.Tool == "helpdesk_automation" && operation.Operation is "create_form" or "update_form")
                {
                    body = body.DeepClone().AsObject();
                    var fields = body["properties"]!.AsObject();
                    AssertWireType(fields["schema"]!.AsObject(), typeof(RequestFormSchemaModel), label + ".body.schema");
                    fields.Remove("schema");
                    fields["jsonSchema"] = new JsonObject { ["type"] = "string" };
                }
                AssertWireType(body, accepts!.RequestType!, label + ".body");
            }
        }
    }

    private static string Shape(string path) => Regex.Replace(path.TrimEnd('/'), "\\{[^}]+\\}", "{}");

    private static Dictionary<string, QueryParameter> QueryParameters(MethodInfo method, IReadOnlySet<string> routeFields)
    {
        var result = new Dictionary<string, QueryParameter>(StringComparer.Ordinal);
        var nullability = new NullabilityInfoContext();
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.GetCustomAttribute<AsParametersAttribute>() is not null)
            {
                foreach (var property in parameter.ParameterType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    result[JsonName(property)] = new(property.PropertyType, false);
                continue;
            }
            if (routeFields.Contains(parameter.Name!) || parameter.GetCustomAttribute<FromBodyAttribute>() is not null ||
                parameter.GetCustomAttribute<FromServicesAttribute>() is not null || !IsScalar(parameter.ParameterType)) continue;
            var name = parameter.GetCustomAttribute<FromQueryAttribute>()?.Name ?? parameter.Name!;
            result[name] = new(parameter.ParameterType, !parameter.HasDefaultValue &&
                Nullable.GetUnderlyingType(parameter.ParameterType) is null &&
                nullability.Create(parameter).ReadState != NullabilityState.Nullable);
        }
        return result;
    }

    private static void AssertWireType(JsonObject schema, Type declaredType, string path)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (type == typeof(JsonObject) || type == typeof(JsonNode) || type == typeof(JsonElement)) return;
        var contracts = type.GetInterfaces().Prepend(type).ToArray();
        var dictionary = contracts.FirstOrDefault(candidate => candidate.IsGenericType &&
            candidate.GetGenericArguments()[0] == typeof(string) &&
            (candidate.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
             candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
             candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        var enumerable = contracts.FirstOrDefault(candidate => candidate.IsGenericType &&
            candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        var expected = type == typeof(string) || type == typeof(Guid) || type == typeof(DateTimeOffset) ||
            type == typeof(DateTime) || type == typeof(DateOnly) || type == typeof(TimeSpan) ? "string"
            : type == typeof(bool) ? "boolean"
            : type.IsEnum || type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ? "integer"
            : type == typeof(double) || type == typeof(float) || type == typeof(decimal) ? "number"
            : dictionary is not null ? "object"
            : type.IsArray || enumerable is not null ? "array"
            : "object";
        var types = schema["type"] is JsonArray alternatives
            ? alternatives.Select(value => value!.GetValue<string>()).ToArray()
            : [schema["type"]!.GetValue<string>()];
        Assert.True(types.Contains(expected), $"{path} must expose canonical {type.Name} as {expected}, but advertises {string.Join(", ", types)}.");
        if (dictionary is not null)
        {
            Assert.True(schema["additionalProperties"] is JsonObject, $"{path} needs the canonical dictionary value schema.");
            AssertWireType(schema["additionalProperties"]!.AsObject(), dictionary.GetGenericArguments()[1], path + "[key]");
            return;
        }
        if (expected == "array")
        {
            var itemType = type.IsArray ? type.GetElementType()! : enumerable!.GetGenericArguments()[0];
            AssertWireType(schema["items"]!.AsObject(), itemType, path + "[]");
        }
        if (expected != "object") return;
        var canonical = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(JsonName, StringComparer.Ordinal);
        foreach (var field in schema["properties"]!.AsObject())
        {
            Assert.True(canonical.TryGetValue(field.Key, out var property), $"{path}.{field.Key} is absent from canonical {type.Name}.");
            AssertWireType(field.Value!.AsObject(), property!.PropertyType, path + "." + field.Key);
        }
    }

    private static bool IsScalar(Type declaredType)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(decimal);
    }

    private static string JsonName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private sealed record QueryParameter(Type Type, bool Required);
}
