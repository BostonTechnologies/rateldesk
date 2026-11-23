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
    private static Command BuildOrganizationsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var orgs = BuildCrudCommand(runtime, globals, "organizations", "Manage organizations and tenants.", "/api/v1/organizations",
            ("--name", "name"),
            ("--description", "description"),
            ("--state", "state"),
            ("--assigned-sla-id", "assignedSlaId"),
            ("--is-enabled", "isEnabled"));
        orgs.AddCommand(GetListCommand(runtime, globals, "tenants", "/api/v1/admin/tenants"));
        orgs.AddCommand(GetListCommand(runtime, globals, "tenant-lookup", "/api/v1/admin/tenants/lookup"));
        orgs.AddCommand(GetByStringIdCommand(runtime, globals, "change-participants", "/api/v1/organizations/{id}/change-participants", "id"));
        orgs.AddCommand(GetByStringIdCommand(runtime, globals, "branding", "/api/v1/tenants/{id}/branding", "id"));
        orgs.AddCommand(BodyCommand(runtime, globals, "update-branding", HttpMethod.Put, "/api/v1/tenants/{id}/branding",
            ("--display-name", "displayName"),
            ("--primary-color", "primaryColor"),
            ("--accent-color", "accentColor"),
            ("--support-email", "supportEmail")));
        return orgs;
    }

    private static Command BuildCustomersCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var customers = BuildCrudCommand(runtime, globals, "customers", "Manage customers.", "/api/v1/customers",
            ("--name", "name"),
            ("--email", "email"),
            ("--phone", "phone"),
            ("--organization-id", "organizationId"),
            ("--state", "state"),
            ("--is-enabled", "isEnabled"));
        customers.AddCommand(GetByStringIdCommand(runtime, globals, "auth-status", "/api/v1/customers/{id}/auth-status", "id"));
        customers.AddCommand(BodyCommand(runtime, globals, "invite", HttpMethod.Post, "/api/v1/customers/{id}/invite"));
        customers.AddCommand(BodyCommand(runtime, globals, "resend-invite", HttpMethod.Post, "/api/v1/customers/{id}/resend-invite"));
        customers.AddCommand(BodyCommand(runtime, globals, "disable-login", HttpMethod.Post, "/api/v1/customers/{id}/disable-login"));
        customers.AddCommand(BodyCommand(runtime, globals, "sync-authentik", HttpMethod.Post, "/api/v1/customers/{id}/sync-authentik"));
        return customers;
    }

    private static Command BuildUsersCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var users = BuildCrudCommand(runtime, globals, "users", "Manage users.", "/api/v1/users",
            ("--name", "name"),
            ("--email", "email"),
            ("--role", "role"),
            ("--is-test-user", "isTestUser"),
            ("--organization-id", "organizationId"),
            ("--password", "password"));
        var email = new Argument<string>("email");
        var byEmail = new Command("by-email", "GET /api/v1/users/by-email/{email}") { email };
        byEmail.SetHandler(ctx => SendProjectedReadAsync(runtime, globals, ctx, "/api/v1/users/by-email/" + Escape(RequiredArgument(ctx.ParseResult, email)), "user"));
        users.AddCommand(byEmail);
        users.AddCommand(BodyCommand(runtime, globals, "provision", HttpMethod.Post, "/api/v1/users/provision",
            ("--email", "email"),
            ("--name", "name"),
            ("--issuer", "issuer"),
            ("--subject", "subject"),
            ("--authentik-user-id", "authentikUserId"),
            ("--customer-id", "customerId")));
        return users;
    }

    private static Command BuildCategoriesCommand(CliRuntime runtime, GlobalOptions globals)
        => BuildCrudCommand(runtime, globals, "categories", "Manage ticket categories.", "/api/v1/categories",
            ("--name", "name"),
            ("--description", "description"),
            ("--type", "type"),
            ("--parent-id", "parentId"),
            ("--organization-id", "organizationId"),
            ("--is-enabled", "isEnabled"));

    private static Command BuildServicesCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var services = BuildCrudCommand(runtime, globals, "services", "Manage service catalog entries.", "/api/v1/services",
            ("--name", "name"),
            ("--description", "description"),
            ("--parent-service-id", "parentServiceId"),
            ("--allowed-organization-ids", "allowedOrganizationIds"),
            ("--is-enabled", "isEnabled"));
        var parent = new Option<string?>("--parent-service-id") { Description = "Parent service id." };
        var q = new Option<string?>("--query") { Description = "Search query." };
        var pageSize = new Option<int?>("--page-size") { Description = "Page size." };
        var limit = new Option<int?>("--limit") { Description = "Alias for --page-size." };
        var take = new Option<int?>("--take") { Description = "Alias for --page-size." };
        services.AddCommand(GetProjectedListCommand(runtime, globals, "items", "service-items", "/api/v1/service-items", null, (parent, "parentServiceId")));
        services.AddCommand(GetProjectedListCommand(runtime, globals, "search-items", "service-items", "/api/v1/service-items/search", null, (q, "q"), (pageSize, "pageSize"), (limit, "pageSize"), (take, "pageSize")));
        services.AddCommand(GetProjectedByStringIdCommand(runtime, globals, "breadcrumb", "/api/v1/services/{id}/breadcrumb", "id", "service-breadcrumb"));
        services.AddCommand(BuildRequestFormsCommand(runtime, globals));
        return services;
    }

    private static Command BuildRequestFormsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var forms = BuildCrudCommand(runtime, globals, "request-forms", "Manage request forms.", "/api/v1/request-forms",
            ("--service-id", "serviceId"),
            ("--name", "name"),
            ("--description", "description"),
            ("--schema-json", "schemaJson"),
            ("--is-enabled", "isEnabled"),
            ("--allowed-organization-ids", "allowedOrganizationIds"));
        forms.AddCommand(GetProjectedByStringIdCommand(runtime, globals, "by-service", "/api/v1/services/{id}/request-forms", "id", "request-forms"));
        forms.AddCommand(BodyCommand(runtime, globals, "create-for-service", HttpMethod.Post, "/api/v1/services/{id}/request-forms",
            ("--name", "name"),
            ("--description", "description"),
            ("--schema-json", "schemaJson"),
            ("--is-enabled", "isEnabled"),
            ("--allowed-organization-ids", "allowedOrganizationIds")));
        return forms;
    }

    private static Command BuildSelfServiceCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var self = new Command("self-service", "Exercise self-service request flows.");
        self.AddCommand(GetListCommand(runtime, globals, "requests", "/api/v1/self-service/requests"));
        self.AddCommand(GetByStringIdCommand(runtime, globals, "get-request", "/api/v1/self-service/requests/{id}", "id"));
        self.AddCommand(BodyCommand(runtime, globals, "create-request", HttpMethod.Post, "/api/v1/self-service/requests",
            ("--request-form-id", "requestFormId"),
            ("--target-organization-id", "targetOrganizationId"),
            ("--submission-id", "submissionId"),
            ("--service-id", "serviceId"),
            ("--title", "title"),
            ("--description", "description"),
            ("--payload-json", "payloadJson"),
            ("--requester-email", "requesterEmail")));
        var query = new Option<string?>("--query") { Description = "User search query." };
        var pageSize = new Option<int?>("--page-size") { Description = "Page size." };
        var requesterOrganization = new Option<string?>("--target-organization-id") { Description = "Restrict requester lookup to an eligible organization." };
        self.AddCommand(GetListCommand(runtime, globals, "request-users", "/api/v1/self-service/request-users",
            (query, "query"), (pageSize, "pageSize"), (requesterOrganization, "targetOrganizationId")));
        var formId = new Option<string?>("--request-form-id") { Required = true, Description = "Request form ID." };
        var target = new Option<string?>("--target-organization-id") { Description = "Select an eligible organization." };
        self.AddCommand(GetListCommand(runtime, globals, "submission-context", "/api/v1/self-service/submission-context",
            (formId, "requestFormId"), (target, "targetOrganizationId")));
        return self;
    }

    private static Command BuildCrudCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, string basePath, params (string optionName, string jsonName)[] bodyFields)
    {
        var command = new Command(name, description);
        command.AddCommand(name == "request-forms"
            ? BuildRequestFormsListCommand(runtime, globals)
            : GetCrudListCommand(runtime, globals, name, basePath.TrimEnd('/') + "/"));
        command.AddCommand(IsProjectedCrudResource(name)
            ? GetProjectedByIdCommand(runtime, globals, "get", basePath.TrimEnd('/') + "/{id}", name.TrimEnd('s'))
            : GetByIdCommand(runtime, globals, "get", basePath.TrimEnd('/') + "/{id}"));
        command.AddCommand(BodyCommand(runtime, globals, "create", HttpMethod.Post, basePath, bodyFields));
        command.AddCommand(BodyCommand(runtime, globals, "update", HttpMethod.Put, basePath.TrimEnd('/') + "/{id}", bodyFields));
        command.AddCommand(DeleteByIdCommand(runtime, globals, "delete", basePath.TrimEnd('/') + "/{id}"));
        return command;
    }

    private static Command BuildSearchCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var search = new Command("search", "Search Helpdesk lookup data.");
        var q = new Option<string?>("--query") { Description = "Search query." };
        var org = new Option<string?>("--organization-id") { Description = "Organization id." };
        search.AddCommand(GetListCommand(runtime, globals, "users", "/api/v1/global-search/users", (q, "q"), (org, "organizationId")));
        search.AddCommand(GetListCommand(runtime, globals, "customers", "/api/v1/global-search/customers", (q, "q"), (org, "organizationId")));
        search.AddCommand(GetListCommand(runtime, globals, "organizations", "/api/v1/global-search/organizations", (q, "q")));
        return search;
    }

    private static Command GetCrudListCommand(CliRuntime runtime, GlobalOptions globals, string resource, string path)
    {
        if (!IsProjectedCrudResource(resource))
        {
            return GetListCommand(runtime, globals, "list", path);
        }

        var query = new Option<string?>("--query") { Description = "Client-side search query for array responses." };
        var enabledOnly = new Option<bool?>("--enabled-only") { Description = "Client-side filter for enabled resources." };
        var limit = new Option<int?>("--limit") { Description = "Client-side display cap for array responses." };
        var take = new Option<int?>("--take") { Description = "Alias for --limit." };
        return GetProjectedListCommand(runtime, globals, "list", resource, path, null, (query, "clientQuery"), (enabledOnly, "clientEnabledOnly"), (limit, "clientLimit"), (take, "clientTake"));
    }

    private static bool IsProjectedCrudResource(string resource)
        => resource is "organizations" or "customers" or "users" or "services" or "categories" or "roles";

    private static Command BuildRequestFormsListCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var serviceId = new Option<string?>("--service-id") { Description = "Service id for service-scoped request forms." };
        var command = GetProjectedListCommand(
            runtime,
            globals,
            "list",
            "request-forms",
            "/api/v1/services/{serviceId}/request-forms",
            ctx =>
            {
                var value = ctx.ParseResult.GetValueForOption(serviceId);
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new CliValidationException("Global request form listing is not available. Use 'request-forms list --service-id <service-id>' or 'request-forms by-service <service-id>'.");
                }

                return string.Empty;
            },
            (serviceId, "serviceId"));
        command.SetHandler(async ctx =>
        {
            var value = ctx.ParseResult.GetValueForOption(serviceId);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliValidationException("Global request form listing is not available. Use 'request-forms list --service-id <service-id>' or 'request-forms by-service <service-id>'.");
            }

            var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/services/" + Escape(value) + "/request-forms", null).ConfigureAwait(false);
            if (ctx.ParseResult.GetValueForOption(globals.Quiet))
            {
                return;
            }

            var output = ResolveOutput(ctx, globals, defaultView: "summary");
            if (output.View == "raw")
            {
                await WriteRawBodyAsync(runtime, response.Body, ctx, globals).ConfigureAwait(false);
                return;
            }

            var envelope = ProjectResourceEnvelope(response.Body, "request-forms", output.Fields, BodyOutputOptions.From(ctx, globals), ctx);
            if (output.Format == "json")
            {
                await WriteJsonPayloadAsync(runtime, envelope, ctx, globals).ConfigureAwait(false);
                return;
            }

            await runtime.Out.WriteLineAsync(RenderResourceTable("request-forms", envelope, output.Fields)).ConfigureAwait(false);
        });
        return command;
    }

    private static string ProjectedCommandDescription(string name, string resource, string path)
    {
        var example = (resource, name) switch
        {
            ("request-tasks", "list") => "Example: helpdesk request-tasks list --assigned-to-me",
            ("notifications", "list") => "Example: helpdesk notifications list --json --view summary",
            ("notifications", "unread-errors") => "Example: helpdesk notifications unread-errors",
            ("organizations", "list") => "Example: helpdesk organizations list --limit 20",
            ("customers", "list") => "Example: helpdesk customers list --query acme",
            ("users", "list") => "Example: helpdesk users list --limit 20",
            ("user", "by-email") => "Example: helpdesk users by-email person@example.com",
            ("timeline", "timeline") => "Example: helpdesk incidents timeline INC-123 --body-format text",
            ("incident-peek", "peek") => "Example: helpdesk incidents peek INC-123 --include-body",
            ("services", "list") => "Example: helpdesk services list",
            ("service-items", "search-items") => "Example: helpdesk services search-items --query onboarding --json --view summary",
            ("request-forms", "list") => "Example: helpdesk request-forms list --service-id <service-id>",
            ("request-forms", "by-service") => "Example: helpdesk request-forms by-service <service-id>",
            ("categories", "list") => "Example: helpdesk categories list --json --view summary",
            ("roles", "list") => "Example: helpdesk roles list",
            ("connectivity-jobs", "orchestration-jobs") => "Example: helpdesk connectivity orchestration-jobs --json --view summary",
            _ => null
        };

        return example is null ? $"GET {path}" : $"GET {path}{Environment.NewLine}{example}";
    }

    private static Command GetProjectedListCommand(
        CliRuntime runtime,
        GlobalOptions globals,
        string name,
        string resource,
        string path,
        Func<InvocationContext, string>? buildQuery,
        params (Option option, string queryName)[] query)
    {
        var command = new Command(name, ProjectedCommandDescription(name, resource, path));
        foreach (var (option, _) in query)
        {
            command.AddOption(option);
        }

        command.SetHandler(async ctx =>
        {
            var requestQuery = buildQuery is not null ? buildQuery(ctx) : QueryFromOptions(ctx, query);
            var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, path + requestQuery, null).ConfigureAwait(false);
            if (ctx.ParseResult.GetValueForOption(globals.Quiet))
            {
                return;
            }

            var output = ResolveOutput(ctx, globals, defaultView: "summary");
            if (output.View == "raw")
            {
                await WriteRawBodyAsync(runtime, response.Body, ctx, globals).ConfigureAwait(false);
                return;
            }

            var bodyOptions = BodyOutputOptions.From(ctx, globals);
            var envelope = ProjectResourceEnvelope(response.Body, resource, output.Fields, bodyOptions, ctx, query);
            if (output.Format == "json")
            {
                await WriteJsonPayloadAsync(runtime, envelope, ctx, globals).ConfigureAwait(false);
                return;
            }

            await runtime.Out.WriteLineAsync(RenderResourceTable(resource, envelope, output.Fields)).ConfigureAwait(false);
        });
        return command;
    }

    private static Command GetProjectedByIdCommand(CliRuntime runtime, GlobalOptions globals, string name, string template, string resource, bool intId = false)
    {
        var id = new Argument<string>("id");
        var command = new Command(name, ProjectedCommandDescription(name, resource, template)) { id };
        command.SetHandler(ctx => SendProjectedReadAsync(runtime, globals, ctx, Fill(template, ctx.ParseResult.GetValueForArgument(id)), resource));
        return command;
    }

    private static Command GetProjectedByStringIdCommand(CliRuntime runtime, GlobalOptions globals, string name, string template, string argumentName, string resource)
    {
        var id = new Argument<string>(argumentName);
        var command = new Command(name, ProjectedCommandDescription(name, resource, template)) { id };
        command.SetHandler(ctx => SendProjectedReadAsync(runtime, globals, ctx, Fill(template, ctx.ParseResult.GetValueForArgument(id)), resource));
        return command;
    }

    private static async Task SendProjectedReadAsync(CliRuntime runtime, GlobalOptions globals, InvocationContext ctx, string path, string resource)
    {
        var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, path, null).ConfigureAwait(false);
        if (ctx.ParseResult.GetValueForOption(globals.Quiet))
        {
            return;
        }

        var output = ResolveOutput(ctx, globals, defaultView: "detail");
        if (output.View == "raw")
        {
            await WriteRawBodyAsync(runtime, response.Body, ctx, globals).ConfigureAwait(false);
            return;
        }

        var bodyOptions = BodyOutputOptions.From(ctx, globals);
        var projected = ProjectResourceDetail(response.Body, resource, output.Fields, bodyOptions);
        if (output.Format == "json")
        {
            await WriteJsonPayloadAsync(runtime, projected, ctx, globals).ConfigureAwait(false);
            return;
        }

        await runtime.Out.WriteLineAsync(RenderDetail(resource.ToUpperInvariant(), projected)).ConfigureAwait(false);
    }

    private static async Task WriteProjectedAsync(CliRuntime runtime, JsonObject payload, InvocationContext ctx, GlobalOptions globals, string title)
    {
        var output = ResolveOutput(ctx, globals, defaultView: "detail");
        if (output.Format == "json")
        {
            await WriteJsonPayloadAsync(runtime, payload, ctx, globals).ConfigureAwait(false);
            return;
        }

        var rows = payload.Select(kvp => new Dictionary<string, string?>
        {
            ["NAME"] = kvp.Key,
            ["VALUE"] = kvp.Value?.ToJsonString(JsonOptions)
        }).ToList();
        await runtime.Out.WriteLineAsync(RenderTable(title, rows, null)).ConfigureAwait(false);
    }

    private static string QueryFromOptions(InvocationContext ctx, params (Option option, string queryName)[] options)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (option, queryName) in options)
        {
            if (queryName.StartsWith("client", StringComparison.Ordinal))
            {
                continue;
            }

            var value = ValueToString(ctx.ParseResult.GetValueForOption(option));
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[queryName] = value;
            }
        }

        return Query(values.Select(item => (item.Key, item.Value)).ToArray());
    }

    private static JsonObject ProjectResourceEnvelope(string body, string resource, IReadOnlySet<string>? fields, BodyOutputOptions bodyOptions, InvocationContext ctx, params (Option option, string queryName)[] queryOptions)
    {
        var root = JsonNode.Parse(body) ?? new JsonObject();
        var sourceItems = root is JsonObject obj && obj["items"] is JsonArray array
            ? array
            : root as JsonArray ?? [];
        var page = GetInt(root, "page") ?? 1;
        var pageSize = GetInt(root, "pageSize") ?? sourceItems.Count;
        var totalCount = GetInt(root, "totalCount") ?? GetInt(root, "total");
        var arrayResponse = root is JsonArray;
        var sourceObjects = sourceItems.OfType<JsonObject>().ToList();
        var scannedCount = sourceObjects.Count;

        if (arrayResponse)
        {
            var clientQuery = GetClientOption<string?>(ctx, queryOptions, "clientQuery");
            if (!string.IsNullOrWhiteSpace(clientQuery))
            {
                sourceObjects = sourceObjects.Where(item => MatchesClientQuery(item, clientQuery!)).ToList();
            }

            if (GetClientOption<bool?>(ctx, queryOptions, "clientEnabledOnly") == true)
            {
                sourceObjects = sourceObjects.Where(item => GetBool(item, "isEnabled") == true).ToList();
            }

            var cap = ResolvePageSize(null, GetClientOption<int?>(ctx, queryOptions, "clientLimit"), GetClientOption<int?>(ctx, queryOptions, "clientTake"), DefaultArrayDisplayCap)!.Value;
            sourceObjects = sourceObjects.Take(Math.Max(1, cap)).ToList();
            pageSize = sourceObjects.Count;
        }

        var items = new JsonArray();
        foreach (var item in sourceObjects)
        {
            items.Add(FilterFields(ProjectResourceSummary(item, resource, bodyOptions), fields));
        }

        var envelope = new JsonObject
        {
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["totalCount"] = totalCount.HasValue ? JsonValue.Create(totalCount.Value) : null,
            ["items"] = items
        };

        if (arrayResponse)
        {
            envelope["clientSide"] = true;
            envelope["scannedCount"] = scannedCount;
            envelope["displayCount"] = items.Count;
            envelope["capReached"] = scannedCount > items.Count;
        }

        return envelope;
    }

    private static JsonObject ProjectResourceDetail(string body, string resource, IReadOnlySet<string>? fields, BodyOutputOptions bodyOptions)
    {
        var root = JsonNode.Parse(body) ?? new JsonObject();
        if (root is JsonArray array)
        {
            var items = new JsonArray();
            foreach (var item in array.OfType<JsonObject>())
            {
                items.Add(FilterFields(ProjectResourceSummary(item, resource, bodyOptions), fields));
            }

            return new JsonObject { ["items"] = items };
        }

        if (root is not JsonObject source)
        {
            return new JsonObject { ["value"] = CloneNode(root) };
        }

        return FilterFields(ProjectResourceSummary(source, resource, bodyOptions, detail: true), fields);
    }

    private static JsonObject ProjectResourceSummary(JsonObject source, string resource, BodyOutputOptions bodyOptions, bool detail = false)
        => resource switch
        {
            "request-tasks" or "request-task" => ProjectRequestTask(source, bodyOptions, detail),
            "notifications" or "notification" => ProjectNotification(source, bodyOptions, detail),
            "organizations" or "organization" => ProjectOrganization(source),
            "customers" or "customer" => ProjectCustomer(source),
            "users" or "user" => ProjectUser(source),
            "services" or "service" => ProjectService(source, bodyOptions),
            "service-items" or "service-breadcrumb" => ProjectServiceItem(source, bodyOptions),
            "request-forms" or "request-form" => ProjectRequestForm(source, bodyOptions),
            "categories" or "category" => ProjectCategory(source, bodyOptions),
            "roles" or "role" => ProjectRole(source, bodyOptions),
            "connectivity" => ProjectConnectivitySettings(source),
            "connectivity-jobs" => ProjectConnectivityJob(source, bodyOptions),
            "connectivity-tenants" => ProjectConnectivityTenant(source),
            "connectivity-request-definitions" => ProjectConnectivityRequestDefinition(source, bodyOptions),
            "connectivity-bindings" => ProjectConnectivityBinding(source),
            "timeline" => ProjectTimelineEvent(source, bodyOptions, detail),
            "worklog" => ProjectWorklog(source, bodyOptions, detail),
            "incident" or "request" or "change" or "incident-peek" => ProjectTicketDetail(source, resource, bodyOptions, detail),
            _ => CloneObject(source)
        };

    private static JsonObject ProjectOrganization(JsonObject source)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["name"]),
            ["isEnabled"] = CloneNode(source["isEnabled"]),
            ["assignedSlaId"] = CloneNode(source["assignedSlaId"]),
            ["dnsName"] = CloneNode(source["dnsName"]),
            ["itSupportOrganizationId"] = CloneNode(source["itSupportOrganizationId"])
        };
        AddEntityState(row, source);
        return row;
    }

    private static JsonObject ProjectCustomer(JsonObject source)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["name"]),
            ["email"] = CloneNode(source["email"]),
            ["organizationId"] = CloneNode(source["organizationId"]),
            ["organizationName"] = CloneNode(source["organizationName"]),
            ["isEnabled"] = CloneNode(source["isEnabled"])
        };
        AddEntityState(row, source);
        return row;
    }

    private static JsonObject ProjectUser(JsonObject source)
        => new()
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["name"]),
            ["email"] = CloneNode(source["email"]),
            ["role"] = CloneNode(source["role"]),
            ["organizationId"] = CloneNode(source["organizationId"]),
            ["organizationName"] = CloneNode(source["organizationName"]),
            ["isTestUser"] = CloneNode(source["isTestUser"])
        };

    private static JsonObject ProjectService(JsonObject source, BodyOutputOptions bodyOptions)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["name"]),
            ["parentServiceId"] = CloneNode(source["parentServiceId"]),
            ["isEnabled"] = CloneNode(source["isEnabled"]),
            ["allowedOrganizationCount"] = CountArray(source["allowedOrganizationIds"]),
            ["allowedCustomerCount"] = CountArray(source["allowedCustomerIds"]),
            ["depth"] = CloneNode(source["depth"])
        };
        AddSafeBodyFields(row, source, bodyOptions, "description");
        return row;
    }

    private static JsonObject ProjectServiceItem(JsonObject source, BodyOutputOptions bodyOptions)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["name"]),
            ["itemType"] = CloneNode(source["itemType"]),
            ["availableRequestCount"] = CloneNode(source["availableRequestCount"]),
            ["allowedOrganizationCount"] = CountArray(source["allowedOrganizationIds"]),
            ["isEnabled"] = CloneNode(source["isEnabled"])
        };
        AddEnumFlexible(row, source, "itemType", typeof(ServiceItemType));
        AddEnumFlexible(row, source, "releaseStatus", typeof(RequestFormReleaseStatus));
        AddSafeBodyFields(row, source, bodyOptions, "description");
        return row;
    }

    private static JsonObject ProjectRequestForm(JsonObject source, BodyOutputOptions bodyOptions)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["serviceId"] = CloneNode(source["serviceId"]),
            ["title"] = CloneNode(source["title"] ?? source["name"]),
            ["name"] = CloneNode(source["name"] ?? source["title"]),
            ["isEnabled"] = CloneNode(source["isEnabled"] ?? JsonValue.Create(true)),
            ["allowedOrganizationCount"] = CountArray(source["allowedOrganizationIds"])
        };
        AddEnumFlexible(row, source, "releaseStatus", typeof(RequestFormReleaseStatus));
        AddSafeBodyFields(row, source, bodyOptions, "description", "jsonSchema", "schemaJson");
        return row;
    }

    private static JsonObject ProjectCategory(JsonObject source, BodyOutputOptions bodyOptions)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["name"]),
            ["parentId"] = CloneNode(source["parentId"] ?? source["parentCategoryId"]),
            ["organizationId"] = CloneNode(source["organizationId"] ?? source["tenantId"]),
            ["isActive"] = CloneNode(source["isActive"] ?? source["isEnabled"]),
            ["sortOrder"] = CloneNode(source["sortOrder"])
        };
        AddEnumFlexible(row, source, "type", typeof(TicketCategoryType));
        AddSafeBodyFields(row, source, bodyOptions, "description");
        return row;
    }

    private static JsonObject ProjectRole(JsonObject source, BodyOutputOptions bodyOptions)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["name"])
        };
        AddSafeBodyFields(row, source, bodyOptions, "description");
        return row;
    }

    private static void AddEntityState(JsonObject row, JsonObject source)
    {
        if (GetInt(source, "state").HasValue)
        {
            AddEnumFlexible(row, source, "state", typeof(EntityState));
            return;
        }

        if (GetBool(source, "isEnabled").HasValue)
        {
            var state = GetBool(source, "isEnabled") == true ? (int)EntityState.Enabled : (int)EntityState.Blocked;
            row["state"] = state;
            var info = EnumInfo(typeof(EntityState), state);
            row["stateName"] = info.Name;
            row["stateLabel"] = info.Label;
        }
    }

    private static string RenderResourceTable(string resource, JsonObject envelope, IReadOnlySet<string>? fields)
    {
        var rows = envelope["items"] as JsonArray ?? [];
        var tableRows = fields is not null
            ? rows.OfType<JsonObject>().Select(ProjectFieldsForTable).ToList()
            : rows.OfType<JsonObject>().Select(row => ProjectResourceTableRow(resource, row)).ToList();
        var footer = BuildPageFooter(envelope);
        if (GetBool(envelope, "clientSide") == true)
        {
            footer = $"{footer}  Client-side cap {GetInt(envelope, "displayCount") ?? tableRows.Count}/{GetInt(envelope, "scannedCount") ?? tableRows.Count}";
        }

        return RenderTable(resource.ToUpperInvariant(), tableRows, footer);
    }

    private static Dictionary<string, string?> ProjectResourceTableRow(string resource, JsonObject row)
        => resource switch
        {
            "request-tasks" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id"),
                ["STATUS"] = GetString(row, "statusLabel") ?? GetString(row, "statusName"),
                ["TYPE"] = GetString(row, "typeLabel") ?? GetString(row, "typeName"),
                ["ASSIGNED"] = GetString(row, "assignedToName") ?? GetString(row, "assignedToId"),
                ["DUE"] = FormatDate(GetString(row, "dueAt")),
                ["REQUEST"] = GetString(row, "trackingId") ?? GetString(row, "requestId"),
                ["NAME"] = GetString(row, "name")
            },
            "notifications" => new Dictionary<string, string?>
            {
                ["CREATED"] = FormatDate(GetString(row, "createdUtc")),
                ["SEVERITY"] = GetString(row, "severityLabel") ?? GetString(row, "severityName"),
                ["SOURCE"] = GetString(row, "source"),
                ["CATEGORY"] = GetString(row, "category"),
                ["TITLE"] = GetString(row, "title"),
                ["READ"] = GetBool(row, "read")?.ToString()
            },
            "organizations" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id"),
                ["NAME"] = GetString(row, "name"),
                ["STATE"] = GetString(row, "stateLabel") ?? GetString(row, "stateName"),
                ["ENABLED"] = GetBool(row, "isEnabled")?.ToString(),
                ["SLA"] = GetString(row, "assignedSlaId"),
                ["DNS"] = GetString(row, "dnsName")
            },
            "customers" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id"),
                ["NAME"] = GetString(row, "name"),
                ["EMAIL"] = GetString(row, "email"),
                ["ORG"] = GetString(row, "organizationName") ?? GetString(row, "organizationId"),
                ["STATE"] = GetString(row, "stateLabel") ?? GetString(row, "stateName"),
                ["ENABLED"] = GetBool(row, "isEnabled")?.ToString()
            },
            "users" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id"),
                ["NAME"] = GetString(row, "name"),
                ["EMAIL"] = GetString(row, "email"),
                ["ROLE"] = GetString(row, "role"),
                ["ORG"] = GetString(row, "organizationName") ?? GetString(row, "organizationId"),
                ["TEST"] = GetBool(row, "isTestUser")?.ToString()
            },
            "services" or "service-items" or "service-breadcrumb" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id"),
                ["NAME"] = GetString(row, "name"),
                ["PARENT"] = GetString(row, "parentServiceId"),
                ["ENABLED"] = GetBool(row, "isEnabled")?.ToString(),
                ["ORGS"] = GetInt(row, "allowedOrganizationCount")?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CUSTOMERS"] = GetInt(row, "allowedCustomerCount")?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            "request-forms" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id"),
                ["SERVICE"] = GetString(row, "serviceId"),
                ["STATUS"] = GetString(row, "releaseStatusLabel") ?? GetString(row, "releaseStatusName"),
                ["ENABLED"] = GetBool(row, "isEnabled")?.ToString(),
                ["ORGS"] = GetInt(row, "allowedOrganizationCount")?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["TITLE"] = GetString(row, "title") ?? GetString(row, "name")
            },
            "categories" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id"),
                ["NAME"] = GetString(row, "name"),
                ["TYPE"] = GetString(row, "typeLabel") ?? GetString(row, "typeName"),
                ["PARENT"] = GetString(row, "parentId"),
                ["ORG"] = GetString(row, "organizationId"),
                ["ACTIVE"] = GetBool(row, "isActive")?.ToString()
            },
            "roles" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id"),
                ["NAME"] = GetString(row, "name"),
                ["DESCRIPTION"] = GetString(row, "descriptionExcerpt")
            },
            "connectivity" or "connectivity-jobs" or "connectivity-tenants" or "connectivity-request-definitions" or "connectivity-bindings" => new Dictionary<string, string?>
            {
                ["ID"] = GetString(row, "id") ?? GetString(row, "jobDefinitionId") ?? GetString(row, "requestDefinitionId"),
                ["NAME"] = GetString(row, "name") ?? GetString(row, "jobDefinitionName") ?? GetString(row, "requestDefinitionName"),
                ["TENANT"] = GetString(row, "tenantName") ?? GetString(row, "tenantId"),
                ["STATUS"] = GetString(row, "status") ?? GetBool(row, "enabled")?.ToString(),
                ["UPDATED"] = FormatDate(GetString(row, "updatedAt"))
            },
            _ => ProjectFieldsForTable(row)
        };

    private static string RenderDetail(string title, JsonObject row)
    {
        var rows = row.Select(kvp => new Dictionary<string, string?>
        {
            ["NAME"] = kvp.Key,
            ["VALUE"] = kvp.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                _ => kvp.Value.ToJsonString(JsonOptions)
            }
        }).ToList();
        return RenderTable(title, rows, null);
    }

}
