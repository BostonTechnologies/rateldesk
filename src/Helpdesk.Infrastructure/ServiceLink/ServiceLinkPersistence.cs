using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed class ServiceLinkAttempt
{
    public string AttemptId { get; set; } = "";
    public string Role { get; set; } = "";
    public string LocalTenantId { get; set; } = "";
    public string LocalActorId { get; set; } = "";
    public string PeerInstanceId { get; set; } = "";
    public string? PeerTenantId { get; set; }
    public string? LinkId { get; set; }
    public string? ActiveRelationshipKey { get; set; }
    public long LinkRevision { get; set; } = 1;
    public string LifecycleState { get; set; } = "awaiting_approval";
    public string Decision { get; set; } = "undecided";
    public string? CommitId { get; set; }
    public string? AbortId { get; set; }
    public string? RevocationId { get; set; }
    public string DescriptorJson { get; set; } = "";
    public string DescriptorHash { get; set; } = "";
    public string? GrantSummaryJson { get; set; }
    public string? GrantHash { get; set; }
    public string? ConsentId { get; set; }
    public string? ProtectedVerifier { get; set; }
    public string? ProtectedBrowserState { get; set; }
    public string? SessionBindingHash { get; set; }
    public string? PairingCodeHash { get; set; }
    public string? ProtectedPairingCode { get; set; }
    public string? ProtectedInboundEscrow { get; set; }
    public string? ProtectedExchangeResponse { get; set; }
    public string? ExchangeFingerprint { get; set; }
    public string? ExchangeResponseHash { get; set; }
    public bool ExchangeDispatched { get; set; }
    public Guid? InboundPrincipalId { get; set; }
    public string? ProtectedOutboundCredential { get; set; }
    public int? OutboundProfileRevision { get; set; }
    public bool PeerPreparedAcknowledged { get; set; }
    public bool LocalPreparedAcknowledged { get; set; }
    public bool LocalInboundActive { get; set; }
    public bool LocalBusinessSenderEnabled { get; set; }
    public bool PeerActiveAcknowledged { get; set; }
    public bool LocalActiveAcknowledged { get; set; }
    public bool PeerRevocationAcknowledged { get; set; }
    public string? InitiatorVerificationReceiptId { get; set; }
    public string? ResponderVerificationReceiptId { get; set; }
    public string? LastErrorCode { get; set; }
    public long ExpiresAtUnixSeconds { get; set; }
    public long? TerminalControlExpiresAtUnixSeconds { get; set; }
    public long CreatedAtUnixSeconds { get; set; }
    public long UpdatedAtUnixSeconds { get; set; }
    public long NextWorkAtUnixSeconds { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class ServiceLinkOperation
{
    public string LinkId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string RequestFingerprint { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public string? ProtectedRequestJson { get; set; }
    public bool Outbound { get; set; }
    public bool Completed { get; set; }
    public long CreatedAtUnixSeconds { get; set; }
}

public sealed class ServiceLinkVerificationReceipt
{
    public string VerificationReceiptId { get; set; } = "";
    public string LinkId { get; set; } = "";
    public string AttemptId { get; set; } = "";
    public string GrantHash { get; set; } = "";
    public Guid ServicePrincipalId { get; set; }
    public string DirectionId { get; set; } = "";
    public long CredentialRevision { get; set; }
    public string? RotationId { get; set; }
    public long VerifiedAtUnixSeconds { get; set; }
}

public sealed class ServiceLinkRotation
{
    public string RotationId { get; set; } = "";
    public string LinkId { get; set; } = "";
    public string DirectionId { get; set; } = "";
    public bool IsIssuer { get; set; }
    public string RotationState { get; set; } = "requested";
    public string? ActiveRotationKey { get; set; }
    public long ExpectedCurrentCredentialRevision { get; set; }
    public long? SuccessorCredentialRevision { get; set; }
    public string? ProtectedOffer { get; set; }
    public string? ProtectedCandidate { get; set; }
    public long? OfferExpiresAtUnixSeconds { get; set; }
    public string? SuccessorVerificationReceiptId { get; set; }
    public string? ActivateDecisionId { get; set; }
    public long? CallerSwitchRevision { get; set; }
    public long? PredecessorRetireAtUnixSeconds { get; set; }
    public string? LastErrorCode { get; set; }
    public long CreatedAtUnixSeconds { get; set; }
    public long Revision { get; set; } = 1;
}

public static class ServiceLinkPersistence
{
    public static void ConfigureServiceLinkModel(this ModelBuilder model)
    {
        model.Entity<ServiceLinkAttempt>(e =>
        {
            e.ToTable("ServiceLinkAttempts"); e.HasKey(x => x.AttemptId);
            e.Property(x => x.AttemptId).HasMaxLength(128);
            e.HasIndex(x => x.LinkId).IsUnique();
            e.HasIndex(x => x.ActiveRelationshipKey).IsUnique();
            e.HasIndex(x => new { x.PeerInstanceId, x.LocalTenantId, x.PeerTenantId, x.LinkRevision, x.Role });
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.LifecycleState, x.NextWorkAtUnixSeconds });
        });
        model.Entity<ServiceLinkOperation>(e =>
        {
            e.ToTable("ServiceLinkOperations"); e.HasKey(x => new { x.LinkId, x.OperationId });
            e.Property(x => x.LinkId).HasMaxLength(128); e.Property(x => x.OperationId).HasMaxLength(128);
            e.HasIndex(x => new { x.Outbound, x.Completed });
        });
        model.Entity<ServiceLinkVerificationReceipt>(e =>
        {
            e.ToTable("ServiceLinkVerificationReceipts"); e.HasKey(x => x.VerificationReceiptId);
            e.HasIndex(x => new { x.LinkId, x.ServicePrincipalId, x.CredentialRevision, x.RotationId });
        });
        model.Entity<ServiceLinkRotation>(e =>
        {
            e.ToTable("ServiceLinkRotations"); e.HasKey(x => x.RotationId);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.LinkId, x.DirectionId, x.ExpectedCurrentCredentialRevision });
            e.HasIndex(x => x.ActiveRotationKey).IsUnique();
        });
    }
}
