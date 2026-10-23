using System.Net;
using System.Text;

namespace Helpdesk.Tests.Mcp;

internal static class TimelineTransportPayload
{
    public const string Json = """
        [{"id":"00000000-0000-0000-0000-000000000001","ticketId":"ticket-wire","eventType":7,"hours":0.25,"recordedHours":0.25,"workLogId":"worklog-quarter"},
         {"id":"00000000-0000-0000-0000-000000000002","ticketId":"ticket-wire","eventType":0,"hours":0.625,"recordedHours":0.625,"workLogId":"worklog-fraction"},
         {"id":"00000000-0000-0000-0000-000000000003","ticketId":"ticket-wire","eventType":7,"hours":0,"recordedHours":0,"workLogId":"worklog-zero"},
         {"id":"00000000-0000-0000-0000-000000000004","ticketId":"ticket-wire","eventType":7,"hours":0,"recordedHours":null,"workLogId":null}]
        """;
    public const string Token = "rdk_timeline_transport_fixture";

    public static HttpMessageHandler CreateHandler() => new Handler();

    public static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath == "/token")
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"access_token\":\"" + Token + "\"}", Encoding.UTF8, "application/json") };
        Assert.Equal("Bearer " + Token, request.Headers.Authorization?.ToString());
        Assert.Matches("^/api/v1/(incidents|requests|changes)/ticket-wire/timeline$", request.RequestUri.AbsolutePath);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json, Encoding.UTF8, "application/json") };
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Respond(request));
    }
}
