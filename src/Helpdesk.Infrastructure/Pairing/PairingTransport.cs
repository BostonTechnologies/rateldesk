using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Shared.Pairing;

namespace Helpdesk.Infrastructure.Pairing;

public sealed class PairingFailure(string code, string message, int status = 400, PairingReadinessDiagnostic? diagnostic = null) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public PairingReadinessDiagnostic? Diagnostic { get; } = PairingReadinessDiagnostics.IsValid(diagnostic) ? diagnostic : null;
}

public sealed class PairingTransport(IHttpClientFactory clients)
{
    public const string ClientName = "SystemPairing";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Origin(string address)
    {
        address = address?.Trim() ?? "";
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) throw new PairingFailure("invalid_address", "Enter a valid HTTP or HTTPS address and optional port.");
        try { IntegrationEndpointPolicy.Validate(uri, "Address", allowPrivateHttp: true); }
        catch (ArgumentException ex) { throw new PairingFailure("blocked_address", ex.Message); }
        if (uri.AbsolutePath.Trim('/') != "") throw new PairingFailure("invalid_address", "Enter the server address without a path.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
    public async Task<PairingMetadata> DiscoverAsync(string address, CancellationToken ct)
    {
        var origin = Origin(address);
        var nonce = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var proof = await SendAsync<PairingMetadataProof>(origin, "/metadata", HttpMethod.Get, null, null, null, ct, nonce);
        VerifyProof(proof, nonce);
        var metadata = proof.Metadata;
        ValidateMetadata(metadata);
        if (origin != Origin(metadata.ApiOrigin))
        {
            var canonical = await SendAsync<PairingMetadataProof>(metadata.ApiOrigin, "/metadata", HttpMethod.Get, null, null, null, ct, nonce);
            VerifyProof(canonical, nonce);
            if (canonical.Metadata != metadata) throw new PairingFailure("identity_mismatch", "The advertised API address does not prove ownership by the entered installation.", 502);
        }
        if (origin != Origin(metadata.ApiOrigin) && origin != Origin(metadata.WebOrigin))
        {
            // Alias is accepted only through the verified immutable metadata above.
            if (metadata.InstallationId.Length == 0) throw new PairingFailure("identity_mismatch", "The address could not be verified.", 502);
        }
        return metadata;
    }
    public static byte[] ProofPayload(PairingMetadata metadata, string nonce) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, Json) + ":" + nonce);
    private static void VerifyProof(PairingMetadataProof proof, string nonce)
    {
        try
        {
            if (proof is null || proof.Metadata is null || proof.Signature is null || proof.Metadata.SigningPublicKey is null || proof.Nonce != nonce || proof.Signature.Length > 2048 || proof.Metadata.SigningPublicKey.Length > 4096) throw new CryptographicException();
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(proof.Metadata.SigningPublicKey), out _);
            if (!rsa.VerifyData(ProofPayload(proof.Metadata, nonce), Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new PairingFailure("identity_mismatch", "The peer could not prove ownership of its installation metadata.", 502); }
    }
    public static void ValidateMetadata(PairingMetadata peer)
    {
        if (peer is null || peer.Contract != PairingContract.Version || peer.Product is not ("netratel" or "rateldesk") ||
            !Guid.TryParse(peer.InstallationId, out var installation) || installation == Guid.Empty || peer.InstallationId != installation.ToString("D") || peer.Name is null || peer.Name.Length > 256)
            throw new PairingFailure("incompatible_peer", "The address must run a compatible NetRatel or RatelDesk beta.", 502);
        _ = Origin(peer.WebOrigin); _ = Origin(peer.ApiOrigin);
        if (peer.Product == "rateldesk" && peer.ProducerInstanceId is not null || peer.Product == "netratel" &&
            (!Guid.TryParse(peer.ProducerInstanceId, out var producer) || producer == Guid.Empty || peer.ProducerInstanceId != producer.ToString("D")))
            throw new PairingFailure("invalid_producer", "The peer did not provide its persistent Flow producer identity.", 502);
        if (peer.Product == "netratel" && peer.ReceiverInstanceId is not null || peer.Product == "rateldesk" &&
            (!Guid.TryParse(peer.ReceiverInstanceId, out var receiver) || receiver == Guid.Empty || peer.ReceiverInstanceId != receiver.ToString("D")))
            throw new PairingFailure("invalid_receiver", "The peer did not provide its persistent incident receiver identity.", 502);
    }
    public async Task<T> SendAsync<T>(string origin, string route, HttpMethod method, object? body, string? secret, string? peerId, CancellationToken ct, string? nonce = null, string? callerHash = null)
    {
        var endpoint = new Uri(Origin(origin) + PairingContract.Root + route);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, endpoint);
        if (nonce is not null) request.Headers.Add("X-Pairing-Nonce", nonce);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        request.Options.Set(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, true);
        if (secret is not null) { request.Headers.Authorization = new AuthenticationHeaderValue("Pairing", secret); request.Headers.Add(PairingContract.PeerHeader, peerId); request.Headers.Add("X-Pairing-Caller", callerHash); }
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[65537]; var count = 0;
            while (count < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token); if (read == 0) break; count += read; }
            if (count > 65536) throw new PairingFailure("invalid_response", "The peer response exceeded the supported size.", 502);
            if (!response.IsSuccessStatusCode)
            {
                string? message = null; string? code = null; PairingReadinessDiagnostic? diagnostic = null; var diagnosticProvided = false;
                try
                {
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, count));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String) message = text.GetString();
                        if (doc.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
                        if (doc.RootElement.TryGetProperty("diagnostic", out var evidence) && evidence.ValueKind != JsonValueKind.Null)
                        {
                            diagnosticProvided = true;
                            if (evidence.ValueKind == JsonValueKind.Object) diagnostic = evidence.Deserialize<PairingReadinessDiagnostic>(Json);
                        }
                    }
                }
                catch (JsonException) { }
                var sensitive = new List<string?> { secret };
                if (body is PairingExchangeRequest exchange) sensitive.AddRange([exchange.InboundSecret, exchange.Code, exchange.Code.Replace("-", "", StringComparison.Ordinal)]);
                if (body is PairingSaveRequest save) sensitive.Add(save.Credential?.ClientSecret);
                var pairingCode = (body as PairingExchangeRequest)?.Code;
                diagnostic = SafeDiagnostic(diagnostic, sensitive, pairingCode);
                code = PairingCodeRedaction.Apply(IntegrationErrorSafety.ProviderMessage(code, 65536, sensitive.ToArray()), pairingCode)!;
                if (code.Length is < 1 or > 80 || code.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('_' or '-'))) code = "peer_rejected";
                message = PairingCodeRedaction.Apply(IntegrationErrorSafety.ProviderMessage(message is { Length: <= 512 } ? message : $"The peer rejected the request (HTTP {(int)response.StatusCode}).", 65536, sensitive.ToArray()), pairingCode)!;
                if (message.Length > 512) message = message[..512];
                var safeMessage = diagnostic is not null ? PairingReadinessDiagnostics.Describe(diagnostic)
                    : diagnosticProvided ? "Receiver readiness could not be verified. Check the server reference, then retry this connection." : message;
                throw new PairingFailure(code, safeMessage, (int)response.StatusCode, diagnostic);
            }
            if (typeof(T) == typeof(bool)) return (T)(object)true;
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, count), Json) ?? throw new PairingFailure("invalid_response", "The peer returned an empty response.", 502);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PairingFailure("peer_timeout", "The peer timed out. Retry this same connection; a completed exchange is retained.", 504); }
        catch (HttpRequestException) { throw new PairingFailure("peer_unavailable", "The peer could not be reached. Check its address, network access and trusted TLS certificate, then retry this connection.", 502); }
        catch (IOException) { throw new PairingFailure("peer_response_lost", "The peer response was interrupted. Retry this same connection; completed operations are retained.", 502); }
        catch (JsonException) { throw new PairingFailure("invalid_response", "The peer returned an incompatible response.", 502); }
    }

    internal static PairingReadinessDiagnostic? SafeDiagnostic(PairingReadinessDiagnostic? diagnostic, IReadOnlyList<string?> sensitive, string? pairingCode = null)
    {
        if (!PairingReadinessDiagnostics.IsValid(diagnostic)) return null;
        foreach (var field in new[] { diagnostic!.Stage, diagnostic.Code, diagnostic.Reference })
            if (PairingCodeRedaction.Apply(IntegrationErrorSafety.ProviderMessage(field, 65536, sensitive.ToArray()), pairingCode) != field) return null;
        return diagnostic;
    }
}
