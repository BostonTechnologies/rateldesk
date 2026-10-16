using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Http.Observability;
using Helpdesk.Mcp.Http.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using System.Security.Claims;
using Xunit;

namespace Helpdesk.Tests.Setup;

public sealed class SetupVerificationSessionTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task Temporary_access_is_revoked_for_every_verification_outcome_and_never_saved_to_a_profile(string outcome)
    {
        using var fixture = new Fixture();
        await using var verifier = fixture.Session();
        await verifier.StartAsync();
        await verifier.PollAsync();
        var credential = verifier.Credential!;
        Assert.Empty(credential.Permissions);
        if (outcome == "cancel")
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.RunAsync((_, _) => Task.FromResult(true), cancellation.Token));
        }
        else if (outcome == "failure")
            await Assert.ThrowsAsync<IOException>(() => verifier.RunAsync<bool>((_, _) => throw new IOException("synthetic diagnostic failure")));
        else Assert.True(await verifier.RunAsync((_, _) => Task.FromResult(true)));
        Assert.Equal(1, fixture.Cancellations);
        Assert.Null(verifier.Credential); Assert.False(verifier.CleanupPending);
        await verifier.CleanupAsync();
        Assert.Equal(1, fixture.Cancellations);
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.DirectoryPath), path => path.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreachable_cleanup_retains_only_private_cancellation_capability_and_blocks_accumulation_until_recovery()
    {
        using var fixture = new Fixture { CleanupUnavailable = true };
        await using (var verifier = fixture.Session())
        {
            await verifier.StartAsync(); await verifier.PollAsync();
            Assert.True(await verifier.RunAsync((_, _) => Task.FromResult(true)));
            Assert.True(verifier.CleanupPending); Assert.Null(verifier.Credential);
            var record = File.ReadAllText(fixture.CleanupPath);
            Assert.DoesNotContain("rdk_test_secret", record); Assert.Contains(fixture.DeviceCode, record);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.CleanupPath));
        }
        await using (var blocked = fixture.Session())
        {
            Assert.False(await blocked.RecoverAsync());
            await Assert.ThrowsAsync<AgentClientValidationException>(() => blocked.StartAsync());
            Assert.Null(blocked.Pairing);
        }
        fixture.CleanupUnavailable = false;
        await using var recovered = fixture.Session();
        Assert.True(await recovered.RecoverAsync()); Assert.False(recovered.CleanupPending); Assert.False(File.Exists(fixture.CleanupPath));
        await recovered.StartAsync(); Assert.NotNull(recovered.Pairing);
    }

    [Fact]
    public async Task Cancelled_poll_revokes_its_owned_attempt_before_disposal()
    {
        using var fixture = new Fixture();
        await using var verifier = fixture.Session();
        await verifier.StartAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.PollAsync(cancelled.Token));
        Assert.Equal(1, fixture.Cancellations); Assert.Null(verifier.Pairing); Assert.Null(verifier.Credential); Assert.False(verifier.CleanupPending);
    }

    [Fact]
    public async Task Legacy_server_cannot_issue_an_ordinary_credential_as_a_temporary_verifier()
    {
        using var fixture = new Fixture { AdvertiseVerifier = false };
        await using var verifier = fixture.Session();
        await Assert.ThrowsAsync<AgentClientValidationException>(() => verifier.StartAsync());
        Assert.Equal(0, fixture.Starts); Assert.Null(verifier.Pairing);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Audit_preserves_protocol_mapping_and_separates_actual_operation_outcome(bool protocol, bool operation, bool overall)
    {
        var result = new CallToolResult { IsError = !protocol, StructuredContent = JsonSerializer.SerializeToElement(new { success = operation }) };
        var outcome = McpToolInvocationAudit.DescribeOutcome(result);
        Assert.True(outcome.TransportSucceeded); Assert.True(outcome.ProtocolSucceeded); Assert.Equal(protocol && operation, outcome.OperationSucceeded); Assert.Equal(overall, outcome.OverallSucceeded);
        Assert.Null(McpToolInvocationAudit.DescribeOutcome(new CallToolResult()).OverallSucceeded);
    }

    [Theory]
    [InlineData("helpdesk_health", "get")]
    [InlineData("helpdesk_capabilities", "get")]
    [InlineData("helpdesk_config", "show")]
    [InlineData("helpdesk_system", "version")]
    [InlineData("helpdesk_auth", "other")]
    [InlineData("helpdesk_incidents", "list")]
    public void Temporary_verifier_does_not_inherit_other_none_mapped_or_business_operations(string tool, string operation)
    {
        var verifier = new ClaimsPrincipal(new ClaimsIdentity([new Claim("setup_verification", "true")], "Test"));
        var arguments = new Dictionary<string, JsonElement> { ["operation"] = JsonSerializer.SerializeToElement(operation) };
        Assert.Throws<McpException>(() => SetupVerificationAuthorization.RequireStatus(verifier, tool, arguments));
        SetupVerificationAuthorization.RequireStatus(new ClaimsPrincipal(new ClaimsIdentity([], "Test")), tool, arguments);
        Assert.Throws<McpException>(() => SetupVerificationAuthorization.RequireOrdinary(verifier));
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "rdk-verifier-tests-" + Guid.NewGuid().ToString("N"));
        public string CleanupPath => Path.Combine(DirectoryPath, "cleanup.json");
        public string DeviceCode { get; } = new('a', 64);
        public bool CleanupUnavailable { get; set; }
        public bool AdvertiseVerifier { get; set; } = true;
        public int Starts { get; private set; }
        public int Cancellations { get; private set; }
        public SetupVerificationSession Session() => new("https://desk.example", CleanupPath, new HttpClient(new Handler(Respond)));
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities", StringComparison.Ordinal)) return Json(new PairingCapabilities("RatelDesk", 1, true, "https://desk.example", "https://desk.example/account/integration-credentials/approve", 30, 90) { SetupVerificationSupported = AdvertiseVerifier });
            if (route.EndsWith("start", StringComparison.Ordinal))
            {
                Starts++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("setup-verification", body.RootElement.GetProperty("grantKind").GetString());
                Assert.Equal("mcp", body.RootElement.GetProperty("purpose").GetString());
                return Json(new PairingSession(DeviceCode, "ABCD-EFGH", "https://desk.example/account/integration-credentials/approve", DateTimeOffset.UtcNow.AddMinutes(10), 5));
            }
            if (route.EndsWith("poll", StringComparison.Ordinal)) return Json(new PairingPollResult("issued", 5, new(Guid.NewGuid(), "rdk_prefix", "rdk_test_secret", "mcp", "org-a", [], DateTimeOffset.UtcNow.AddMinutes(4), "https://desk.example/mcp") { GrantKind = "setup-verification" }));
            if (route.EndsWith("cancel", StringComparison.Ordinal)) { Cancellations++; return new(CleanupUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent); }
            throw new InvalidOperationException("Unexpected pairing request.");
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
