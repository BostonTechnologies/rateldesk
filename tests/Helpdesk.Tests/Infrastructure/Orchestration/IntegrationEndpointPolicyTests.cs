using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence.Connectivity;
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
}
