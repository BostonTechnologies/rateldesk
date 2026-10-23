extern alias NewWeb;
using AngleSharp.Html.Parser;
using System.Net;
using EmailDisplayDocument = NewWeb::HelpDesk.NewWeb.Services.EmailDisplayDocument;
using TicketMessageContent = NewWeb::HelpDesk.NewWeb.Services.TicketMessageContent;

namespace Helpdesk.Tests.NewWeb;

public class EmailDisplayDocumentTests
{
    [Theory]
    [InlineData("<div style='background-color:#fff;color:#111'>Light email</div>")]
    [InlineData("<body style='background-color:#111;color:#eee'><p>Dark email</p></body>")]
    [InlineData("<html style='background-color:#111;color:#eee'><body><p>Dark email</p></body></html>")]
    public void PreservesOriginalColourPairAndExplicitDefaults(string html)
    {
        var result = EmailDisplayDocument.Create(html, null, "https://desk.example.test/");
        var document = new HtmlParser().ParseDocument(result.Html);
        Assert.Contains("background-color", document.Body!.InnerHtml);
        Assert.Contains("color", document.Body.InnerHtml);
        Assert.Contains("background:#fff;color:#111", result.Html);
        Assert.Contains("email", result.Text);
    }

    [Theory]
    [InlineData("<div style='background-color:#111'>Dark canvas</div>", "color:#ffffff")]
    [InlineData("<div style='color:#fff'>Light foreground</div>", "background-color:#111111")]
    [InlineData("<div style='background-color:white'>Light canvas</div>", "color:#111111")]
    public void MissingPairReceivesReadableDefault(string html, string expected)
    {
        var result = EmailDisplayDocument.Create(html, null, "https://desk.example.test/");
        Assert.Contains(expected, result.Html);
    }

    [Theory]
    [InlineData("<script>parent.evil=true</script><p onmouseover='evil()'>Safe</p>")]
    [InlineData("<svg onload='evil()'><script>evil()</script></svg><math><a href='javascript:evil()'>X</a></math>")]
    [InlineData("<iframe srcdoc='<script>evil()</script>'></iframe><object data='https://tracker.example.test'></object><form action='https://tracker.example.test'><input autofocus></form>")]
    [InlineData("<style>@import 'https://tracker.example.test';body{position:fixed}</style><p style='background-image:url(https://tracker.example.test);position:fixed;color:red'>Safe</p>")]
    [InlineData("<a href='javascript:evil()'>Unsafe</a><a href='data:text/html,evil'>Unsafe</a><img src='data:image/svg+xml,<svg onload=evil()>'>")]
    public void UntrustedActiveContentCannotCrossDisplayBoundary(string html)
    {
        var result = EmailDisplayDocument.Create(html, null, "https://desk.example.test/");
        var document = new HtmlParser().ParseDocument(result.Html);
        Assert.Single(document.QuerySelectorAll("script")); // only the hash-pinned geometry/Escape bridge
        Assert.Empty(document.QuerySelectorAll("iframe,object,svg,math,form,input,[onload],[onmouseover],[href]"));
        Assert.DoesNotContain("evil", document.Body!.InnerHtml);
        Assert.DoesNotContain("tracker.example.test", result.Html);
        Assert.DoesNotContain("position:fixed", result.Html);
        Assert.Contains("script-src 'sha256-", WebUtility.HtmlDecode(result.Html));
        Assert.Contains("connect-src 'none'", WebUtility.HtmlDecode(result.Html));
    }

    [Fact]
    public void SignedImagesStayOnAuthorizedGatewayAndRemoteCidAndSvgHaveUsefulFallbacks()
    {
        var result = EmailDisplayDocument.Create("<img src='https://historical.example.test/api/incidents/ticket-1/images/photo.png?token=scoped'><img src='/api/worklogs/log-1/images/photo.png?token=scoped'><img src='https://tracker.example.test/pixel' alt='Logo'><img src='cid:missing' alt='Chart'><img src='data:image/svg+xml,bad'>", null, "https://desk.example.test/");
        var document = new HtmlParser().ParseDocument(result.Html);
        Assert.Equal(2, document.QuerySelectorAll("img").Length);
        Assert.All(document.QuerySelectorAll("img"), image => Assert.StartsWith("https://desk.example.test/api/", image.GetAttribute("src")));
        Assert.Equal(3, result.BlockedImages);
        Assert.Contains("Image unavailable: Logo", result.Text);
        Assert.Contains("Image unavailable: Chart", result.Text);
        Assert.DoesNotContain("historical.example.test", result.Html);
        Assert.DoesNotContain("tracker.example.test", result.Html);
    }

    [Fact]
    public void ExplicitPlainTextIsNeverTruncatedAndSafeLinksAreOutsideDocument()
    {
        var text = new string('x', 900);
        var result = EmailDisplayDocument.Create("<a href='https://example.test/path'>Read more</a><a href='/relative'>Relative</a>", text, "https://desk.example.test/");
        Assert.Equal(text, result.Text);
        Assert.Equal(new EmailDisplayDocument.Link("Read more", "https://example.test/path"), Assert.Single(result.Links));
        Assert.DoesNotContain("href=", result.Html);
    }

    [Theory]
    [InlineData("incidents", "/api/v1/incidents/ticket-1/peek", "{\"html\":\"<p>Email</p>\",\"text\":\"Full message\",\"snippetText\":\"Short\"}", "<p>Email</p>", "Full message")]
    [InlineData("requests", "/api/v1/requests/ticket-1", "{\"description\":\"<p>Literal request</p>\"}", null, "<p>Literal request</p>")]
    [InlineData("changes", "/api/v1/changes/ticket-1", "{\"description\":\"Change description\"}", null, "Change description")]
    public async Task ActualTicketContentApisPreservePlainTextSemantics(string type, string path, string json, string? html, string text)
    {
        var handler = new StubHandler(json);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://desk.example.test/") };
        var result = await TicketMessageContent.LoadAsync(client, type, "ticket-1", CancellationToken.None);
        Assert.Equal(path, handler.Path);
        Assert.Equal(html, result!.Html);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public async Task CancellationIsForwardedAndUnsupportedTypeMakesNoApiCall()
    {
        var handler = new StubHandler("{}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://desk.example.test/") };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TicketMessageContent.LoadAsync(client, "incidents", "ticket", cancellation.Token));
        handler.Path = null;
        await Assert.ThrowsAsync<NotSupportedException>(() => TicketMessageContent.LoadAsync(client, "unknown", "ticket", CancellationToken.None));
        Assert.Null(handler.Path);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? Path { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
