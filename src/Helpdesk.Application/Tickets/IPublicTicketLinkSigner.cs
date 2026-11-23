namespace Helpdesk.Application.Tickets;

public interface IPublicTicketLinkSigner
{
    string GenerateToken(string trackingId, string email, DateTimeOffset expires);

    bool TryReadExpiry(string token, string trackingId, string email, out long expiry);

    bool ValidateToken(string token, string trackingId, string email);
}
