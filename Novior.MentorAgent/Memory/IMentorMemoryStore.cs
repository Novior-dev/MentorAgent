

namespace Novior.MentorAgent.Memory;

/// <summary>
/// Interface for persisting the Mentor AI contextual memory.
///
/// MentorAgent registers <see cref="InMemoryMentorMemoryStore"/> by default (RAM, zero config).
/// For real persistence, register your own implementation BEFORE AddMentorAgent():
///
/// <code>
/// // Redis
/// builder.Services.AddSingleton&lt;IMentorMemoryStore, RedisMemoryStore&gt;();
///
/// // EF Core
/// builder.Services.AddScoped&lt;IMentorMemoryStore, EfCoreMemoryStore&gt;();
///
/// // MongoDB
/// builder.Services.AddSingleton&lt;IMentorMemoryStore, MongoMemoryStore&gt;();
///
/// builder.Services.AddMentorAgent(options => { ... });
/// </code>
/// </summary>
public interface IMentorMemoryStore
{
    /// <summary>
    /// Saves a memory entry for a user.
    /// </summary>
    /// <param name="userId">Unique user ID. Use "anonymous" if not authenticated.</param>
    /// <param name="key">Memory key (e.g. "last_action", "preferred_filter")</param>
    /// <param name="value">Serialized value (string, JSON, etc.)</param>
    /// <param name="ct">Cancels the save.</param>
    Task SaveAsync(string userId, string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a specific memory entry by key.
    /// Returns null if not found.
    /// </summary>
    Task<string?> GetAsync(string userId, string key, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the most recent N entries for the user, ordered by date descending.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> GetRecentAsync(string userId, int count = 10, CancellationToken ct = default);

    /// <summary>
    /// Deletes a specific memory entry.
    /// </summary>
    Task DeleteAsync(string userId, string key, CancellationToken ct = default);

    /// <summary>
    /// Deletes all memory entries for a user.
    /// </summary>
    Task ClearAsync(string userId, CancellationToken ct = default);
}