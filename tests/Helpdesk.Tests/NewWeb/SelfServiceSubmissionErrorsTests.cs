extern alias NewWeb;
using System.Net;
using ErrorMessages = NewWeb::Helpdesk.NewWeb.Services.SelfServiceSubmissionErrors;

namespace Helpdesk.Tests.NewWeb;

public sealed class SelfServiceSubmissionErrorsTests
{
    [Theory]
    [InlineData(400, "")]
    [InlineData(401, "")]
    [InlineData(403, "")]
    [InlineData(500, "")]
    [InlineData(500, "<html>secret stack trace</html>")]
    [InlineData(400, "{bad-json")]
    [InlineData(403, "{\"detail\":\"secret-stack-trace\"}")]
    public async Task Failed_responses_always_show_safe_status_and_retry_guidance(int status, string body)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        var message = await ErrorMessages.ReadAsync(response);
        Assert.Contains($"HTTP {status}", message);
        Assert.True(message.Length > 40);
        Assert.DoesNotContain("secret", message);
        Assert.DoesNotContain("<html>", message);
    }

    [Fact]
    public async Task Known_organization_problem_is_actionable_and_retains_safe_reference()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"code":"request_organization_mismatch","detail":"do not echo arbitrary upstream text","traceId":"ref-123"}""")
        };
        var message = await ErrorMessages.ReadAsync(response);
        Assert.Contains("different organization", message);
        Assert.Contains("Reference: ref-123", message);
        Assert.DoesNotContain("arbitrary", message);
    }
}
