using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Mcp.Tools;

namespace Helpdesk.Tests.Api;

public sealed partial class McpPolicyDefinitionTests
{
    [Fact]
    public void Full_access_covers_the_published_http_catalog_and_preserves_credential_and_lifecycle_boundaries()
    {
        var credential = Credential();
        var scope = Scope() with { AllAccess = true, Operations = McpPolicyDefinitionService.Operations.Select(op => op.Key).ToArray() };
        var (row, _) = McpPolicyDefinitionService.Create(credential, [scope], "synthetic-owner", Now);
        Assert.Null(McpPolicyDefinitionDocumentReader.Read(row.DefinitionJson)!.Scopes.Single().PermissionCatalogSnapshot);
        foreach (var operation in McpOperationCatalog.All)
        {
            if (operation.HttpMethod == "LOCAL") continue; // Local configuration has no downstream HTTP policy request.
            Assert.Contains(operation.Tool + "." + operation.Operation, scope.Operations);
            var route = operation.PathTemplate.Split(", ", StringSplitOptions.RemoveEmptyEntries)[0].Split(" then ")[0];
            var path = Regex.Replace(route, @"\{[^}]+\}", m => m.Value == "{ticketType}" ? "incidents" : "synthetic");
            Assert.True(McpPolicyDefinitionService.AllowsRequest(row, credential, operation.HttpMethod, path, route, Now),
                operation.Tool + "." + operation.Operation + ": " + route);
        }
        // Intent still covers a newly published route when its stored exact-operation snapshot is stale.
        row.DefinitionJson = JsonSerializer.Serialize(new McpPolicyDefinitionDocument(1, credential.McpResourceUri!,
            [scope with { Operations = ["helpdesk_incidents.get"] }]));
        Assert.True(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/requests", "/api/v1/requests", Now));
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/unpublished-route", "/api/v1/unpublished-route", Now));
        row.State = "Revoked";
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/requests", "/api/v1/requests", Now));
        row.State = "Active";
        Assert.False(McpPolicyDefinitionService.AllowsRequest(row, credential, "GET", "/api/v1/requests", "/api/v1/requests", scope.ExpiresUtc));
    }
}
