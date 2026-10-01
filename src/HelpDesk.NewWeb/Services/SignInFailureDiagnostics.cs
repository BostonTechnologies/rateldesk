namespace HelpDesk.NewWeb.Services;

// Only fixed categories and status codes may enter authentication logs.
internal static class SignInFailureDiagnostics
{
    public static string Category(Exception? exception) => exception switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest } => "invalid-provisioning-identity",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "api-token-rejected",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "account-access-denied",
        HttpRequestException => "provisioning-api-unavailable",
        OperationCanceledException => "sign-in-cancelled",
        _ => "external-sign-in-failed"
    };
}
