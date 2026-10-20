using Helpdesk.API.Ops;
using Microsoft.Extensions.Logging;
using Xunit;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Tests.Api;

public sealed class AiAgentOpsLogBufferTests
{
    [Fact]
    public void Service_defaults_forward_real_transport_warning_to_searchable_redacted_ops_buffer()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Observability:ConsoleLogsEnabled"] = "false" });
        builder.ConfigureOpenTelemetry();
        builder.Services.AddSingleton<AiAgentOpsLogBuffer>();
        builder.Services.AddSingleton<ILoggerProvider, AiAgentOpsLoggerProvider>();
        using var host = builder.Build();
        using var activity = new Activity("native-transport-regression").Start();
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk.NativeTransport")
            .LogWarning("Transport failed for conversation {ConversationId}; token=synthetic-secret", "conversation-synthetic");
        var buffer = host.Services.GetRequiredService<AiAgentOpsLogBuffer>();
        var record = Assert.Single(buffer.Query(null, "Warning", "conversation-synthetic", activity.TraceId.ToString(), 10).Items);
        Assert.DoesNotContain("synthetic-secret", record.Message);
        Assert.Equal(activity.TraceId.ToString(), record.TraceId);
        Assert.Empty(buffer.Query(null, null, "conversation-synthetic", "different-trace", 10).Items);
    }

    [Fact]
    public void Query_Redacts_Common_Secret_Patterns()
    {
        var buffer = new AiAgentOpsLogBuffer();

        buffer.Add(
            LogLevel.Warning,
            "Helpdesk.Tests",
            new EventId(7, "SecretTest"),
            "Authorization: Bearer abc.def.ghi password=secret client_secret=hidden Cookie: session=value",
            new InvalidOperationException("Host=db;Username=app;Password=dbsecret;Database=helpdesk"));

        var result = buffer.Query(null, null, null, null, 10);
        var item = Assert.Single(result.Items);

        Assert.True(result.Redacted);
        Assert.DoesNotContain("abc.def.ghi", item.Message);
        Assert.DoesNotContain("secret", item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden", item.Message);
        Assert.DoesNotContain("session=value", item.Message);
        Assert.DoesNotContain("dbsecret", item.ExceptionMessage);
    }

    [Fact]
    public void Query_Enforces_Hard_Limit_And_Since_Filter()
    {
        var buffer = new AiAgentOpsLogBuffer();
        for (var i = 0; i < 600; i++)
        {
            buffer.Add(LogLevel.Information, "Helpdesk.Tests", new EventId(0), $"message {i}", null);
        }

        var result = buffer.Query(500, null, null, null, 999);

        Assert.Equal(100, result.Items.Count);
        Assert.All(result.Items, item => Assert.True(item.Id > 500));
    }
}
