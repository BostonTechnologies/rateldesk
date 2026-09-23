using System.Text.Json;
using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Services.Email;

public sealed record IngressEmailEffect(string[] Recipients, string Subject, string Html,
    string[] Cc, string? TicketId, EmailAttachmentData[] Attachments,
    string? FromName, string? ReplyTo, bool SuppressTimeline,
    string? SupportDeliveryId, Guid? TimelineDeliveryId)
{
    public Guid? MailboxId { get; init; }
    public string? OrganizationId { get; init; }
}

public sealed record IngressCapturedEffect(string Key, MailboxEffectKind Kind, string Payload);

public interface IIngressEffectContext
{
    bool IsActive { get; }
    Guid ReceiptId { get; }
    string? SupportDeliveryId { get; set; }
    Guid? TimelineDeliveryId { get; set; }
    IReadOnlyList<IngressCapturedEffect> Effects { get; }
    IDisposable Begin(Guid receiptId);
    int NextCreationOrdinal();
    void Capture<T>(MailboxEffectKind kind, T payload);
    void Validate();
}

/// <summary>Scoped, sequential business-operation buffer, flushed durably before receipt commit.</summary>
public sealed class IngressEffectContext : IIngressEffectContext
{
    private readonly List<IngressCapturedEffect> effects = [];
    private Exception? captureFailure;
    private int totalPayloadLength;
    private int creationOrdinal;
    public bool IsActive { get; private set; }
    public Guid ReceiptId { get; private set; }
    public string? SupportDeliveryId { get; set; }
    public Guid? TimelineDeliveryId { get; set; }
    public IReadOnlyList<IngressCapturedEffect> Effects => effects;

    public IDisposable Begin(Guid receiptId)
    {
        if (IsActive || receiptId == Guid.Empty)
            throw new InvalidOperationException("Ingress effects require one nonempty receipt per scope.");
        effects.Clear();
        captureFailure = null;
        totalPayloadLength = 0;
        creationOrdinal = 0;
        ReceiptId = receiptId;
        IsActive = true;
        return new ActiveScope(this);
    }

    // Request handlers are transient. Keep creation identity in the shared receipt
    // scope so sequential commands remain distinct and transaction retries repeat it.
    public int NextCreationOrdinal()
    {
        if (!IsActive) throw new InvalidOperationException("No active ingress effect scope.");
        return checked(creationOrdinal++);
    }

    public void Capture<T>(MailboxEffectKind kind, T payload)
    {
        if (!IsActive)
            throw new InvalidOperationException("No active ingress effect scope.");
        try
        {
            var serialized = JsonSerializer.Serialize(payload);
            if (effects.Count >= 256 || serialized.Length > 16 * 1024 * 1024 - totalPayloadLength)
                throw new InvalidOperationException("Ingress effect payload limit exceeded.");
            totalPayloadLength += serialized.Length;
            effects.Add(new IngressCapturedEffect($"{effects.Count:D4}:{kind}", kind, serialized));
        }
        catch (Exception error)
        {
            // Some existing notification handlers catch exceptions. Keep the transaction
            // uncommittable even if they swallow a serialization or resource-limit failure.
            captureFailure = error;
            throw;
        }
    }

    public void Validate()
    {
        if (!IsActive || captureFailure is not null)
            throw new InvalidOperationException("Ingress effects could not be durably captured.", captureFailure);
    }

    private sealed class ActiveScope(IngressEffectContext context) : IDisposable
    {
        public void Dispose()
        {
            context.IsActive = false;
            context.SupportDeliveryId = null;
            context.TimelineDeliveryId = null;
            context.effects.Clear();
        }
    }
}
