using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

static string Shape(Type type) => type.IsGenericType
    ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(Shape)) + ">"
    : type.FullName!;

if (args.Length != 4) throw new ArgumentException("Expected dependency directory, control assembly, candidate assembly and output path.");

var dependencies = Path.GetFullPath(args[0]);
var result = new Dictionary<string, object>();
var surfaces = new Dictionary<string, Dictionary<string, string>>();
foreach (var (side, path) in new[] { ("control", args[1]), ("candidate", args[2]) })
{
    var context = new AssemblyLoadContext(side);
    context.Resolving += (_, name) =>
    {
        foreach (var directory in new[] { dependencies, Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly.Location)! })
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
        }
        return null;
    };
    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
    var models = assembly.GetTypes().Where(type => type.Namespace?.StartsWith("Microsoft.Graph", StringComparison.Ordinal) == true && type.IsPublic && !type.IsNested && (type.IsEnum || type.GetInterfaces().Any(contract => contract.FullName == "Microsoft.Kiota.Abstractions.Serialization.IParsable")));
    var descriptions = new Dictionary<string, string>();
    foreach (var type in models)
    {
        var fields = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name + ":" + Shape(property.PropertyType)).Order(StringComparer.Ordinal).ToArray();
        string[] wire = [];
        string? odataType = null;
        if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null && type.GetMethod("GetFieldDeserializers") is { } method)
        {
            var instance = Activator.CreateInstance(type);
            wire = ((IDictionary)method.Invoke(instance, null)!).Keys.Cast<string>().Order(StringComparer.Ordinal).ToArray();
            odataType = type.GetProperty("OdataType")?.GetValue(instance)?.ToString();
        }
        descriptions[type.FullName!] = JsonSerializer.Serialize(new { fields, wire, odataType,
            enums = type.IsEnum ? Enum.GetNames(type).Zip(Enum.GetValues(type).Cast<object>(), (name, value) => name + ":" + Convert.ToInt32(value)).ToArray() : [] });
    }
    surfaces[side] = descriptions;
    result[side + "ModelCount"] = descriptions.Count;
}
var differences = surfaces["candidate"].Where(pair => !surfaces["control"].TryGetValue(pair.Key, out var shape) || pair.Value != shape)
    .Select(pair => new { type = pair.Key, control = surfaces["control"].GetValueOrDefault(pair.Key), candidate = pair.Value }).ToArray();
result["differences"] = differences;
result["status"] = differences.Length == 0 ? "MATCHING_MODEL_SURFACES" : "MODEL_SURFACE_DIFFERENCES";
File.WriteAllText(args[3], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Compared {surfaces["candidate"].Count} candidate models; differences: {differences.Length}.");

return differences.Length == 0 && surfaces["candidate"].Count == 87 ? 0 : 1;
