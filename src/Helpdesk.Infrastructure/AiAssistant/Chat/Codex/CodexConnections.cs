using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using EntityState = Helpdesk.Shared.Models.EntityState;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.Codex;

public sealed class CodexConnections(HelpdeskDbContext db, CodexAppServerHost host, TimeProvider clock)
{
    private static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(10);

    public async Task<CodexConnectionView> BeginAsync(string owner, string organization, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) throw new ChatConflictException("Native AI Providers require PostgreSQL.");
        var hostId = await host.CheckAsync(ct);
        using var deadline = CodexAppServerHost.BeginDeadline(ct);
        var token = deadline.Token;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var scope = $"{owner}\n{organization}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope}, 441))", token);
        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(row => row.Id == organization && row.State == EntityState.Enabled, token))
            throw new ArgumentException("Choose an enabled organization.");

        var now = clock.GetUtcNow();
        var pending = await db.Set<CodexConnection>().AsNoTracking()
            .Where(row => row.OwnerId == owner && row.OrganizationId == organization && row.HostId == hostId &&
                row.State == CodexConnectionState.Pending && row.ExpiresAtUtc > now)
            .OrderByDescending(row => row.StartedAtUtc).FirstOrDefaultAsync(token);
        if (pending is not null)
            throw new ChatConflictException("A device sign-in is already pending. Open its status or cancel it before retrying.");

        var row = new CodexConnection { Id = Guid.NewGuid(), OwnerId = owner, OrganizationId = organization, HostId = hostId,
            State = CodexConnectionState.Pending, StartedAtUtc = now, ExpiresAtUtc = now + LoginLifetime };
        await using var runtime = await host.AcquireAsync(row.Id, hostId, token);
        var server = await runtime.OpenAsync(token);
        try
        {
            var start = await server.CallAsync("account/login/start", new { type = "chatgptDeviceCode" }, token);
            runtime.Challenge = ParseChallenge(start);
            db.Add(row);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return View(row, runtime.Challenge);
        }
        catch
        {
            await runtime.CloseAsync();
            throw;
        }
    }

    public async Task<CodexConnectionView[]> ListAsync(string owner, string organization, CancellationToken ct)
    {
        var rows = await db.Set<CodexConnection>().AsNoTracking().Where(row => row.OwnerId == owner && row.OrganizationId == organization)
            .OrderByDescending(row => row.StartedAtUtc).Take(25).ToArrayAsync(ct);
        return rows.Select(row => View(row)).ToArray();
    }

    public async Task<CodexConnectionView> StatusAsync(Guid id, string owner, string organization, CancellationToken ct)
    {
        using var deadline = CodexAppServerHost.BeginDeadline(ct);
        var token = deadline.Token;
        var row = await OwnedAsync(id, owner, organization, token);
        if (row.State is not (CodexConnectionState.Pending or CodexConnectionState.Connected)) return View(row);
        await using var runtime = await host.AcquireAsync(row.Id, row.HostId, token);
        if (row.State == CodexConnectionState.Pending)
        {
            var challenge = runtime.Challenge;
            if (challenge is null || row.ExpiresAtUtc <= clock.GetUtcNow())
            {
                await CloseLoginAsync(row, runtime, CodexConnectionState.Expired, token);
                return View(row);
            }
            var server = await runtime.OpenAsync(token);
            if (server.LoginResult is not { } result) return View(row, challenge);
            if (CodexWire.String(result.Parameters, "loginId") != challenge.LoginId) return View(row, challenge);
            if (result.Parameters.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            {
                var account = await ReadIdentityAsync(server, token);
                row.AccountLabel = account?.Email;
                row.Plan = account?.Plan;
                row.WorkspaceLabel = account?.Workspace;
                row.State = account is null ? CodexConnectionState.PlanAccessUnavailable : CodexConnectionState.Connected;
                runtime.Challenge = null;
                await db.SaveChangesAsync(token);
                return View(row);
            }
            await CloseLoginAsync(row, runtime, CodexConnectionState.Failed, token);
            return View(row);
        }

        var current = await runtime.OpenAsync(token);
        if (!await AccountMatchesAsync(row, current, token))
        {
            row.State = CodexConnectionState.ReauthenticationRequired;
            await db.SaveChangesAsync(token);
        }
        return View(row);
    }

    public async Task<CodexConnectionView> EndAsync(Guid id, string owner, string organization, CancellationToken ct)
    {
        var row = await OwnedAsync(id, owner, organization, ct);
        if (row.State is CodexConnectionState.Cancelled or CodexConnectionState.Disconnected) return View(row);
        var state = row.State == CodexConnectionState.Pending ? CodexConnectionState.Cancelled : CodexConnectionState.Disconnected;
        row.State = state;
        await db.SaveChangesAsync(ct);
        using var deadline = CodexAppServerHost.BeginDeadline(ct);
        try
        {
            await using var runtime = await host.AcquireAsync(row.Id, row.HostId, deadline.Token);
            await CloseLoginAsync(row, runtime, state, deadline.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ChatConflictException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            row.Detail = "Local access is stopped. Verify revocation in ChatGPT when the original runtime is unavailable.";
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return View(row);
    }

    public async Task<OpenAiModelCatalog> ModelsAsync(Guid id, string owner, string organization, CancellationToken ct)
    {
        using var deadline = CodexAppServerHost.BeginDeadline(ct);
        var token = deadline.Token;
        var row = await OwnedAsync(id, owner, organization, token);
        if (row.State != CodexConnectionState.Connected) throw new ChatConflictException("Connect your ChatGPT plan before checking models.");
        await using var runtime = await host.AcquireAsync(id, row.HostId, token);
        var server = await runtime.OpenAsync(token);
        if (!await AccountMatchesAsync(row, server, token))
            throw new ChatConflictException("The original ChatGPT account is unavailable. Reconnect to start new work.");

        var models = new Dictionary<string, OpenAiModelChoice>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await server.CallAsync("model/list", new { limit = 100, includeHidden = false, cursor }, token);
            foreach (var item in CodexWire.Array(page, "data"))
            {
                if (CodexWire.String(item, "model") is not { } model || !OpenAiChatProfiles.Identifier(model, 128)) continue;
                var display = CodexWire.String(item, "displayName");
                var efforts = CodexWire.Array(item, "supportedReasoningEfforts").Select(effort => CodexWire.String(effort, "reasoningEffort"))
                    .OfType<string>().Where(CodexWire.IsReasoningEffort).Distinct(StringComparer.Ordinal).ToArray();
                models.TryAdd(model, new(model, display is { Length: > 0 and <= 128 } ? display : model, efforts));
            }
            cursor = CodexWire.String(page, "nextCursor");
            if (cursor is not null && (cursors.Count >= 20 || !cursors.Add(cursor)))
                throw new InvalidDataException("Codex returned an invalid model catalog cursor.");
        } while (cursor is not null);
        return new(clock.GetUtcNow(), models.Values.OrderBy(model => model.Name, StringComparer.Ordinal).ToArray());
    }

    internal async Task<CodexConnection> OwnedAsync(Guid id, string owner, string organization, CancellationToken ct) =>
        await db.Set<CodexConnection>().SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == owner && row.OrganizationId == organization, ct)
        ?? throw new KeyNotFoundException("ChatGPT connection not found.");

    private async Task CloseLoginAsync(CodexConnection row, CodexAppServerHost.RuntimeUse runtime,
        CodexConnectionState state, CancellationToken ct)
    {
        row.State = state;
        await db.SaveChangesAsync(ct);
        try
        {
            var server = await runtime.OpenAsync(ct);
            if (runtime.Challenge is { } challenge)
                await server.CallAsync("account/login/cancel", new { challenge.LoginId }, ct);
            await server.CallAsync("account/logout", new { }, ct);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            row.Detail = "Local access is stopped. Verify revocation in ChatGPT if the login completed.";
        }
        finally
        {
            await runtime.CloseAsync();
            host.RemoveLocalCredential(row.Id);
        }
        if (row.Detail is not null) await db.SaveChangesAsync(CancellationToken.None);
    }

    internal static async Task<bool> AccountMatchesAsync(CodexConnection row, ICodexAppServerRpc server, CancellationToken ct)
    {
        var account = await ReadIdentityAsync(server, ct);
        return account is not null && StringComparer.OrdinalIgnoreCase.Equals(account.Email, row.AccountLabel) &&
            StringComparer.Ordinal.Equals(account.Workspace, row.WorkspaceLabel);
    }

    private static async Task<Identity?> ReadIdentityAsync(ICodexAppServerRpc server, CancellationToken ct)
    {
        var result = await server.CallAsync("account/read", new { refreshToken = false }, ct);
        if (!result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object ||
            CodexWire.String(account, "type") != "chatgpt" ||
            CodexWire.String(account, "email") is not { Length: > 0 and <= 254 } email ||
            email.Any(char.IsControl) || CodexWire.String(account, "planType") is not ("plus" or "pro" or "prolite" or "promax"))
            return null;
        var workspace = CodexWire.String(account, "workspaceName");
        if (workspace?.Length > 128 || workspace?.Any(char.IsControl) == true) return null;
        return new(email, CodexWire.String(account, "planType")!, workspace);
    }

    internal static CodexDeviceChallenge ParseChallenge(JsonElement start)
    {
        var url = CodexWire.String(start, "verificationUrl");
        var code = CodexWire.String(start, "userCode");
        var login = CodexWire.String(start, "loginId");
        if (CodexWire.String(start, "type") != "chatgptDeviceCode" || !Guid.TryParse(login, out _) ||
            url != "https://auth.openai.com/codex/device" || code is not { Length: >= 4 and <= 32 } ||
            code.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new InvalidDataException("The Codex runtime did not return a valid device sign-in challenge.");
        return new(login!, url, code);
    }

    private static CodexConnectionView View(CodexConnection row, CodexDeviceChallenge? challenge = null) =>
        new(row.Id, row.OrganizationId, row.State, row.AccountLabel, row.Plan,
            row.State == CodexConnectionState.Pending ? row.ExpiresAtUtc : null, challenge?.Url, challenge?.Code, row.Detail);

    private sealed record Identity(string Email, string Plan, string? Workspace);
}

internal static class CodexWire
{
    internal static string? String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString() : null;

    internal static IEnumerable<JsonElement> Array(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.Array
            ? found.EnumerateArray() : Enumerable.Empty<JsonElement>();

    internal static bool IsReasoningEffort(string effort) => effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra";
}
