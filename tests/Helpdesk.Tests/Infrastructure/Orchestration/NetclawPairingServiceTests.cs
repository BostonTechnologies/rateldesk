using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Connectivity;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class NetclawPairingServiceTests
{
    [Fact]
    public async Task Exchange_posts_the_expected_contract_and_returns_only_the_token_to_the_server_caller()
    {
        Uri? capturedUri = null;
        HttpMethod? capturedMethod = null;
        bool? capturedPrivateHttp = null;
        string? capturedBody = null;
        using var client = new HttpClient(new StubHandler(async (request, _) =>
        {
            capturedUri = request.RequestUri;
            capturedMethod = request.Method;
            capturedPrivateHttp = request.Options.TryGetValue(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, out var allowed) && allowed;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, "{\"token\":\"synthetic-protected-token\"}");
        }), disposeHandler: true);
        var pairing = new NetclawPairingService(client);

        var token = await pairing.ExchangeCodeAsync(
            new NetclawPairingTarget(new Uri("https://netclaw.example.test/hub/session"), AllowPrivateHttp: false),
            "synthetic-one-time-code");

        Assert.Equal("synthetic-protected-token", token);
        Assert.Equal("https://netclaw.example.test/api/pair/exchange", capturedUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.False(capturedPrivateHttp);
        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal(new[] { "code", "deviceName" }, body.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("synthetic-one-time-code", body.RootElement.GetProperty("code").GetString());
        Assert.StartsWith("rateldesk-api-", body.RootElement.GetProperty("deviceName").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-protected-token", body.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Private_http_permission_is_attached_only_when_explicitly_enabled()
    {
        bool? capturedPermission = null;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            request.Options.TryGetValue(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, out var allowed);
            capturedPermission = allowed;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"token\":\"synthetic-token\"}"));
        }), disposeHandler: true);

        await new NetclawPairingService(client).ExchangeCodeAsync(
            new NetclawPairingTarget(new Uri("http://10.23.45.67/hub/session"), AllowPrivateHttp: true),
            "synthetic-one-time-code");

        Assert.True(capturedPermission);
    }

    [Theory]
    [InlineData("https://metadata.google.internal/hub/session", false)]
    [InlineData("http://203.0.113.10/hub/session", true)]
    [InlineData("http://10.23.45.67/hub/session", false)]
    [InlineData("https://netclaw.example.test/hub/session/other", false)]
    public async Task Unsafe_or_non_hub_pairing_targets_are_rejected_before_http_send(string endpoint, bool allowPrivateHttp)
    {
        var sendCount = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            sendCount++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"token\":\"synthetic-token\"}"));
        }), disposeHandler: true);

        var exception = await Assert.ThrowsAsync<NetclawPairingException>(() =>
            new NetclawPairingService(client).ExchangeCodeAsync(
                new NetclawPairingTarget(new Uri(endpoint), allowPrivateHttp),
                "synthetic-one-time-code"));

        Assert.Equal("invalid_pairing_target", exception.Code);
        Assert.Equal(0, sendCount);
        Assert.DoesNotContain(endpoint, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "pairing_code_rejected")]
    [InlineData(HttpStatusCode.NotFound, "pairing_code_not_found")]
    [InlineData(HttpStatusCode.TooManyRequests, "pairing_rate_limited")]
    [InlineData(HttpStatusCode.Found, "pairing_service_error")]
    public async Task Upstream_failures_are_mapped_without_echoing_response_bodies(HttpStatusCode status, string expectedCode)
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(JsonResponse(status, "synthetic-response-secret"))), disposeHandler: true);

        var exception = await Assert.ThrowsAsync<NetclawPairingException>(() =>
            new NetclawPairingService(client).ExchangeCodeAsync(
                new NetclawPairingTarget(new Uri("https://netclaw.example.test/hub/session"), false),
                "synthetic-one-time-code"));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("synthetic-response-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-one-time-code", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_device_name_retries_with_a_new_name_without_spending_the_code()
    {
        var deviceNames = new List<string>();
        var attempt = 0;
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            deviceNames.Add(body.RootElement.GetProperty("deviceName").GetString()!);
            attempt++;
            return attempt < 3
                ? JsonResponse(HttpStatusCode.Conflict, "synthetic-duplicate")
                : JsonResponse(HttpStatusCode.OK, "{\"token\":\"synthetic-token\"}");
        }), disposeHandler: true);

        var token = await new NetclawPairingService(client).ExchangeCodeAsync(
            new NetclawPairingTarget(new Uri("https://netclaw.example.test/hub/session"), false),
            "synthetic-one-time-code");

        Assert.Equal("synthetic-token", token);
        Assert.Equal(3, deviceNames.Count);
        Assert.Equal(deviceNames.Count, deviceNames.Distinct(StringComparer.Ordinal).Count());
        Assert.All(deviceNames, name => Assert.StartsWith("rateldesk-api-", name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deadline_covers_a_stalled_pairing_response_body()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledReadStream())
            })), disposeHandler: true);

        var pairing = new NetclawPairingService(client, TimeSpan.FromMilliseconds(30));
        var exception = await Assert.ThrowsAsync<NetclawPairingException>(() => pairing.ExchangeCodeAsync(
            new NetclawPairingTarget(new Uri("https://netclaw.example.test/hub/session"), false),
            "synthetic-one-time-code"));

        Assert.Equal("pairing_timeout", exception.Code);
    }

    [Fact]
    public async Task Response_body_read_failure_reports_uncertain_outcome_without_echoing_details()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultingReadStream())
            })), disposeHandler: true);

        var exception = await Assert.ThrowsAsync<NetclawPairingException>(() => new NetclawPairingService(client).ExchangeCodeAsync(
            new NetclawPairingTarget(new Uri("https://netclaw.example.test/hub/session"), false),
            "synthetic-one-time-code"));

        Assert.Equal("pairing_outcome_uncertain", exception.Code);
        Assert.DoesNotContain("synthetic-body-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-one-time-code", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_failure_reports_uncertain_outcome_without_echoing_request_details()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic-one-time-code transport detail"))), disposeHandler: true);

        var exception = await Assert.ThrowsAsync<NetclawPairingException>(() =>
            new NetclawPairingService(client).ExchangeCodeAsync(
                new NetclawPairingTarget(new Uri("https://netclaw.example.test/hub/session"), false),
                "synthetic-one-time-code"));

        Assert.Equal("pairing_outcome_uncertain", exception.Code);
        Assert.DoesNotContain("synthetic-one-time-code", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_pairing_client_does_not_retry_a_transient_failure_inherited_from_http_defaults()
    {
        var requestCount = 0;
        var handler = new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, "synthetic transient response"));
        });
        var directory = Path.Combine(Path.GetTempPath(), "rateldesk-netclaw-pairing-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:Sqlite:Path"] = Path.Combine(directory, "helpdesk.db")
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
#pragma warning disable EXTEXP0001
            services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());
