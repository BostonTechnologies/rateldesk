using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class HelpdeskMcpTargetTests
{
    [Fact]
    public void Resolve_accepts_matching_dev_endpoint_and_normalizes_trailing_slashes()
    {
        using var environment = new EnvironmentVariables(
            (HelpdeskMcpTarget.InstanceEnvironmentVariable, "dev"),
            (HelpdeskMcpTarget.ConfigurationEnvironmentVariable, "/tmp/helpdesk-dev.json"),
            ("RATELDESK_MCP_DEV_API_BASE_URL", "https://dev.example/api/"));

        var target = HelpdeskMcpTarget.Resolve(Configuration("https://dev.example/api"), "/tmp/helpdesk-dev.json");

        Assert.Equal("dev", target.Instance);
        Assert.Equal("https://dev.example/api", target.CanonicalApiBaseUrl);
    }

    [Theory]
    [InlineData(null, "https://dev.example", "RATELDESK_MCP_INSTANCE must be a lowercase instance label containing letters, digits, and hyphens.")]
    [InlineData("stage", "https://stage.example", "RATELDESK_MCP_INSTANCE 'stage' requires")]
    [InlineData("dev", null, "RATELDESK_MCP_DEV_API_BASE_URL must be an absolute http or https URL without credentials, query, or fragment.")]
    [InlineData("prod", "https://prod.example", "RATELDESK_MCP_INSTANCE 'prod' requires")]
    public void Resolve_rejects_missing_or_mismatched_target_configuration(string? instance, string? expectedEndpoint, string error)
    {
        using var environment = new EnvironmentVariables(
            (HelpdeskMcpTarget.InstanceEnvironmentVariable, instance),
            (HelpdeskMcpTarget.ConfigurationEnvironmentVariable, "/tmp/helpdesk-prod.json"),
            (instance is null ? "RATELDESK_MCP_DEV_API_BASE_URL" : $"RATELDESK_MCP_{instance.ToUpperInvariant()}_API_BASE_URL", expectedEndpoint));

        var exception = Assert.Throws<AgentClientValidationException>(() => HelpdeskMcpTarget.Resolve(Configuration("https://dev.example"), "/tmp/helpdesk-prod.json"));

        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_accepts_environment_only_configuration_with_an_explicit_bound_target()
    {
        using var environment = new EnvironmentVariables(
            (HelpdeskMcpTarget.InstanceEnvironmentVariable, "dev"),
            ("RATELDESK_MCP_DEV_API_BASE_URL", "https://dev.example"));

        var target = HelpdeskMcpTarget.Resolve(Configuration("https://dev.example"), null);
        Assert.Equal("https://dev.example", target.CanonicalApiBaseUrl);
    }

    [Fact]
    public void Target_only_accepts_its_own_canonical_endpoint()
    {
        var target = new HelpdeskMcpTarget("prod", new Uri("https://prod.example/api"));

        Assert.True(target.MatchesApiBaseUrl("https://prod.example/api/"));
        Assert.False(target.MatchesApiBaseUrl("https://dev.example/api"));
    }

    [Fact]
    public void Named_instance_and_private_http_backend_remain_supported_without_a_file()
    {
        var target = HelpdeskMcpTarget.Resolve(Configuration("http://api:8222/"), null, "north-west", "http://api:8222");
        Assert.Equal("north-west", target.Instance);
        Assert.Equal("http://api:8222", target.CanonicalApiBaseUrl);
    }

    [Fact]
    public void Shell_safe_expected_endpoint_alias_beats_legacy_instance_alias()
    {
        using var environment = new EnvironmentVariables(
            (HelpdeskMcpTarget.InstanceEnvironmentVariable, "north-west"),
            ("RATELDESK_MCP_NORTH-WEST_API_BASE_URL", "https://old.example"),
            ("RATELDESK_MCP_EXPECTED_API_BASE_URL", "http://api:8222"));
        var target = HelpdeskMcpTarget.Resolve(Configuration("http://api:8222"), null);
        Assert.Equal("north-west", target.Instance);
    }

    private static AgentClientConfiguration Configuration(string apiBaseUrl)
        => new(apiBaseUrl, "https://auth.example/token", "client", "agent", "secret", "scope", null);

    private sealed class EnvironmentVariables : IDisposable
    {
        private readonly Dictionary<string, string?> _previous;

        public EnvironmentVariables(params (string Name, string? Value)[] values)
        {
            _previous = values.ToDictionary(value => value.Name, value => Environment.GetEnvironmentVariable(value.Name));
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
