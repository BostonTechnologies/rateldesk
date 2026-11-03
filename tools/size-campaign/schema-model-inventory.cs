using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Length != 3)
    throw new ArgumentException("Expected the Core assembly, MCP schema source, and output inventory paths.");

// This is an offline SDK tool. Reflection runs against the complete untrimmed
// application output and emits literal type roots for the runtime assembly.
var assemblyPath = Path.GetFullPath(args[0]);
var folder = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var dependency = Path.Combine(folder, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
var shared = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, "Helpdesk.Shared.dll"));
var types = core.GetTypes().Concat(shared.GetTypes()).ToArray();
var roots = new HashSet<Type>();
var catalog = core.GetType("Helpdesk.Mcp.Tools.OperatorOperationCatalog", throwOnError: true)!;
foreach (var operation in (IEnumerable)catalog.GetProperty("All")!.GetValue(null)!)
{
    foreach (var key in new[] { "BodyType", "QueryType" })
        if (operation.GetType().GetProperty(key)!.GetValue(operation) is Type type)
            roots.Add(type);
}

// Include types selected through local variables as well as direct AddDto calls.
foreach (Match match in Regex.Matches(File.ReadAllText(args[1]), @"typeof\(([\w.]+)\)"))
{
    var name = match.Groups[1].Value;
    var matches = types.Where(type => type.FullName == name || type.Name == name).ToArray();
    if (matches.Length == 1) roots.Add(matches[0]);
    else if (matches.Length > 1) throw new InvalidOperationException("Ambiguous schema model " + name);
}
var objects = new HashSet<Type>();
var enums = new HashSet<Type>();
var seen = new HashSet<Type>();
foreach (var root in roots) Visit(root);
var result = new
{
    roots = roots.Select(type => type.FullName).Order(StringComparer.Ordinal).ToArray(),
    objects = objects.Select(type => new
    {
        name = type.FullName,
        properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).ToArray()
    }).OrderBy(type => type.name, StringComparer.Ordinal).ToArray(),
    enums = enums.Select(type => type.FullName).Order(StringComparer.Ordinal).ToArray()
};
File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Compiled schema graph: {roots.Count} roots, {objects.Count} object types, {enums.Count} enums");

void Visit(Type type)
{
    type = Nullable.GetUnderlyingType(type) ?? type;
    if (!seen.Add(type)) return;
    if (type.IsEnum)
    {
        enums.Add(type);
        return;
    }
    if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ||
        type == typeof(TimeOnly) || type == typeof(TimeSpan) || type.FullName!.StartsWith("System.Text.Json.", StringComparison.Ordinal))
        return;
    if (type.IsArray)
    {
        Visit(type.GetElementType()!);
        return;
    }
    if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
    {
        foreach (var argument in type.GetGenericArguments()) Visit(argument);
        return;
    }
    objects.Add(type);
    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        if (property.CanWrite && property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            Visit(property.PropertyType);
}
