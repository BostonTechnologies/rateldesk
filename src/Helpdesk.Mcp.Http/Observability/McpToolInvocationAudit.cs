using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Http.Authorization;
using Helpdesk.Mcp.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

namespace Helpdesk.Mcp.Http.Observability;

/// <summary>
/// Emits bounded, structured diagnostics for parsed MCP tool calls. It never
/// inspects raw HTTP request bodies or authorization headers.
/// </summary>
public sealed class McpToolInvocationAudit(
    HelpdeskMcpHostContext hostContext,
    ILogger<McpToolInvocationAudit> logger)
{
    private static readonly IReadOnlySet<string> KnownTools = McpOperationCatalog.All
        .Select(operation => operation.Tool).ToHashSet(StringComparer.Ordinal);

    public async ValueTask<CallToolResult> InvokeAsync(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        // The verifier guard rejects arbitrary selectors before SDK validation.
        // Derive safe labels before invocation so cancellation/error logs cannot
        // expose those selectors either; the original request remains unchanged.
        var denied = SetupVerificationAuthorization.IsDenied(context.User, context.Params.Name, context.Params.Arguments);
        var tool = denied ? KnownTools.Contains(context.Params.Name) ? context.Params.Name : "unknown" : Token(context.Params.Name);
        var operation = denied ? SetupVerificationAuthorization.OperationDeniedCode : Operation(context.Params.Arguments);
        try
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            var details = ResultDetails(result);
            var outcome = DescribeOutcome(result);
            logger.LogInformation(
                "MCP tool invocation completed. Instance={Instance} Transport={Transport} Tool={Tool} Operation={Operation} Caller={Caller} Success={Success} TransportSucceeded={TransportSucceeded} ProtocolSucceeded={ProtocolSucceeded} OperationSucceeded={OperationSucceeded} OverallSucceeded={OverallSucceeded} ResultStatus={ResultStatus} CorrelationId={CorrelationId} DurationMs={DurationMs} TraceId={TraceId}",
                hostContext.Instance,
                hostContext.Transport,
                tool,
                operation,
                Caller(context.User),
                result.IsError is not true, // Preserve historical Success mapping; new fields describe actual operation success.
                outcome.TransportSucceeded,
                outcome.ProtocolSucceeded,
                outcome.OperationSucceeded,
                outcome.OverallSucceeded,
                details.Status,
                details.CorrelationId,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                Activity.Current?.TraceId.ToString());
            return result;
        }
        catch (Exception)
        {
            logger.LogWarning(
                "MCP tool invocation failed. Instance={Instance} Transport={Transport} Tool={Tool} Operation={Operation} Caller={Caller} DurationMs={DurationMs} TraceId={TraceId}",
                hostContext.Instance,
                hostContext.Transport,
                tool,
                operation,
                Caller(context.User),
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                Activity.Current?.TraceId.ToString());
            throw;
        }
    }

    public static McpInvocationOutcome DescribeOutcome(CallToolResult result)
    {
        bool? operation = result.StructuredContent is { ValueKind: JsonValueKind.Object } content &&
            content.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? success.ValueKind == JsonValueKind.True : null;
        // A returned CallToolResult is a protocol response even when its tool reports isError.
        // Keep the historical log's Success value above; new fields name the actual layers.
        if (result.IsError is true) operation = false;
        return new(true, true, operation, operation);
    }
    public sealed record McpInvocationOutcome(bool TransportSucceeded, bool ProtocolSucceeded, bool? OperationSucceeded, bool? OverallSucceeded);

    private static (string Status, string CorrelationId) ResultDetails(CallToolResult result)
    {
        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } content)
            return (result.IsError is true ? "error" : "unknown", "none");

        return (Token(content, "status"), Token(content, "correlationId"));
    }

    private static string Operation(IDictionary<string, JsonElement>? arguments)
        => arguments is not null && arguments.TryGetValue("operation", out var operation) && operation.ValueKind == JsonValueKind.String
            ? Token(operation.GetString())
            : "unknown";

    private static string Caller(ClaimsPrincipal? user)
        => Token(user?.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? user?.FindFirstValue("sub")
                   ?? user?.FindFirstValue("client_id")
                   ?? user?.FindFirstValue("azp")
                   ?? "anonymous");

    private static string Token(JsonElement objectValue, string property)
        => objectValue.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? Token(value.GetString())
            : "unknown";

    private static string Token(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Contains("rdk_", StringComparison.OrdinalIgnoreCase) || value.Contains("rdx_", StringComparison.OrdinalIgnoreCase))
            return "unknown";

        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '/' or '.')
            ? value
            : "unknown";
    }
}
