

namespace Novior.MentorAgent.Memory;

/// <summary>
/// Default implementation of <see cref="IMentorMemoryStore"/> backed by RAM.
///
/// Automatically registered by AddMentorAgent() with TryAddSingleton —
/// used only if the developer has not registered their own implementation.
///
/// LIMITATIONS:
/// - Data is lost on app restart
/// - Does not scale across multiple instances (load balancer)
///
/// For production use Redis, EF Core, MongoDB, or any other implementation
/// of IMentorMemoryStore registered before AddMentorAgent().
/// </summary>
internal sealed class InMemoryMentorMemoryStore : IMentorMemoryStore
{
    private readonly Dictionary<string, Dictionary<string, MemoryEntry>> _store = new();
    private readonly Lock _lock = new();

    public Task SaveAsync(string userId, string key, string value, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_store.TryGetValue(userId, out var userStore))
            {
                userStore = new Dictionary<string, MemoryEntry>();
                _store[userId] = userStore;
            }

            userStore[key] = new MemoryEntry(userId, key, value, DateTime.UtcNow);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string userId, string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_store.TryGetValue(userId, out var userStore) &&
                userStore.TryGetValue(key, out var entry))
                return Task.FromResult<string?>(entry.Value);

            return Task.FromResult<string?>(null);
        }
    }

    public Task<IReadOnlyList<MemoryEntry>> GetRecentAsync(
        string userId, int count = 10, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_store.TryGetValue(userId, out var userStore))
                return Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

            var recent = userStore.Values
                .OrderByDescending(e => e.SavedAt)
                .Take(count)
                .ToList();

            return Task.FromResult<IReadOnlyList<MemoryEntry>>(recent);
        }
    }

    public Task DeleteAsync(string userId, string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_store.TryGetValue(userId, out var userStore))
                userStore.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(string userId, CancellationToken ct = default)
    {
        lock (_lock)
            _store.Remove(userId);

        return Task.CompletedTask;
    }
}