#pragma warning restore EXTEXP0001
            services.AddHelpdeskInfrastructure(configuration);
            services.Configure<HttpClientFactoryOptions>(nameof(INetclawPairingService), options =>
                options.HttpMessageHandlerBuilderActions.Add(builder =>
                {
                    var configuredHandler = builder.PrimaryHandler;
                    builder.PrimaryHandler = handler;
                    configuredHandler.Dispose();
                }));

            await using var serviceProvider = services.BuildServiceProvider();
            var pairing = serviceProvider.GetRequiredService<INetclawPairingService>();
            var exception = await Assert.ThrowsAsync<NetclawPairingException>(() => pairing.ExchangeCodeAsync(
                new NetclawPairingTarget(new Uri("https://netclaw.example.test/hub/session"), AllowPrivateHttp: false),
                "synthetic-one-time-code"));

            Assert.Equal("pairing_service_error", exception.Code);
            Assert.Equal(1, Volatile.Read(ref requestCount));
            Assert.DoesNotContain("synthetic transient response", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            handler.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Production_safe_handler_disables_redirects_and_proxies()
    {
        using var handler = IntegrationSafeHttpMessageHandler.Create();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }

    private sealed class StalledReadStream : Stream
    {
        private static readonly TaskCompletionSource<bool> NeverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await NeverCompletes.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    private sealed class FaultingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("synthetic-body-detail"));
    }
}
