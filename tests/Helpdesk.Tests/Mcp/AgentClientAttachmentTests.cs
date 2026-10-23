using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;

namespace Helpdesk.Tests.Mcp;

public sealed class AgentClientAttachmentTests
{
    [Theory]
    [InlineData(false, "/api/v1/tickets/ticket-1/attachments/", "files")]
    [InlineData(true, "/api/v1/email-templates/notice/images", "file")]
    public async Task Confirmed_upload_uses_the_canonical_multipart_field_authenticated_target_and_exact_bytes(bool template, string path, string field)
    {
        var calls = 0;
        var client = Client(async request =>
        {
            calls++;
            Assert.Equal("https://api.example.test" + path, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer rdk_synthetic_attachment_credential", request.Headers.Authorization!.ToString());
            Assert.True(request.Headers.Contains("X-Correlation-Id"));
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var file = Assert.Single(multipart);
            Assert.Equal(field, file.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("fixture.txt", file.Headers.ContentDisposition.FileName!.Trim('"'));
            Assert.Equal(template ? "image/png" : "text/plain", file.Headers.ContentType!.MediaType);
            Assert.Equal("bounded fixture", await file.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"id\":\"attachment-1\",\"url\":\"/email-brand/fixture.txt\"}") };
        });
        var tools = Tools(client);
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [template ? "templateName" : "ticketId"] = template ? "notice" : "ticket-1",
            ["body"] = new { fileName = "fixture.txt", contentType = template ? "image/png" : "text/plain", contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("bounded fixture")) }
        }));
        var preview = template ? await tools.helpdesk_email_settings("upload_template_image", request.RootElement) : await tools.helpdesk_attachments("upload", request.RootElement);
        Assert.Equal("confirmation_required", preview.Status);
        Assert.Equal(0, calls);
        var result = template ? await tools.helpdesk_email_settings("upload_template_image", request.RootElement, true) : await tools.helpdesk_attachments("upload", request.RootElement, true);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Download_preserves_filename_media_type_and_payload_bytes()
    {
        var bytes = new byte[] { 0, 1, 127, 128, 255 };
        var client = Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer rdk_synthetic_attachment_credential", request.Headers.Authorization!.ToString());
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "fixture.bin" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

        var result = await client.DownloadAttachmentAsync("/api/v1/attachments/attachment-1");

        Assert.Equal(bytes, Convert.FromBase64String(result!["contentBase64"]!.GetValue<string>()));
        Assert.Equal("fixture.bin", result["fileName"]!.GetValue<string>());
        Assert.Equal("application/octet-stream", result["contentType"]!.GetValue<string>());
        Assert.Equal(bytes.Length, result["sizeBytes"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Download_enforces_the_limit_with_and_without_content_length(bool declaredLength)
    {
        var bytes = new byte[1048577];
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = declaredLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes)
        }));
        var failure = await Assert.ThrowsAsync<AgentClientRemoteException>(() => client.DownloadAttachmentAsync("/api/v1/attachments/attachment-1"));
        Assert.Equal("attachment_too_large", failure.Code);
        Assert.Equal(413, failure.StatusCode);
    }

    [Fact]
    public async Task An_uncertain_upload_is_not_automatically_retried()
    {
        var calls = 0;
        var client = Client(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("private upstream response") });
        });
        using var request = JsonDocument.Parse("""{"ticketId":"ticket-1","body":{"fileName":"fixture.txt","contentType":"text/plain","contentBase64":"eA=="}}""");
        var response = await Tools(client).helpdesk_attachments("upload", request.RootElement, true);
        Assert.False(response.Success);
        Assert.False(response.Failure!.Retryable);
        Assert.Equal(1, calls);
        Assert.Contains(response.NextActions!, action => action.Contains("before repeating", StringComparison.Ordinal));
        Assert.DoesNotContain("private upstream response", JsonSerializer.Serialize(response));
    }

    private static HelpdeskAgentClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        => new(AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://api.example.test", CredentialMode = "integration", IntegrationCredential = "rdk_synthetic_attachment_credential"
        }, () => new Handler(respond));

    private static HelpdeskTools Tools(IHelpdeskAgentClient client)
        => new(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
