using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

public sealed class OpenAiChatProfile
{
    public Guid Id { get; set; }
    public int Revision { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? LastVerifiedAtUtc { get; set; }
    public string? LastVerification { get; set; }
}

public sealed class OpenAiChatProfileRevision
{
    public Guid ProfileId { get; set; }
    public int Revision { get; set; }
    public string SettingsJson { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public string? SkillBundleHash { get; set; }
}

public sealed class ChatProviderPreference
{
    public int Id { get; set; }
    public Guid ProfileId { get; set; }
}

public enum OpenAiDispatchState { Queued, Dispatching, Admitted, Rejected, Unknown, Completed }

public sealed class OpenAiChatDispatch
{
    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    public OpenAiDispatchState State { get; set; }
    public string ActorId { get; set; } = "";
    public string? TurnId { get; set; }
    public DateTimeOffset AdmittedAtUtc { get; set; }
    public DateTimeOffset? DispatchedAtUtc { get; set; }
    public DateTimeOffset? CancelRequestedAtUtc { get; set; }
    public DateTimeOffset? CancelSentAtUtc { get; set; }
    public DateTimeOffset? NextObservationAtUtc { get; set; }
}

public sealed class OpenAiChatSessionGrant
{
    public Guid ConversationId { get; set; }
    public Guid CredentialId { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? CleanupRequestedAtUtc { get; set; }
    public DateTimeOffset? CleanupNextAttemptAtUtc { get; set; }
    public DateTimeOffset? RemoteDeletedAtUtc { get; set; }
}

public sealed class OpenAiChatEventReceipt
{
    public Guid ConversationId { get; set; }
    public string RemoteKey { get; set; } = "";
}

public enum OpenAiDecisionState { Pending, Decided, Executing, AnswerReady, Answered, Closed }

public sealed class OpenAiChatDecision
{
    public Guid ConversationId { get; set; }
    public string CallId { get; set; } = "";
    public string RemoteActionId { get; set; } = "";
    public string TurnId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string ArgumentHash { get; set; } = "";
    public string Operation { get; set; } = "";
    public int Kind { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public OpenAiDecisionState State { get; set; }
    public bool? Approved { get; set; }
    public string? Result { get; set; }
}

public sealed class OpenAiChatArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string RemoteId { get; set; } = "";
    public string TurnId { get; set; } = "";
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public byte[] Contents { get; set; } = [];
}

internal static class OpenAiChatModel
{
    internal static void Configure(ModelBuilder builder)
    {
        builder.Entity<OpenAiChatProfile>().Property(row => row.Revision).IsConcurrencyToken();
        builder.Entity<OpenAiChatProfileRevision>().HasKey(row => new { row.ProfileId, row.Revision });
        builder.Entity<OpenAiChatProfileRevision>().HasOne<OpenAiChatProfile>().WithMany()
            .HasForeignKey(row => row.ProfileId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ChatProviderPreference>().Property(row => row.Id).ValueGeneratedNever();
        builder.Entity<OpenAiChatDispatch>().HasKey(row => new { row.ConversationId, row.MessageId });
        builder.Entity<OpenAiChatDispatch>().HasIndex(row => new { row.State, row.NextObservationAtUtc });
        builder.Entity<OpenAiChatDispatch>().HasOne<AiAssistantChatConversation>().WithMany()
            .HasForeignKey(row => row.ConversationId).OnDelete(DeleteBehavior.Restrict);

        builder.Entity<OpenAiChatSessionGrant>().HasKey(row => row.ConversationId);
        builder.Entity<OpenAiChatSessionGrant>().HasIndex(row => row.CredentialId).IsUnique();
        builder.Entity<OpenAiChatSessionGrant>().HasOne<AiAssistantChatConversation>().WithMany()
            .HasForeignKey(row => row.ConversationId).OnDelete(DeleteBehavior.Restrict);

        builder.Entity<OpenAiChatEventReceipt>().HasKey(row => new { row.ConversationId, row.RemoteKey });
        builder.Entity<OpenAiChatEventReceipt>().Property(row => row.RemoteKey).HasMaxLength(64);
        builder.Entity<OpenAiChatDecision>().HasKey(row => new { row.ConversationId, row.CallId });
        builder.Entity<OpenAiChatArtifact>().HasIndex(row => new { row.ConversationId, row.RemoteId }).IsUnique();
        builder.Entity<OpenAiChatArtifact>().HasOne<AiAssistantChatConversation>().WithMany()
            .HasForeignKey(row => row.ConversationId).OnDelete(DeleteBehavior.Restrict);
    }
}
