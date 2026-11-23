using System.Security.Cryptography;
using System.Text;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Security;

public sealed class PublicTicketLinkSigner(
    IOptions<StorageOptions> options,
    ILogger<PublicTicketLinkSigner> logger) : IPublicTicketLinkSigner
{
    private readonly ILogger<PublicTicketLinkSigner> _logger = logger;
    private readonly byte[] _secret = Encoding.UTF8.GetBytes(
        !string.IsNullOrWhiteSpace(options.Value.ImageSigningSecret)
            ? options.Value.ImageSigningSecret
            : throw new InvalidOperationException("Configuration value 'StorageOptions:ImageSigningSecret' is required."));

    public string GenerateToken(string trackingId, string email, DateTimeOffset expires)
    {
        var expiryUnix = expires.ToUnixTimeSeconds();
        var payload = $"{trackingId}|{email}|{expiryUnix}";
        var signature = ComputeSignature(payload);
        var token = $"{payload}|{signature}";
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
    }

    public bool ValidateToken(string token, string trackingId, string email) =>
        TryReadExpiry(token, trackingId, email, out var expiry) &&
        expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public bool TryReadExpiry(string token, string trackingId, string email, out long expiry)
    {
        expiry = 0;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024)
            return false;
        string decoded;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            if (!string.Equals(WebEncoders.Base64UrlEncode(bytes), token, StringComparison.Ordinal))
                return false;
            decoded = Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = decoded.Split('|');
        if (parts.Length != 4 || parts[0] != trackingId || parts[1] != email ||
            !long.TryParse(parts[2], out expiry) || expiry <= 0)
            return false;

        var expected = ComputeSignature($"{parts[0]}|{parts[1]}|{parts[2]}");
        if (FixedTimeEquals(parts[3], expected))
            return true;
        _logger.LogDebug("Legacy public-link signature was invalid.");
        return false;
    }

    private string ComputeSignature(string payload)
    {
        using var hmac = new HMACSHA256(_secret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return WebEncoders.Base64UrlEncode(hash);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
