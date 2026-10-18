using System.Text.Json.Nodes;
using Helpdesk.AgentClient;

namespace Helpdesk.Mcp.Configuration;

/// <summary>
/// Describes the configured execution identity without treating configuration as
/// proof of provider authority or a successful authenticated API request.
/// </summary>
public static class HelpdeskMcpIdentityMetadata
{
    public static JsonObject ToJson(HelpdeskMcpHostContext context, AgentClientConfiguration? configuration)
    {
        var configuredMode = configuration?.CredentialMode;
        var credentialMode = string.IsNullOrWhiteSpace(configuredMode)
            ? "authentik"
            : configuredMode.Trim().ToLowerInvariant();
        var gateway = credentialMode == "gateway";
        var bounded = gateway || credentialMode == "integration";
        var http = context.Transport is "http" or "streamable-http";

        return new JsonObject
        {
            ["source"] = "active_host_configuration",
            ["ingressAuthenticationMode"] = context.IngressAuthenticationMode ?? (http ? "not_declared" : "stdio"),
            ["downstreamCredentialMode"] = credentialMode,
            ["executionIdentity"] = gateway ? "request_credential_owner"
                : http ? "separately_configured_shared_principal" : "configured_api_principal",
            ["ingressCallerUsedForApi"] = http ? (bool?)gateway : null,
            ["inboundTokenForwarded"] = false,
            ["ordinaryTenantGrantsConferAdministrator"] = false,
            ["instanceAdministration"] = bounded ? "denied_by_bounded_credential_policy" : "requires_current_api_helpdesk_admin",
            ["exactAiScheme"] = bounded ? "denied_by_bounded_credential_policy" : "requires_exact_authentik_ai_agent",
            ["authorityVerified"] = false,
            ["accessVerificationOperation"] = "helpdesk_auth.access",
            ["aiSchemeVerificationOperation"] = bounded ? null : "helpdesk_auth.status",
            ["authorizationEvidence"] = "Read the execution principal's current API access, then verify each operation and its own prerequisites. Configuration and discovery do not establish authorization.",
            ["identityBoundary"] = gateway
                ? "The API reloads the credential owner's current account and intersects approved permissions and organization on every request. No persistent downstream credential is stored."
                : http
                    ? "Every admitted ingress caller uses this host's same separately configured downstream principal. Ingress roles are not downstream roles; separate hosts and explicit ingress restrictions are required for distinct execution authorities."
                    : "The explicitly configured API principal supplies execution authority; no ambient HTTP or other-profile identity is substituted."
        };
    }
}
