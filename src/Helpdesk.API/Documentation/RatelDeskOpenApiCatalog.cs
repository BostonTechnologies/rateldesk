using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Helpdesk.API.Documentation;

/// <summary>Single source of truth for the public API reference navigation.</summary>
public static class RatelDeskOpenApiCatalog
{
    private const string JwtBearerScheme = "JwtBearer";
    private const string IntegrationCredentialScheme = "IntegrationCredential";
    private const string McpIntegrationCredentialScheme = "McpIntegrationCredential";
    private const string LocalSessionScheme = "LocalSession";
    private const string AiAgentScheme = "AiAgentJwt";
    private const string OrchestrationScheme = "OrchestrationM2M";
    private const string SystemScheme = "SystemToken";

    private static readonly string[] GroupOrder =
    [
        "Getting Started", "Identity & Access", "Organizations & Customers", "Ticketing",
        "Service Catalog & Self-Service", "Service Levels", "Email & Notifications",
        "Automation & Integrations", "AI Assistant", "Reporting & Search", "Assets & Data",
        "System & Diagnostics"
    ];

    private sealed record Tag(string Name, string Group, string Description);

    private static readonly Tag[] Tags =
    [
        new("Setup", "Getting Started", "Bootstrap and setup state."),
        new("Authentication", "Identity & Access", "Current identity and authentication integrations."),
        new("Local Authentication", "Identity & Access", "Browser cookie sign-in and MFA for local accounts."),
        new("Customer Authentication", "Identity & Access", "Customer identity linking."),
        new("Users", "Identity & Access", "Application users and access."),
        new("Role Definitions", "Identity & Access", "Scoped application role definitions."),
        new("Integration Credentials", "Identity & Access", "Revocable API credentials. Secrets are shown once."),
        new("MCP Gateway", "Automation & Integrations", "Paired MCP credential delegation."),
        new("Organizations", "Organizations & Customers", "Application tenant organizations."),
        new("Customers", "Organizations & Customers", "Contacts; a customer is not the application tenant."),
        new("Tenant Administration", "Organizations & Customers", "Tenant-scoped administration."),
        new("Tenant Settings", "Organizations & Customers", "Tenant configuration."),
        new("Tenant Branding", "Organizations & Customers", "Tenant branding."),
        new("Support Groups", "Organizations & Customers", "Support groups."),
        new("Support Group Members", "Organizations & Customers", "Support group membership."),
        new("Support Coverage", "Organizations & Customers", "Support coverage."),
        new("Tickets", "Ticketing", "Shared ticket operations."), new("Incidents", "Ticketing", "Incident tickets."), new("Requests", "Ticketing", "Service requests."), new("Request Tasks", "Ticketing", "Request tasks."), new("Request Approvals", "Ticketing", "Request approvals."), new("Changes", "Ticketing", "Change tickets."), new("Work Logs", "Ticketing", "Ticket work logs."), new("Attachments", "Ticketing", "Ticket attachments."), new("Timeline", "Ticketing", "Ticket timeline."),
        new("Services", "Service Catalog & Self-Service", "Service catalog."), new("Request Forms", "Service Catalog & Self-Service", "Request forms."), new("Ticket Categories", "Service Catalog & Self-Service", "Ticket categories."), new("Ticket Lookups", "Service Catalog & Self-Service", "Ticket lookup values."), new("Self-Service", "Service Catalog & Self-Service", "Customer self-service."), new("Public Tickets", "Service Catalog & Self-Service", "Public ticket submission."), new("CAPTCHA", "Service Catalog & Self-Service", "CAPTCHA verification."),
        new("SLA Policies", "Service Levels", "SLA policies."), new("SLA Calendars", "Service Levels", "Working calendars."), new("Tenant SLA Settings", "Service Levels", "Tenant SLA settings."), new("Ticket SLA", "Service Levels", "Ticket SLA state."), new("SLA Reports", "Service Levels", "SLA reporting."), new("SLA Report Subscriptions", "Service Levels", "SLA report subscriptions."),
        new("Email Settings", "Email & Notifications", "Email settings."), new("Email Processing", "Email & Notifications", "Email processing."), new("Inbound Email Rules", "Email & Notifications", "Inbound email rules."), new("Email Layouts", "Email & Notifications", "Email layouts."), new("Email Templates", "Email & Notifications", "Email templates."), new("Notifications", "Email & Notifications", "Notifications."), new("Support Notification Subscriptions", "Email & Notifications", "Support notification subscriptions."), new("Support Notification Preferences", "Email & Notifications", "Support notification preferences."),
        new("Automation Rules", "Automation & Integrations", "Automation rules."), new("Workflow Operations", "Automation & Integrations", "Workflow operations."), new("External Orchestration", "Automation & Integrations", "External orchestration."), new("Orchestration Provider", "Automation & Integrations", "Orchestration provider."), new("Netclaw", "Automation & Integrations", "Netclaw connectivity."),
        new("AI Assistant", "AI Assistant", "AI assistant operations."), new("AI Assistant Chat", "AI Assistant", "AI assistant chat."), new("AI Assistant Webhooks", "AI Assistant", "AI assistant webhooks."), new("AI Assistant MCP", "AI Assistant", "Application MCP callbacks; distinct from the public MCP host."),
        new("Dashboard", "Reporting & Search", "Dashboards."), new("Global Search", "Reporting & Search", "Global search."), new("Assets", "Assets & Data", "Assets."), new("Resources", "Assets & Data", "Resource datasets."),
        new("Instance Branding", "System & Diagnostics", "Instance-wide branding."), new("System", "System & Diagnostics", "System operations."), new("System Tickets", "System & Diagnostics", "Machine/system-only ticket contract."), new("Presence", "System & Diagnostics", "User presence."), new("Background Jobs", "System & Diagnostics", "Background job operations."), new("AI Agent Operations", "System & Diagnostics", "AI agent diagnostics."), new("Health", "System & Diagnostics", "Health checks.")
    ];

