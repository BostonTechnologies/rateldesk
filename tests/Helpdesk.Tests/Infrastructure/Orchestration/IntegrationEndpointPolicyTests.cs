using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using System.Net;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class IntegrationEndpointPolicyTests
{
    [Fact]
    public void Deliberate_private_https_provider_targets_remain_allowed()
    {
        var uri = new Uri("https://10.20.30.40/internal/health");

        IntegrationEndpointPolicy.Validate(uri, "BaseUrl");

        Assert.True(IntegrationEndpointPolicy.IsAllowed(uri));
    }

    [Theory]
    [InlineData("http://169.254.169.254/internal/health")]
    [InlineData("https://metadata.google.internal/internal/health")]
    [InlineData("https://10.20.30.40/internal/health?secret=bad")]
    [InlineData("https://10.20.30.40/internal/health#fragment")]
    public void Reserved_metadata_and_ambiguous_provider_targets_are_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.Validate(new Uri(value), "BaseUrl"));
    }

    [Fact]
    public void Netclaw_requires_the_exact_session_path_without_fragment()
    {
        var options = new AiAssistantChatOptions
        {
            Enabled = true,
            DeviceToken = "synthetic-token",
            Endpoint = "https://netclaw.example.test/hub/session#wrong"
        };

        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData("http://10.20.30.40/internal/health")]
    [InlineData("http://provider.example.test/internal/health")]
    public void Plain_http_requires_explicit_private_opt_in(string value)
    {
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.Validate(new Uri(value), "BaseUrl"));
    }

    [Fact]
    public void Private_http_opt_in_still_rejects_public_or_mapped_addresses()
    {
        var uri = new Uri("http://provider.example.test/internal/health");

        IntegrationEndpointPolicy.ValidateResolvedAddresses(
            uri,
            [IPAddress.Parse("10.20.30.40")],
            "BaseUrl",
            allowPrivateHttp: true);

        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateResolvedAddresses(
            uri,
            [IPAddress.Parse("203.0.113.10")],
            "BaseUrl",
            allowPrivateHttp: true));
    }

    [Fact]
    public void Resolved_ipv6_link_local_is_rejected_and_ipv6_private_http_is_allowed()
    {
        var uri = new Uri("http://provider.example.test/internal/health");

        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateResolvedAddresses(
            uri,
            [IPAddress.Parse("fe80::1")],
            "BaseUrl",
            allowPrivateHttp: true));

        IntegrationEndpointPolicy.ValidateResolvedAddresses(
            uri,
            [IPAddress.Parse("fd00::1")],
            "BaseUrl",
            allowPrivateHttp: true);
    }

    [Fact]
    public void Netclaw_private_http_validation_accepts_ipv6_ula_literals()
    {
        var options = new AiAssistantChatOptions
        {
            Enabled = true,
            DeviceToken = "synthetic-token",
            AllowPrivateHttp = true,
            Endpoint = "http://[fd00::1]/hub/session"
        };

        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData("https://netclaw.example.test/hub/session/negotiate?negotiateVersion=1")]
    [InlineData("https://netclaw.example.test/hub/session?id=connection-token")]
    [InlineData("wss://netclaw.example.test/hub/session?id=connection-token&access_token=device-token")]
    public void SignalR_protocol_queries_are_allowed_only_on_the_configured_hub(string value)
    {
        var configured = new Uri("https://netclaw.example.test/hub/session");

        IntegrationEndpointPolicy.ValidateSignalRRequest(configured, new Uri(value), allowPrivateHttp: false);
    }

    [Theory]
    [InlineData("https://netclaw.example.test/admin?next=bad")]
    [InlineData("https://other.example.test/hub/session?id=connection-token")]
    [InlineData("https://netclaw.example.test/hub/session/negotiate?unexpected=bad")]
    [InlineData("https://netclaw.example.test/other?id=connection-token")]
    [InlineData("http://netclaw.example.test/hub/session?id=connection-token")]
    public void SignalR_protocol_validation_rejects_redirects_and_unapproved_queries(string value)
    {
        var configured = new Uri("https://netclaw.example.test/hub/session");

        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateSignalRRequest(configured, new Uri(value), allowPrivateHttp: false));
    }

    [Fact]
    public void SignalR_resolved_addresses_are_checked_for_fallback_and_websocket_requests()
    {
        var configured = new Uri("http://10.20.30.40/hub/session");
        var negotiate = new Uri("http://10.20.30.40/hub/session/negotiate?negotiateVersion=1");
        var websocket = new Uri("ws://10.20.30.40/hub/session?id=connection-token");

        IntegrationEndpointPolicy.ValidateSignalRResolvedAddresses(configured, negotiate, [IPAddress.Parse("10.20.30.40")], "Netclaw SignalR endpoint", true);
        IntegrationEndpointPolicy.ValidateSignalRResolvedAddresses(configured, websocket, [IPAddress.Parse("10.20.30.40")], "Netclaw SignalR endpoint", true);
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateSignalRResolvedAddresses(
            configured,
            websocket,
            [IPAddress.Parse("203.0.113.10")],
            "Netclaw SignalR endpoint",
            true));
    }
}
