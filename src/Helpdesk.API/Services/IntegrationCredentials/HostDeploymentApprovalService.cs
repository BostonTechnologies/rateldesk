using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.API.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services.IntegrationCredentials;

/// <summary>Uses the existing pairing checkpoint store and interactive authority. It never issues tokens.</summary>
public sealed class HostDeploymentApprovalService(RatelDeskIdentityDbContext identity,
    IntegrationCredentialPairingService pairing, IIntegrationCredentialOwnerResolver owners,
    ICurrentUserAccessService access, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("RatelDesk.HostDeploymentApproval.v1");
    private DateTimeOffset Now => clock.GetUtcNow();
    private sealed record Checkpoint(HostDeploymentProposalRequest Proposal, string? ApprovedBy = null, DateTimeOffset? ApprovedAtUtc = null);

    public async Task<IntegrationPairingStarted> StartAsync(HostDeploymentProposalRequest proposal, string remote, CancellationToken ct)
    {
        Validate(proposal, pairing.PublicOrigin ?? proposal.PublicOrigin);
        if (!await pairing.TakeAttemptAsync("host-start:" + remote, 30, ct)) throw new PairingRateLimitException();
        await pairing.CleanupAsync(ct);
        var device = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var human = new string(Enumerable.Range(0, 8).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        var expires = Now.AddMinutes(10);
        identity.IntegrationCredentialPairings.Add(new()
        {
            Id = Guid.NewGuid(), DeviceHash = Hash(device), HumanHash = Hash(human),
            Name = proposal.Name, Purpose = "http-host", McpResourceUri = proposal.ResourceUri,
            ExpiresAtUnixMilliseconds = expires.ToUnixTimeMilliseconds(),
            ProtectedApproval = protector.Protect(JsonSerializer.Serialize(new Checkpoint(proposal)))
        });
        await identity.SaveChangesAsync(ct);
        return new(device, human.Insert(4, "-"), $"{proposal.PublicOrigin}/account/integration-credentials/approve?kind=host", expires, 5);
    }

    public async Task<HostDeploymentReview?> ReviewAsync(string human, ClaimsPrincipal principal, string remote, CancellationToken ct)
    {
        await RequireAdministratorAsync(principal, ct);
        var row = await FindHumanAsync(human, principal, ct);
        if (row is null || row.State != "pending" || Expired(row)) return null;
        var checkpoint = Read(row);
        return new(Normalize(human).Insert(4, "-"), checkpoint.Proposal, DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresAtUnixMilliseconds));
    }

    public async Task<bool> ApproveAsync(HostDeploymentApprovalRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var owner = await RequireAdministratorAsync(principal, ct);
        if (!request.Consent) throw Invalid("consent", "Explicit deployment approval is required.");
        var row = await FindHumanAsync(request.HumanCode, principal, ct);
        if (row is null || row.State != "pending" || Expired(row)) return false;
        var checkpoint = Read(row);
        if (request.ProposalHash != checkpoint.Proposal.ProposalHash || request.SourceHash != checkpoint.Proposal.SourceHash)
            throw Invalid("proposalHash", "The deployment generation changed. Review it again.");
        // Current authority and the trusted origin are checked again at the actual mutation boundary.
        Validate(checkpoint.Proposal, pairing.PublicOrigin ?? checkpoint.Proposal.PublicOrigin);
        row.OwnerUserId = owner.UserId;
        row.ProtectedApproval = protector.Protect(JsonSerializer.Serialize(checkpoint with { ApprovedBy = owner.UserId, ApprovedAtUtc = Now }));
        row.State = "approved"; row.Revision++;
        // A saved generation can be applied later without repeating consent; every
        // approval-capability check still revalidates the owner's current authority.
        row.ExpiresAtUnixMilliseconds = Now.AddDays(7).ToUnixTimeMilliseconds();
        return await SaveAsync(ct);
    }

    public async Task<bool> DenyAsync(string human, ClaimsPrincipal principal, CancellationToken ct)
    {
        await RequireAdministratorAsync(principal, ct);
        var row = await FindHumanAsync(human, principal, ct);
        if (row is null || row.State != "pending" || Expired(row)) return false;
        row.State = "denied"; row.ProtectedApproval = null; row.Revision++;
        return await SaveAsync(ct);
    }

    public async Task<HostDeploymentApprovalStatus> PollAsync(string device, CancellationToken ct)
    {
        var row = await FindDeviceAsync(device, ct);
        if (row is null || Expired(row)) return new("expired");
        if (row.State != "approved") return new(row.State);
        var checkpoint = Read(row);
        // An approval cannot outlive loss of its interactive owner's durable administrator authority.
        var owner = await identity.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == row.OwnerUserId && user.IsEnabled, ct);
        if (owner is null) return new("denied");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, owner.Id), new Claim("auth_mode", "local")
        }, "HostApprovalRecheck"));
        if (!(await access.ResolveAsync(principal, ct)).IsHelpdeskAdmin) return new("denied");
        return new("approved", checkpoint.Proposal.ProposalHash, checkpoint.Proposal.SourceHash, checkpoint.ApprovedBy, checkpoint.ApprovedAtUtc);
    }

    public async Task CancelAsync(string device, CancellationToken ct)
    {
        var row = await FindDeviceAsync(device, ct);
        if (row is null || row.State is "cancelled" or "denied" or "expired") return;
        row.State = "cancelled"; row.ProtectedApproval = null; row.Revision++;
        await SaveAsync(ct);
    }

    private async Task<IntegrationCredentialOwner> RequireAdministratorAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(principal, ct);
        if (owner is null || !(await access.ResolveAsync(principal, ct)).IsHelpdeskAdmin) throw new CredentialGrantException(null);
        return owner;
    }
    private async Task<IntegrationCredentialPairing?> FindHumanAsync(string human, ClaimsPrincipal principal, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(principal, ct);
        if (owner is null) throw new CredentialGrantException(null);
        if (!await pairing.TakeAttemptAsync("host-code:" + owner.UserId, 30, ct)) throw new PairingRateLimitException();
        var normalized = Normalize(human);
        if (normalized.Length != 8) return null;
        var hash = Hash(normalized);
        return await identity.IntegrationCredentialPairings.SingleOrDefaultAsync(row => row.Purpose == "http-host" && row.HumanHash == hash, ct);
    }
    private Task<IntegrationCredentialPairing?> FindDeviceAsync(string device, CancellationToken ct)
    {
        if (device is null || device.Length != 64 || !device.All(Uri.IsHexDigit)) return Task.FromResult<IntegrationCredentialPairing?>(null);
        var hash = Hash(device);
        return identity.IntegrationCredentialPairings.SingleOrDefaultAsync(row => row.Purpose == "http-host" && row.DeviceHash == hash, ct);
    }
    private bool Expired(IntegrationCredentialPairing row) => row.ExpiresAtUnixMilliseconds <= Now.ToUnixTimeMilliseconds();
    private Checkpoint Read(IntegrationCredentialPairing row)
    {
        try { return JsonSerializer.Deserialize<Checkpoint>(protector.Unprotect(row.ProtectedApproval!)) ?? throw new CryptographicException(); }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentNullException)
        { throw Invalid("proposal", "The protected proposal is unavailable. Start a fresh review."); }
    }
    private async Task<bool> SaveAsync(CancellationToken ct)
    {
        try { await identity.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { identity.ChangeTracker.Clear(); return false; }
    }
    private static string Normalize(string? code) => (code ?? "").Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static CredentialGrantException Invalid(string key, string message) => new(new Dictionary<string, string[]> { [key] = [message] });
    private static void Validate(HostDeploymentProposalRequest proposal, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var publicUri) || publicUri.Scheme != "https" ||
            publicUri.AbsolutePath != "/" || publicUri.UserInfo.Length != 0 || publicUri.Query.Length != 0 || publicUri.Fragment.Length != 0 ||
            proposal.Name is not { Length: > 0 and <= 128 } || proposal.Name.Any(char.IsControl) ||
            !Regex.IsMatch(proposal.ProposalHash ?? "", "^[A-Fa-f0-9]{64}$") || !Regex.IsMatch(proposal.SourceHash ?? "", "^[A-Fa-f0-9]{64}$") ||
            !SameUri(proposal.PublicOrigin, origin) || !SameUri(proposal.ResourceUri, origin.TrimEnd('/') + "/mcp") ||
            !Regex.IsMatch(proposal.Instance ?? "", "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$") ||
            proposal.AuthenticationMode is not ("gateway" or "authentik") || proposal.PublicOrigin.Length > 2048 || proposal.ResourceUri.Length > 2048 ||
            proposal.ReviewSettings is null || proposal.ReviewSettings.Count > 48 || proposal.ReviewSettings.Any(setting =>
                setting is null || setting.Key is not { Length: > 0 and <= 128 } || setting.Value is not { Length: <= 2048 } || setting.Source is not { Length: <= 256 } ||
                setting.Key.Any(char.IsControl) || setting.Value.Any(char.IsControl) || setting.Source.Any(char.IsControl)) ||
            proposal.Changes is null || proposal.Changes.Count > 32 ||
            proposal.Changes.Any(change => change is null || change.Length > 512 || change.Any(char.IsControl)))
            throw Invalid("proposal", "Use a bounded, valid proposal for this deployment's trusted origin and exact generation.");
        foreach (var backend in new[] { proposal.ApiBaseUrl, proposal.McpDestination })
            if (backend is not { Length: <= 2048 } || !Uri.TryCreate(backend, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw Invalid("proposal", "Deployment backends must be explicit HTTP(S) root destinations without credentials.");
    }
    private static bool SameUri(string? left, string? right) => Uri.TryCreate(left, UriKind.Absolute, out var first) &&
        Uri.TryCreate(right, UriKind.Absolute, out var second) && first.AbsoluteUri.TrimEnd('/') == second.AbsoluteUri.TrimEnd('/');
}
