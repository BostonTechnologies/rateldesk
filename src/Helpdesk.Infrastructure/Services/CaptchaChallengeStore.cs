using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Services;

public sealed class CaptchaChallengeStore(IOptions<CaptchaOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly Dictionary<string, int> _byClient = new(StringComparer.Ordinal);

    internal Guid? Reserve(string client)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var expired = _entries.Where(pair => pair.Value.Answer is not null && pair.Value.ExpiresAt <= now)
                .Select(pair => pair.Key).ToArray();
            foreach (var id in expired)
                RemoveUnderLock(id);

            if (_entries.Count >= options.Value.Capacity ||
                _byClient.GetValueOrDefault(client) >= options.Value.OutstandingPerClient)
                return null;
            var reservedId = Guid.NewGuid();
            _entries.Add(reservedId, new Entry(client));
            _byClient[client] = _byClient.GetValueOrDefault(client) + 1;
            return reservedId;
        }
    }

    internal void Complete(Guid id, string answer)
    {
        lock (_gate)
        {
            var entry = _entries[id];
            entry.Answer = answer;
            entry.ExpiresAt = clock.GetUtcNow().Add(Lifetime);
        }
    }

    internal string? Consume(Guid id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Answer is null)
                return null;
            RemoveUnderLock(id);
            return entry.ExpiresAt > clock.GetUtcNow() ? entry.Answer : null;
        }
    }

    internal void Remove(Guid id)
    {
        lock (_gate)
            RemoveUnderLock(id);
    }

    private void RemoveUnderLock(Guid id)
    {
        if (!_entries.Remove(id, out var entry))
            return;
        var client = entry.Client;
        if (_byClient[client] == 1)
            _byClient.Remove(client);
        else
            _byClient[client]--;
    }

    private sealed class Entry(string client)
    {
        public string Client { get; } = client;
        public string? Answer { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
