using System.Diagnostics;
using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Mcp;
using Helpdesk.Mcp.Configuration;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class HelpdeskMcpProcessTests
{
    [Fact]
    public async Task Missing_instance_fails_closed_with_stderr_only_diagnostic()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(HelpdeskMcpStdioHost).Assembly.Location);
        foreach (var name in new[]
                 {
                     HelpdeskMcpTarget.InstanceEnvironmentVariable,
                     "RATELDESK_MCP_DEV_API_BASE_URL",
                     "RATELDESK_MCP_PROD_API_BASE_URL",
                     "RATELDESK_API_BASE_URL"
                 })
            start.Environment.Remove(name);
        var configurationPath = Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-{Guid.NewGuid():N}.json");
        new AgentClientConfigurationStore(configurationPath).Save(StdioHost.Configuration("https://api.example"));
        start.Environment[HelpdeskMcpTarget.ConfigurationEnvironmentVariable] = configurationPath;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(78, process.ExitCode);
        Assert.Equal(string.Empty, await stdout);
        Assert.Contains("RATELDESK_MCP_INSTANCE must be a lowercase instance label containing letters, digits, and hyphens.", await stderr, StringComparison.Ordinal);
        File.Delete(configurationPath);
    }

    [Fact]
    public async Task Environment_only_stdio_starts_with_pure_jsonrpc_and_ignores_ambient_cli_identity()
    {
        var placeholder = Path.Combine(Path.GetTempPath(), $"helpdesk-env-only-{Guid.NewGuid():N}.json");
        var start = StdioHost.StartInfo("north-west", "http://api:8222", placeholder);
        start.Environment.Remove(HelpdeskMcpTarget.ConfigurationEnvironmentVariable);
        start.Environment["RATELDESK_MCP_API_BASE_URL"] = "http://api:8222";
        start.Environment["RATELDESK_MCP_CREDENTIAL_MODE"] = "integration";
        start.Environment["RATELDESK_MCP_INTEGRATION_CREDENTIAL"] = "rdk_test_only";
        start.Environment["RATELDESK_MCP_EXPECTED_API_BASE_URL"] = "http://api:8222";
        start.Environment["RATELDESK_API_BASE_URL"] = "https://wrong.example";
        start.Environment["RATELDESK_INTEGRATION_CREDENTIAL"] = "rdk_wrong_cli_identity";
        await using var host = new StdioHost(Process.Start(start)!, placeholder);
        await host.InitializeAsync();
        using var response = await host.CallAsync(2, "tools/call", new { name = "helpdesk_capabilities", arguments = new { operation = "get" } });
        var data = response.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
        Assert.Equal("north-west", data.GetProperty("instance").GetString());
        Assert.Equal("http://api:8222", data.GetProperty("apiBaseUrl").GetString());
        Assert.False(File.Exists(placeholder));
    }

    [Fact]
    public async Task Dev_and_prod_stdio_hosts_are_concurrently_isolated()
    {
        const string devApi = "https://dev-api.example";
        const string prodApi = "https://prod-api.example";
        await using var dev = StdioHost.Start("dev", devApi);
        await using var prod = StdioHost.Start("prod", prodApi);

        await Task.WhenAll(dev.InitializeAsync(), prod.InitializeAsync());
        using var devCapabilities = await dev.CallAsync(2, "tools/call", new { name = "helpdesk_capabilities", arguments = new { operation = "get" } });
        using var prodCapabilities = await prod.CallAsync(2, "tools/call", new { name = "helpdesk_capabilities", arguments = new { operation = "get" } });
        var devData = devCapabilities.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
        var prodData = prodCapabilities.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");

        Assert.Equal("dev", devData.GetProperty("instance").GetString());
        Assert.Equal(devApi, devData.GetProperty("apiBaseUrl").GetString());
        Assert.Equal("stdio", devData.GetProperty("transport").GetString());
        Assert.Equal("prod", prodData.GetProperty("instance").GetString());
        Assert.Equal(prodApi, prodData.GetProperty("apiBaseUrl").GetString());
        Assert.Equal("stdio", prodData.GetProperty("transport").GetString());
        Assert.NotEqual(File.ReadAllText(dev.ConfigurationPath), File.ReadAllText(prod.ConfigurationPath));
    }

    [Fact]
    public async Task Stdio_host_rejects_an_instance_with_a_mismatched_configured_endpoint()
    {
        var configurationPath = Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-invalid-{Guid.NewGuid():N}.json");
        try
        {
            new AgentClientConfigurationStore(configurationPath).Save(StdioHost.Configuration("https://wrong-api.example"));
            var start = StdioHost.StartInfo("dev", "https://dev-api.example", configurationPath);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(78, process.ExitCode);
            Assert.Equal(string.Empty, await stdout);
            Assert.Contains("requires the isolated MCP configuration apiBaseUrl to match RATELDESK_MCP_DEV_API_BASE_URL", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configurationPath);
        }
    }

    private sealed class StdioHost(Process process, string configurationPath) : IAsyncDisposable
    {
        private readonly Process _process = process;

        public string ConfigurationPath { get; } = configurationPath;

        public static StdioHost Start(string instance, string apiBaseUrl)
        {
            var configurationPath = Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-{instance}-{Guid.NewGuid():N}.json");
            new AgentClientConfigurationStore(configurationPath).Save(Configuration(apiBaseUrl));
            return new StdioHost(Process.Start(StartInfo(instance, apiBaseUrl, configurationPath))!, configurationPath);
        }

        public static AgentClientConfiguration Configuration(string apiBaseUrl)
            => new(apiBaseUrl, "https://auth.example/token", "helpdesk-mcp-test", "helpdesk-mcp-test", "not-a-real-secret", "openid profile email", null);

        public static ProcessStartInfo StartInfo(string instance, string apiBaseUrl, string configurationPath)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(HelpdeskMcpStdioHost).Assembly.Location);
            foreach (var name in new[]
                     {
                         "RATELDESK_MCP_CONFIG",
                         "RATELDESK_MCP_INSTANCE",
                         "RATELDESK_MCP_DEV_API_BASE_URL",
                         "RATELDESK_MCP_PROD_API_BASE_URL",
                         "RATELDESK_API_BASE_URL",
                         "RATELDESK_AUTHENTIK_TOKEN_URL",
                         "RATELDESK_AUTHENTIK_CLIENT_ID",
                         "RATELDESK_AUTHENTIK_USERNAME",
                         "RATELDESK_AUTHENTIK_APP_PASSWORD"
                     })
                start.Environment.Remove(name);
            start.Environment[HelpdeskMcpTarget.ConfigurationEnvironmentVariable] = configurationPath;
            start.Environment[HelpdeskMcpTarget.InstanceEnvironmentVariable] = instance;
            start.Environment[$"RATELDESK_MCP_{instance.ToUpperInvariant()}_API_BASE_URL"] = apiBaseUrl;
            return start;
        }

        public async Task InitializeAsync()
        {
            using var initialize = await CallAsync(1, "initialize", new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "helpdesk-tests", version = "1.0" }
            });
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));
            await _process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}");
            await _process.StandardInput.FlushAsync();
        }

        public async Task<JsonDocument> CallAsync(int id, string method, object parameters)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(string.IsNullOrWhiteSpace(line), $"The {ConfigurationPath} stdio host did not respond to '{method}'.");
            return JsonDocument.Parse(line!);
        }

        public async ValueTask DisposeAsync()
        {
            _process.StandardInput.Close();
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            _process.Dispose();
            File.Delete(ConfigurationPath);
        }
    }
}
