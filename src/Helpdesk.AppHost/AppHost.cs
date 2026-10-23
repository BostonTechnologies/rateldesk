using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Helpdesk_API>("helpdesk-api");
var web = builder.AddProject<Projects.HelpDesk_NewWeb>("helpdesk-newweb")
    .WithReference(api)
    .WaitFor(api);

if (builder.Configuration["ApiBaseUrl"] is { Length: > 0 } customApi)
    web.WithEnvironment("ApiBaseUrl", customApi);
else
    web.WithEnvironment("ApiBaseUrl", api.GetEndpoint("http"));

foreach (var item in builder.Configuration.GetSection("WebGateway").AsEnumerable().Where(item => item.Value is not null))
    web.WithEnvironment(item.Key.Replace(":", "__"), item.Value!);
var pairingOrigin = builder.Configuration["IntegrationCredentials:Pairing:PublicOrigin"];
if (string.IsNullOrWhiteSpace(pairingOrigin)) pairingOrigin = builder.Configuration["WebGateway:PublicOrigin"];
if (!string.IsNullOrWhiteSpace(pairingOrigin))
    api.WithEnvironment("IntegrationCredentials__Pairing__PublicOrigin", pairingOrigin);

// A selected MCP service configures both hosts. No profile-only exposure or backend IP discovery.
if (builder.Configuration.GetValue<bool>("WebGateway:Mcp:Enabled"))
{
    var origin = builder.Configuration["WebGateway:PublicOrigin"];
    var resource = builder.Configuration["WebGateway:Mcp:PublicResourceUri"];
    if (string.IsNullOrWhiteSpace(resource) && !string.IsNullOrWhiteSpace(origin))
        resource = new Uri(new Uri(origin), "/mcp").AbsoluteUri;
    if (string.IsNullOrWhiteSpace(resource))
        throw new InvalidOperationException("Optional HTTP MCP requires WebGateway:PublicOrigin or WebGateway:Mcp:PublicResourceUri.");
    if (string.IsNullOrWhiteSpace(origin)) origin = new Uri(resource).GetLeftPart(UriPartial.Authority);
    if (string.IsNullOrWhiteSpace(pairingOrigin))
        api.WithEnvironment("IntegrationCredentials__Pairing__PublicOrigin", origin);
    var mode = builder.Configuration["WebGateway:Mcp:AuthenticationMode"] ?? "gateway";
    var instance = builder.Configuration["Helpdesk:Mcp:Instance"];
    if (string.IsNullOrWhiteSpace(instance)) instance = builder.Configuration["RATELDESK_MCP_INSTANCE"] ?? "development";
    var configurationPath = builder.Configuration["Helpdesk:Mcp:ConfigurationPath"];
    if (string.IsNullOrWhiteSpace(configurationPath)) configurationPath = builder.Configuration["RATELDESK_MCP_CONFIG"];
    var expectedTarget = builder.Configuration["Helpdesk:Mcp:ExpectedApiBaseUrl"];
    if (string.IsNullOrWhiteSpace(expectedTarget))
        expectedTarget = builder.Configuration["RATELDESK_MCP_EXPECTED_API_BASE_URL"] ??
            builder.Configuration[$"RATELDESK_MCP_{instance.Trim().ToUpperInvariant()}_API_BASE_URL"];

    var mcp = builder.AddProject<Projects.Helpdesk_Mcp_Http>("helpdesk-mcp-http")
        .WithHttpEndpoint(name: "http")
        .WithReference(api)
        .WaitFor(api)
        .WithEnvironment("Helpdesk__Mcp__AuthenticationMode", mode)
        .WithEnvironment("Helpdesk__Mcp__PublicResourceUri", resource)
        .WithEnvironment("Helpdesk__Mcp__AllowedOrigins__0", new Uri(origin).GetLeftPart(UriPartial.Authority))
        .WithEnvironment("Helpdesk__Mcp__Instance", instance);

    if (expectedTarget is not null)
        mcp.WithEnvironment("Helpdesk__Mcp__ExpectedApiBaseUrl", expectedTarget);
    else if (builder.Configuration["ApiBaseUrl"] is { Length: > 0 } sharedTarget)
        mcp.WithEnvironment("Helpdesk__Mcp__ExpectedApiBaseUrl", sharedTarget);
    else
        mcp.WithEnvironment("Helpdesk__Mcp__ExpectedApiBaseUrl", api.GetEndpoint("http"));
    if (mode.Equals("gateway", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(configurationPath))
    {
        if (builder.Configuration["Helpdesk:Mcp:Outbound:ApiBaseUrl"] is null && builder.Configuration["RATELDESK_MCP_API_BASE_URL"] is null)
        {
            if (expectedTarget is not null)
                mcp.WithEnvironment("Helpdesk__Mcp__Outbound__ApiBaseUrl", expectedTarget);
            else if (builder.Configuration["ApiBaseUrl"] is { Length: > 0 } sharedOutboundTarget)
                mcp.WithEnvironment("Helpdesk__Mcp__Outbound__ApiBaseUrl", sharedOutboundTarget);
            else
                mcp.WithEnvironment("Helpdesk__Mcp__Outbound__ApiBaseUrl", api.GetEndpoint("http"));
        }
        if (builder.Configuration["Helpdesk:Mcp:Outbound:CredentialMode"] is null && builder.Configuration["RATELDESK_MCP_CREDENTIAL_MODE"] is null)
            mcp.WithEnvironment("Helpdesk__Mcp__Outbound__CredentialMode", "gateway");
    }

    // Existing isolated files and explicit external-authentication/downstream identities remain usable.
    foreach (var section in new[] { "Authentication:AuthentikMcp", "Helpdesk:Mcp:Outbound", "Helpdesk:Mcp:AllowedOrigins" })
        foreach (var item in builder.Configuration.GetSection(section).AsEnumerable().Where(item => item.Value is not null))
            mcp.WithEnvironment(item.Key.Replace(":", "__"), item.Value!);
    foreach (var item in builder.Configuration.AsEnumerable().Where(item => item.Value is not null &&
        item.Key.StartsWith("RATELDESK_MCP_", StringComparison.OrdinalIgnoreCase) && !item.Key.Contains(':')))
        mcp.WithEnvironment(item.Key, item.Value!);
    if (!string.IsNullOrWhiteSpace(configurationPath))
        mcp.WithEnvironment("Helpdesk__Mcp__ConfigurationPath", configurationPath);
    if (builder.Configuration["Helpdesk:Mcp:DelegationTimeoutSeconds"] is { Length: > 0 } timeout)
        mcp.WithEnvironment("Helpdesk__Mcp__DelegationTimeoutSeconds", timeout);

    web.WithReference(mcp)
        .WithEnvironment("WebGateway__Mcp__Enabled", "true")
        .WithEnvironment("WebGateway__Mcp__AuthenticationMode", mode)
        .WithEnvironment("WebGateway__Mcp__PublicResourceUri", resource)
        .WithEnvironment("WebGateway__PublicOrigin", origin);
    if (builder.Configuration["WebGateway:Mcp:Destination"] is { Length: > 0 } customMcp)
        web.WithEnvironment("WebGateway__Mcp__Destination", customMcp);
    else
        web.WithEnvironment("WebGateway__Mcp__Destination", mcp.GetEndpoint("http"));
}

builder.Build().Run();
