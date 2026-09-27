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
            ProfileFingerprint: "profile-b",
            Revision: 1);

        state.Publish(snapshot);

        Assert.Equal(snapshot, state.Current);
        Assert.Equal("https://provider-b.example.test/hub/session", state.Current.Endpoint);
        Assert.Equal("token-b", state.Current.DeviceToken);
        Assert.NotEqual("https://provider-a.example.test/hub/session", state.Current.Endpoint);
    }

    [Fact]
    public void Older_same_source_revision_cannot_replace_the_applied_snapshot()
    {
        var state = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
        var newer = Snapshot("profile-new", 2, "database", "database", "https://provider-new.example.test/hub/session");
        var older = Snapshot("profile-old", 1, "database", "database", "https://provider-old.example.test/hub/session");

        Assert.True(state.TryPublish(newer));
        Assert.False(state.CanApply(older));
        Assert.False(state.TryPublish(older));
        Assert.Same(newer, state.Current);
    }

    [Fact]
    public void Same_revision_profile_conflict_is_rejected_but_source_transition_is_explicit()
    {
        var state = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
        var current = Snapshot("profile-a", 3, "database", "database", "https://provider-a.example.test/hub/session");
        var conflicting = Snapshot("profile-b", 3, "database", "database", "https://provider-b.example.test/hub/session");
        var deployment = Snapshot("profile-deployment", 1, "deployment", "deployment", "https://provider-deployment.example.test/hub/session");

        Assert.True(state.TryPublish(current));
        Assert.False(state.TryPublish(conflicting));
        Assert.True(state.TryPublish(deployment));
        Assert.Same(deployment, state.Current);
    }

    private static AiAssistantChatRuntimeSnapshot Snapshot(
        string fingerprint,
        int revision,
        string source,
        string sourceKey,
        string endpoint)
        => new(
            Enabled: false,
            Instance: "dev",
            Endpoint: endpoint,
            DeviceToken: string.Empty,
            AllowPrivateHttp: false,
            IdleMinutes: 15,
            ConnectionCapacity: 25,
            TurnInactivityTimeout: TimeSpan.FromMinutes(5),
            ActivityHeartbeatInterval: TimeSpan.FromSeconds(15),
            ProfileFingerprint: fingerprint,
            Revision: revision,
            Source: source,
            ManagedByDeployment: source == "deployment",
            SourceKey: sourceKey,
            CanAdoptLegacySessions: false);
}
