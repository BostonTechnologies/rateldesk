using System.Text.Json;

namespace Helpdesk.AgentClient;

/// <summary>
/// Owns one explicitly approved temporary verifier. Only its bounded cancellation capability
/// is saved; the credential stays in memory and cannot become a personal or host profile.
/// </summary>
public sealed class SetupVerificationSession : IAsyncDisposable
{
    private readonly string _origin;
    private readonly string _cleanupPath;
    private readonly IntegrationCredentialPairingClient _client;
    private CleanupRecord? _cleanup;
    private IDisposable? _lease;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public PairingSession? Pairing { get; private set; }
    public PairedIntegrationCredential? Credential { get; private set; }
    public bool CleanupPending => _cleanup is not null;

    public SetupVerificationSession(string origin, string cleanupPath, HttpClient? client = null)
    {
        _origin = IntegrationCredentialPairingClient.ValidateOrigin(origin).GetLeftPart(UriPartial.Authority);
        _cleanupPath = Path.GetFullPath(cleanupPath);
        _client = new(_origin, client);
    }

    public async Task<bool> RecoverAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lease ??= SecureConfigurationFiles.AcquireOperationLease(_cleanupPath, ct);
        if (_cleanup is null && File.Exists(_cleanupPath))
        {
            try { _cleanup = JsonSerializer.Deserialize<CleanupRecord>(SecureConfigurationFiles.ReadText(_cleanupPath, 8192, requirePrivate: true), JsonOptions); }
            catch (JsonException) { throw new AgentClientValidationException("The setup verification cleanup record is invalid."); }
            if (_cleanup is null || _cleanup.Origin != _origin || _cleanup.DeviceCode is not { Length: 64 } ||
                !_cleanup.DeviceCode.All(char.IsAsciiHexDigit) || _cleanup.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(15))
                throw new AgentClientValidationException("The setup verification cleanup record does not match this instance.");
        }
        return await CleanupAsync(ct);
    }

    public async Task<PairingSession> StartAsync(CancellationToken ct = default)
    {
        if (!await RecoverAsync(ct)) throw new AgentClientValidationException("Temporary verification cleanup is pending. Retry cleanup before requesting another verifier.");
        var capability = await _client.DiscoverAsync(ct);
        if (capability?.PairingSupported != true || !capability.SetupVerificationSupported)
            throw new AgentClientValidationException("This instance does not support bounded setup verification. Update the instance before verifying host setup.");
        Pairing = await _client.StartAsync(new("Temporary RatelDesk setup verification", "mcp", _origin + "/mcp") { GrantKind = "setup-verification" }, ct);
        _cleanup = new(_origin, Pairing.DeviceCode, Pairing.ExpiresAtUtc.AddMinutes(5));
        try { SaveCleanup(); }
        catch { await CleanupAsync(CancellationToken.None); throw; }
        return Pairing;
    }

    public async Task<PairingPollResult> PollAsync(CancellationToken ct = default)
    {
        if (Pairing is null || _cleanup is null) throw new AgentClientValidationException("Start a temporary verification approval first.");
        try
        {
            var result = await _client.PollAsync(Pairing.DeviceCode, ct);
            if (result.Credential is { } credential)
            {
                if (credential.GrantKind != "setup-verification" || credential.Purpose != "mcp" || credential.Permissions.Length != 0 ||
                    credential.McpResourceUri != _origin + "/mcp" || credential.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    throw new AgentClientValidationException("The approved identity is not a narrow temporary verifier.");
                Credential = credential;
                _cleanup = _cleanup with { ExpiresAtUtc = credential.ExpiresAtUtc };
                SaveCleanup();
            }
            if (result.Status is "denied" or "expired" or "cancelled") await CleanupAsync(CancellationToken.None);
            return result;
        }
        catch { await CleanupAsync(CancellationToken.None); throw; }
    }

    public async Task<T> RunAsync<T>(Func<PairedIntegrationCredential, CancellationToken, Task<T>> verify, CancellationToken ct = default)
    {
        if (Credential is null) throw new AgentClientValidationException("Wait for the explicit temporary verification approval first.");
        try { ct.ThrowIfCancellationRequested(); return await verify(Credential, ct); }
        finally { await CleanupAsync(CancellationToken.None); }
    }

    /// <returns>False when remote cleanup is unconfirmed; the private retry record remains.</returns>
    public async Task<bool> CleanupAsync(CancellationToken ct = default)
    {
        Credential = null;
        Pairing = null;
        if (_cleanup is null) return true;
        if (_cleanup.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try { await _client.CancelAsync(_cleanup.DeviceCode, deadline.Token); }
            catch (Exception error) when (error is AgentClientValidationException or HttpRequestException or OperationCanceledException or IOException)
            { SaveCleanup(); return false; }
        }
        if (File.Exists(_cleanupPath)) SecureConfigurationFiles.DeleteAtomic(_cleanupPath);
        _cleanup = null;
        return true;
    }

    private void SaveCleanup() => SecureConfigurationFiles.WriteAtomic(_cleanupPath, JsonSerializer.Serialize(_cleanup, JsonOptions));
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await CleanupAsync(); }
        finally { _disposed = true; _lease?.Dispose(); _client.Dispose(); }
    }
    private sealed record CleanupRecord(string Origin, string DeviceCode, DateTimeOffset ExpiresAtUtc)
    { public override string ToString() => "SetupVerificationCleanup { capability = [REDACTED] }"; }
}
