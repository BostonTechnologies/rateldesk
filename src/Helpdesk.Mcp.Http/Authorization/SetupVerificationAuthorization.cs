using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol;

namespace Helpdesk.Mcp.Http.Authorization;

/// <summary>Checks the API-issued identity, independently of tool permission mappings.</summary>
public static class SetupVerificationAuthorization
{
    public static void RequireStatus(ClaimsPrincipal? user, string name, IDictionary<string, JsonElement>? arguments)
    {
        if (user?.HasClaim("setup_verification", "true") != true) return;
        if (name != "helpdesk_auth" || arguments is null || arguments.Keys.Any(key => key is not ("operation" or "confirm" or "request")) ||
            !arguments.TryGetValue("operation", out var operation) || operation.ValueKind != JsonValueKind.String || operation.GetString() != "status" ||
            (arguments.TryGetValue("confirm", out var confirm) && confirm.ValueKind != JsonValueKind.False) ||
            (arguments.TryGetValue("request", out var request) && request.ValueKind != JsonValueKind.Null)) Deny();
    }

    public static void RequireOrdinary(ClaimsPrincipal? user)
    {
        if (user?.HasClaim("setup_verification", "true") == true) Deny();
    }

    private static void Deny() => throw new McpException("Setup verification permits only helpdesk_auth/status.");
}
