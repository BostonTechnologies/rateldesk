extern alias NewWeb;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NewWeb::HelpDesk.NewWeb.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.NewWeb;

public class SystemTokenServiceTests
{
    [Fact]
    public async Task GetTokenAsync_Uses_SystemTokenSecret_From_Environment_Config()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiBaseUrl"] = "https://helpdesk-api.test/",
                ["SYSTEM_TOKEN_SECRET"] = "system-secret"
            })
            .Build();

        AuthenticationHeaderValue? secretHeader = null;
        Uri? requestTarget = null;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("SystemApiNoAuth").Returns(new HttpClient(new StubMessageHandler(request =>
        {
            requestTarget = request.RequestUri;
            request.Headers.TryGetValues("X-System-Secret", out var values);
            secretHeader = values is null
                ? null
                : new AuthenticationHeaderValue("X-System-Secret", values.Single());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token":"system-token"}""", Encoding.UTF8, "application/json")
            };
        })) { BaseAddress = new Uri("https://helpdesk-api.test/") });

        var service = new SystemTokenService(factory, configuration);
        configuration["ApiBaseUrl"] = "https://changed-api.test/";

        var token = await service.GetTokenAsync();

        Assert.Equal("system-token", token);
        Assert.Equal("system-secret", secretHeader?.Parameter);
        Assert.Equal("https://helpdesk-api.test/api/v1/system/token", requestTarget!.AbsoluteUri);
    }

    [Fact]
    public async Task GetTokenAsync_Throws_When_SystemTokenSecret_Is_Missing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiBaseUrl"] = "https://helpdesk-api.test/"
            })
            .Build();

        var factory = Substitute.For<IHttpClientFactory>();
        var service = new SystemTokenService(factory, configuration);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTokenAsync());

        Assert.Contains("SYSTEM_TOKEN_SECRET", ex.Message, StringComparison.Ordinal);
    }

    private sealed class StubMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
