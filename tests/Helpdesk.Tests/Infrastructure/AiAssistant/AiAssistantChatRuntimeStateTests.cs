using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class AiAssistantChatRuntimeStateTests
{
    [Fact]
    public void Publish_swaps_an_immutable_provider_snapshot_as_one_value()
    {
        var options = Options.Create(new AiAssistantChatOptions
        {
            Enabled = true,
            Instance = "dev",
            Endpoint = "https://provider-a.example.test/hub/session",
            DeviceToken = "token-a"
        });
        var state = new AiAssistantChatRuntimeState(options);
        var snapshot = new AiAssistantChatRuntimeSnapshot(
            Enabled: true,
            Instance: "dev",
            Endpoint: "https://provider-b.example.test/hub/session",
            DeviceToken: "token-b",
            AllowPrivateHttp: false,
            IdleMinutes: 15,
            ConnectionCapacity: 25,
            TurnInactivityTimeout: TimeSpan.FromMinutes(5),
            ActivityHeartbeatInterval: TimeSpan.FromSeconds(15),
            ProfileFingerprint: "profile-b");

        state.Publish(snapshot);

        Assert.Equal(snapshot, state.Current);
        Assert.Equal("https://provider-b.example.test/hub/session", state.Current.Endpoint);
        Assert.Equal("token-b", state.Current.DeviceToken);
        Assert.NotEqual("https://provider-a.example.test/hub/session", state.Current.Endpoint);
    }
}
