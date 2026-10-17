using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Helpdesk.Mcp.Http.Authorization;

/// <summary>Checks the API-issued identity, independently of tool permission mappings.</summary>
public static class SetupVerificationAuthorization
{
    public const string OperationDeniedCode = "setup_verification_operation_denied";
    private static readonly JsonElement DeniedContent = JsonSerializer.SerializeToElement(new HelpdeskToolResponse(
        false,
        "forbidden",
        "Setup verification permits only helpdesk_auth/status.",
        NextActions: ["Connect an ordinary client with explicitly approved business permissions."],
        Failure: new HelpdeskToolFailure(OperationDeniedCode, false)), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// Returns a known tool failure before calling the SDK's next handler. The text
    /// content carries the same bounded contract for clients predating structuredContent.
    /// Unrelated errors and cancellation retain their original meaning.
    /// </summary>
    public static ValueTask<CallToolResult> InvokeToolAsync(
        ClaimsPrincipal? user,
        string name,
        IDictionary<string, JsonElement>? arguments,
        Func<CancellationToken, ValueTask<CallToolResult>> next,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDenied(user, name, arguments)) return next(cancellationToken);

        return ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            StructuredContent = DeniedContent,
            Content = [new TextContentBlock { Text = DeniedContent.GetRawText() }]
        });
    }

    public static void RequireStatus(ClaimsPrincipal? user, string name, IDictionary<string, JsonElement>? arguments)
    {
        if (IsDenied(user, name, arguments)) Deny();
    }

    private static bool IsDenied(ClaimsPrincipal? user, string name, IDictionary<string, JsonElement>? arguments)
        => user?.HasClaim("setup_verification", "true") == true &&
            (name != "helpdesk_auth" || arguments is null || arguments.Keys.Any(key => key is not ("operation" or "confirm" or "request")) ||
            !arguments.TryGetValue("operation", out var operation) || operation.ValueKind != JsonValueKind.String || operation.GetString() != "status" ||
            (arguments.TryGetValue("confirm", out var confirm) && confirm.ValueKind != JsonValueKind.False) ||
            (arguments.TryGetValue("request", out var request) && request.ValueKind != JsonValueKind.Null));

    public static void RequireOrdinary(ClaimsPrincipal? user)
    {
        if (user?.HasClaim("setup_verification", "true") == true) Deny();
    }

    private static void Deny() => throw new McpException("Setup verification permits only helpdesk_auth/status.");
}