    private static readonly IReadOnlyDictionary<string, string> CanonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Local authentication"] = "Local Authentication", ["Role definitions"] = "Role Definitions", ["Tenant administration"] = "Tenant Administration", ["Tenant settings"] = "Tenant Settings", ["Ticketing"] = "Ticket Lookups", ["Captcha"] = "CAPTCHA", ["SLA"] = "SLA Policies", ["Email"] = "Email Processing", ["Workflow Ops"] = "Workflow Operations", ["AutomationRules"] = "Automation Rules", ["Branding"] = "Instance Branding", ["AI Agent Ops"] = "AI Agent Operations", ["Ops"] = "Background Jobs", ["Admin"] = "Tenant Administration", ["AiAssistant Chat"] = "AI Assistant Chat", ["AiAssistant Webhooks"] = "AI Assistant Webhooks", ["External orchestration"] = "External Orchestration", ["Orchestration provider"] = "Orchestration Provider", ["Self Service"] = "Self-Service"
    };

    public static Task TransformDocumentAsync(OpenApiDocument document, CancellationToken cancellationToken)
    {
        document.Info.Title = "RatelDesk API";
        document.Info.Description = "Use the Web-hosted reference at `/api/docs`. The API base URL is the server selected in Scalar. Local sign-in uses a browser cookie and CSRF protection; CLI and MCP use configured integration credentials. Organizations are application tenants; Customers are contacts. Pagination, filters, and errors are documented per operation.";
        // OpenApiDocument uses set and dictionary collections. Rebuild each collection in
        // a defined order so the generated reference is stable across endpoint discovery
        // order, rather than merely stable by the current collection implementation.
        document.Tags = new SortedSet<OpenApiTag>(
            Tags.Select(tag => new OpenApiTag { Name = tag.Name, Description = tag.Description }),
            Comparer<OpenApiTag>.Create((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name)));
        var paths = document.Paths.OrderBy(path => path.Key, StringComparer.Ordinal).ToArray();
        document.Paths.Clear();
        foreach (var path in paths)
        {
            var operations = path.Value.Operations.OrderBy(operation => operation.Key.ToString(), StringComparer.Ordinal).ToArray();
            path.Value.Operations.Clear();
            foreach (var operation in operations)
                path.Value.Operations.Add(operation.Key, operation.Value);

            document.Paths.Add(path.Key, path.Value);
        }
        document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        document.Extensions["x-tagGroups"] = new JsonNodeExtension(new JsonArray(Tags.GroupBy(tag => tag.Group)
        .OrderBy(group => Array.IndexOf(GroupOrder, group.Key))
        .Select(group => (JsonNode)new JsonObject
        {
            ["name"] = group.Key,
            ["tags"] = new JsonArray(group.Select(tag => (JsonNode)tag.Name).ToArray())
        }).ToArray()));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Documents the authentication mechanisms an endpoint actually accepts. This deliberately
    /// does not set document-level security: a document default would make public endpoints look
    /// protected unless every operation supplied a security override.
    /// </summary>
    public static void ConfigureSecuritySchemes(OpenApiDocument document, string localCookieName)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[JwtBearerScheme] = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "OIDC or local development JWT in the Authorization header."
        };
        document.Components.SecuritySchemes[IntegrationCredentialScheme] = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "opaque rdk credential",
            In = ParameterLocation.Header,
            Description = "Opaque integration credential: `Bearer rdk_<credential-id>_<secret>`. It is not a JWT and is scoped to its organization and permissions."
        };
        document.Components.SecuritySchemes[McpIntegrationCredentialScheme] = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "paired opaque rdk credential",
            In = ParameterLocation.Header,
            Description = "Paired MCP credential: `Bearer rdk_<credential-id>_<secret>`. It is valid only for the MCP execution-token exchange and cannot call ordinary API operations."
        };
        document.Components.SecuritySchemes[LocalSessionScheme] = new OpenApiSecurityScheme
        {
            Name = localCookieName,
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Description = "Local browser session cookie. State-changing local-session requests also require the application's CSRF protection."
        };
        document.Components.SecuritySchemes[AiAgentScheme] = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT issued for the configured AI-agent machine identity."
        };
        document.Components.SecuritySchemes[OrchestrationScheme] = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Client-credentials JWT for the configured orchestration provider."
        };
        document.Components.SecuritySchemes[SystemScheme] = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Machine-only system JWT. It is not an end-user or integration credential."
        };
    }

    public static Task TransformOperationAsync(OpenApiOperation operation, ApiDescription description, OpenApiDocument document, IServiceProvider applicationServices, CancellationToken cancellationToken)
    {
        var tag = operation.Tags?.Select(item => item.Name).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(tag) && CanonicalNames.TryGetValue(tag, out var canonical))
        {
            operation.Tags!.Clear();
            operation.Tags.Add(new OpenApiTagReference(canonical));
        }

        var metadata = ResolveEndpointMetadata(description, applicationServices);
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security = [];
            return Task.CompletedTask;
        }

        var authorization = metadata.OfType<IAuthorizeData>().ToArray();
        if (authorization.Length == 0)
        {
            // No document-level default exists, but make the public contract explicit in JSON.
            operation.Security = [];
            return Task.CompletedTask;
        }

        var policies = authorization
            .Select(item => item.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .ToHashSet(StringComparer.Ordinal);

        operation.Security = policies switch
        {
            _ when policies.Contains("AuthentikAiAgentApi") => Requirements(document, AiAgentScheme),
            _ when policies.Contains("OrchestrationM2MOnly") => Requirements(document, OrchestrationScheme),
            _ when policies.Contains("SystemBlazorWeb") => Requirements(document, SystemScheme),
            _ when policies.Contains("IntegrationCredentialManagementSession") => Requirements(document, JwtBearerScheme, LocalSessionScheme),
            _ when policies.Contains("IntegrationCredentialSelfRevocation") => Requirements(document, IntegrationCredentialScheme),
            _ when policies.Contains("McpCredentialDelegation") => Requirements(document, McpIntegrationCredentialScheme),
            _ => Requirements(document, JwtBearerScheme, IntegrationCredentialScheme, LocalSessionScheme)
        };
        return Task.CompletedTask;
    }

    private static List<OpenApiSecurityRequirement> Requirements(OpenApiDocument document, params string[] schemes) =>
        schemes.Select(scheme => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(scheme, document)] = []
        }).ToList();

    private static IEnumerable<object> ResolveEndpointMetadata(ApiDescription description, IServiceProvider applicationServices)
    {
        var relativePath = description.RelativePath?.Trim('/');
        var endpoint = applicationServices.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.RoutePattern.RawText?.Trim('/'), relativePath, StringComparison.OrdinalIgnoreCase) &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                    .Any(method => string.Equals(method, description.HttpMethod, StringComparison.OrdinalIgnoreCase)) == true);

        // ApiExplorer currently omits group-level authorization metadata for some minimal API
        // routes. The endpoint data source contains the effective runtime metadata, which is
        // what authorization middleware will evaluate.
        return endpoint is not null
            ? endpoint.Metadata
            : description.ActionDescriptor.EndpointMetadata ?? [];
    }
}
