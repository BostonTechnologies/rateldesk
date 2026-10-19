using System.Net;
using System.Text.Json;
using Helpdesk.API.Endpoints.Captcha;
using Helpdesk.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed class CaptchaEndpointsTests
{
    [Fact]
    public async Task Anonymous_captcha_returns_only_id_and_uncached_image_while_answer_stays_on_server()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ICaptchaService, CaptchaService>();
        await using var app = builder.Build();
        app.MapCaptchaEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/v1/captcha/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(["id", "imageBase64"], body.EnumerateObject().Select(property => property.Name));
        Assert.False(body.TryGetProperty("challenge", out _));
        var id = body.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        var dataUri = body.GetProperty("imageBase64").GetString()!;
        Assert.StartsWith("data:image/png;base64,", dataUri, StringComparison.Ordinal);
        var png = Convert.FromBase64String(dataUri["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var answer = app.Services.GetRequiredService<IMemoryCache>().Get<string>(id);
        Assert.NotNull(answer);
        var captcha = app.Services.GetRequiredService<ICaptchaService>();
        Assert.True(captcha.ValidateCaptcha(id, answer));
        Assert.False(captcha.ValidateCaptcha(id, answer));
    }
}
