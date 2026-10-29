using System.Text.Json;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.AspNetCore.Http;

namespace Helpdesk.API.Endpoints.McpPolicies;

/// <summary>Validates one policy against the credential authority; never creates grants.</summary>
public static class McpPolicyDefinitionService
{
    private static readonly McpOperationDefinition[] HttpOperations = McpOperationCatalog.All
        .Where(x => x.HttpMethod is "GET" or "POST" or "PUT" or "PATCH" or "DELETE")
        .ToArray();

    public static McpPolicyOperation[] Operations { get; } = HttpOperations
        .Select(x => new McpPolicyOperation(Key(x), Words(x.Tool["helpdesk_".Length..]), Words(x.Operation), x.Effect))
        .DistinctBy(x => x.Key).OrderBy(x => x.Family, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();

    private static readonly HashSet<string> OperationKeys = Operations.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    public static string? ValidateScopes(IntegrationCredential credential, McpPolicyScopeDraft[]? scopes, DateTime utcNow)
    {
        if (scopes is not { Length: 1 } || scopes[0] is null)
            return "Choose one base policy for this credential's approved scope.";
        var scope = scopes[0];
        var ownerAdministration = credential.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose;
        if (credential.Purpose is not ("mcp" or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose))
            return "Operation policies require an HTTP MCP credential.";
        if (Helpdesk.API.Endpoints.Authentication.McpGatewayDelegationEndpoints.CanonicalResourceUri(credential.McpResourceUri) is null)
            return "The HTTP MCP credential must be bound to its canonical HTTPS resource.";
        if (scope.Scope != (ownerAdministration ? "server" : "organization") ||
            (ownerAdministration ? scope.OrganizationId is not null : scope.OrganizationId != credential.OrganizationId))
            return "The base policy must match the credential's approved server or organization scope.";
        if (string.IsNullOrWhiteSpace(scope.Name) || scope.Name.Length > 128 || scope.Name.Any(char.IsControl) ||
            scope.Effect is not ("Allow" or "Deny"))
            return "Give the base policy a name and select Allow or Deny.";
        if (scope.ExpiresUtc.Kind != DateTimeKind.Utc || scope.ExpiresUtc <= utcNow ||
            scope.ExpiresUtc > credential.ExpiresAtUtc.UtcDateTime)
            return "The policy expiry must be a future UTC instant no later than the credential expiry.";
        var ceiling = credential.Permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (scope.Permissions is not { Length: > 0 and <= 100 } ||
            scope.Permissions.Distinct(StringComparer.Ordinal).Count() != scope.Permissions.Length ||
            scope.Permissions.Any(x => !ceiling.Contains(x)))
            return "Policy permissions must be selected from this credential's existing grants.";
        if (scope.Operations is not { Length: > 0 and <= 1000 } ||
            scope.Operations.Distinct(StringComparer.Ordinal).Count() != scope.Operations.Length ||
            scope.Operations.Any(x => !OperationKeys.Contains(x)))
            return "Select supported HTTP MCP operations for this base policy.";
        if (scope.Exceptions is null || scope.Exceptions.Length > 100)
            return "A base policy may contain at most 100 nested exceptions.";
        var selected = scope.Operations.ToHashSet(StringComparer.Ordinal);
        foreach (var exception in scope.Exceptions)
        {
            if (exception is null || string.IsNullOrWhiteSpace(exception.Name) || exception.Name.Length > 128 ||
                exception.Name.Any(char.IsControl) || exception.Effect != "Deny" ||
                exception.ExpiresUtc.Kind != DateTimeKind.Utc || exception.ExpiresUtc <= utcNow ||
                exception.ExpiresUtc > scope.ExpiresUtc || exception.Operations is not { Length: > 0 and <= 1000 } ||
                exception.Operations.Distinct(StringComparer.Ordinal).Count() != exception.Operations.Length ||
                exception.Operations.Any(x => !selected.Contains(x)))
                return "Each exception must name selected parent operations, use Deny, and expire no later than its base policy.";
        }
        return null;
    }

    public static (McpAccessPolicy Policy, McpAccessPolicyAudit Audit) Create(IntegrationCredential credential,
        McpPolicyScopeDraft[] scopes, string actor, DateTime utcNow)
    {
        var error = ValidateScopes(credential, scopes, utcNow);
        if (error is not null) throw new ArgumentException(error, nameof(scopes));
        var row = new McpAccessPolicy { CredentialId = credential.Id, Revision = 1 };
        Apply(row, credential, scopes, "Active");
        return (row, Audit(row, actor, utcNow));
    }

    public static McpPolicyScopeDraft[] Normalize(McpPolicyScopeDraft[] scopes) => scopes.Select(scope => scope with
    {
        Name = scope.Name.Trim(), ExpiresUtc = Precision(scope.ExpiresUtc),
        Permissions = scope.Permissions.Order(StringComparer.Ordinal).ToArray(),
        Operations = scope.Operations.Order(StringComparer.Ordinal).ToArray(),
        Exceptions = scope.Exceptions.Select(exception => exception with
        {
            Name = exception.Name.Trim(), ExpiresUtc = Precision(exception.ExpiresUtc),
            Operations = exception.Operations.Order(StringComparer.Ordinal).ToArray()
        }).ToArray()
    }).ToArray();

    public static void Apply(McpAccessPolicy row, IntegrationCredential credential, McpPolicyScopeDraft[] scopes, string state)
    {
        var normalized = Normalize(scopes);
        var scope = normalized.Single();
        row.OrganizationId = scope.OrganizationId ?? "";
        row.PermissionsJson = JsonSerializer.Serialize(scope.Permissions);
        row.ExpiresUtc = scope.ExpiresUtc;
        row.State = state;
        row.DefinitionJson = JsonSerializer.Serialize(new McpPolicyDefinitionDocument(1, credential.McpResourceUri ?? "", normalized));
    }

    public static McpAccessPolicyAudit Audit(McpAccessPolicy row, string actor, DateTime utcNow) => new()
    {
        CredentialId = row.CredentialId, Revision = row.Revision, Actor = actor,
        OccurredUtc = utcNow, DefinitionJson = JsonSerializer.Serialize(ToDto(row))
    };

    public static McpPolicyDto ToDto(McpAccessPolicy row) => new(row.CredentialId, row.OrganizationId,
        JsonSerializer.Deserialize<string[]>(row.PermissionsJson) ?? [],
        DateTime.SpecifyKind(row.ExpiresUtc, DateTimeKind.Utc), row.Revision, row.State)
    {
        Scopes = McpPolicyDefinitionDocumentReader.Read(row.DefinitionJson)?.Scopes ?? [],
        Resource = McpPolicyDefinitionDocumentReader.Read(row.DefinitionJson)?.Resource
    };

    /// <summary>
    /// Match the API route actually being executed, not a caller-supplied tool name.
    /// Operations sharing a route share its allow boundary; a matching explicit deny wins.
    /// </summary>
    public static bool AllowsRequest(McpAccessPolicy row, IntegrationCredential credential, string method,
        string path, string? routeTemplate, DateTime utcNow, IQueryCollection? query = null)
    {
        if (row.State != "Active" || row.ExpiresUtc <= utcNow) return false;
        if (string.IsNullOrEmpty(row.DefinitionJson)) return true; // Legacy permission constraint remains authoritative.
        var definition = McpPolicyDefinitionDocumentReader.Read(row.DefinitionJson);
        if (definition is null || definition.Resource != credential.McpResourceUri) return false;
        var scope = definition.Scopes.Single();
        if (scope.Effect != "Allow" || scope.ExpiresUtc <= utcNow || scope.Operations is null || scope.Exceptions is null)
            return false;
        var matching = HttpOperations.Where(operation => operation.HttpMethod == method &&
            Routes(operation).Any(template => Matches(template, routeTemplate, path)))
            .Select(Key).ToHashSet(StringComparer.Ordinal);
        if (matching.Contains("helpdesk_ai_assistant.chat") || matching.Contains("helpdesk_ai_assistant.chat_open"))
        {
            // LoadAsync is read-only only when an exact conversation is requested.
            // Omitting it may create a conversation even though the HTTP method is GET.
            // Bind admission to the API's query contract, never a client-supplied tool label.
            var readsExisting = query is not null && query.TryGetValue("conversationId", out var conversationId) &&
                                conversationId.Count == 1 && Guid.TryParse(conversationId[0], out _);
            matching.Remove(readsExisting ? "helpdesk_ai_assistant.chat_open" : "helpdesk_ai_assistant.chat");
        }
        if (scope.Exceptions.Any(exception => exception is null || exception.Effect != "Deny" || exception.Operations is null))
            return false;
        var denied = scope.Exceptions.Where(exception => exception.ExpiresUtc > utcNow)
            .SelectMany(exception => exception.Operations).ToHashSet(StringComparer.Ordinal);
        if (matching.Overlaps(denied)) return false;
        if (scope.Operations.Any(matching.Contains)) return true;
        // Self-assignment resolves the executing account before issuing bulk/assign.
        // This prerequisite does not make denying assignment deny every status read.
        return method == "GET" && Matches("/api/v1/auth/me", routeTemplate, path) &&
            HttpOperations.Any(operation => operation.Operation == "assign_self" &&
                scope.Operations.Contains(Key(operation), StringComparer.Ordinal) && !denied.Contains(Key(operation)));
    }

    private static IEnumerable<string> Routes(McpOperationDefinition operation)
    {
        // The gateway reads its execution identity; API-purpose and External
        // clients use the AI-agent status endpoint advertised by the catalog.
        if (operation.Tool == "helpdesk_auth" && operation.Operation == "status" ||
            operation.Tool == "helpdesk_health" && operation.Operation == "get")
            yield return "/api/v1/auth/me";
        foreach (var part in operation.PathTemplate.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var chained = part.Split(" then ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            yield return chained[0];
            if (chained.Length == 2 && chained[1].StartsWith('/'))
                yield return chained[0][..chained[0].LastIndexOf('/')] + chained[1];
        }
    }

    private static bool Matches(string expected, string? routeTemplate, string path)
    {
        var expectedParts = expected.Trim('/').Split('/');
        var actualParts = (routeTemplate ?? path).Trim('/').Split('/');
        var pathParts = path.Trim('/').Split('/');
        if (expectedParts.Length != actualParts.Length || pathParts.Length != expectedParts.Length) return false;
        for (var index = 0; index < expectedParts.Length; index++)
        {
            var target = expectedParts[index];
            var actual = actualParts[index];
            if (!target.StartsWith('{'))
            {
                var value = actual.StartsWith('{') ? pathParts[index] : actual;
                if (!string.Equals(target, value, StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (target == "{ticketType}")
            {
                var value = pathParts[index];
                if (!new[] { "incidents", "requests", "changes" }.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
            }
            else if (routeTemplate is null || !actual.StartsWith('{'))
            {
                // A literal route (for example /summary) is never authorized by
                // an unrelated /{id} operation merely because it has the same shape.
                // Without endpoint metadata a dynamic route cannot be proven safe.
                return false;
            }
        }
        return true;
    }

    private static DateTime Precision(DateTime value) => new(value.Ticks / 10 * 10, DateTimeKind.Utc);
    private static string Key(McpOperationDefinition value) => $"{value.Tool}.{value.Operation}";
    private static string Words(string value) => string.Join(' ', value.Split('_').Select(word =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
}
