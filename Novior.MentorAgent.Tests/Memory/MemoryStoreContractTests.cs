using FluentAssertions;
using Novior.MentorAgent.Memory;
using Xunit;

namespace Novior.MentorAgent.Tests.Memory;

/// <summary>
/// The <see cref="IMentorMemoryStore"/> contract, as a suite any implementation can inherit.
///
/// <para>
/// This interface is one of MentorAgent's advertised extension seams: the README tells hosts to
/// register a Redis, EF Core or Mongo store <em>before</em> <c>AddMentorAgent()</c> and says nothing
/// else about what the store must do. Everything downstream — auto-capture, the injected memory
/// block, per-user isolation, the <c>forget_all</c> tool — is written against assumptions that live
/// only in the default in-RAM implementation. Writing them down as an inheritable suite is the
/// difference between "here is an interface" and "here is a contract".
/// </para>
///
/// <para>
/// Isolation is the part that matters most: every method takes a <c>userId</c>, and an
/// implementation that ignores it leaks one user's remembered facts into another user's prompt.
/// </para>
/// </summary>
public abstract class MemoryStoreContract
{
    /// <summary>A fresh, empty store. One per test — these tests assert on the whole contents.</summary>
    protected abstract IMentorMemoryStore CreateStore();

    private const string Alice = "alice";
    private const string Bob = "bob";

    // ── Save / Get ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_saved_value_reads_back()
    {
        var store = CreateStore();

        await store.SaveAsync(Alice, "user_name", "Antonio");

        (await store.GetAsync(Alice, "user_name")).Should().Be("Antonio");
    }

    [Fact]
    public async Task An_unknown_key_reads_as_null()
    {
        var store = CreateStore();
        await store.SaveAsync(Alice, "user_name", "Antonio");

        // Null, not empty string and not an exception: MentorMemoryService.RecallAsync returns this
        // straight to a caller, and the documented meaning of "not found" is null.
        (await store.GetAsync(Alice, "user_role")).Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_user_reads_as_null()
    {
        var store = CreateStore();
        await store.SaveAsync(Alice, "user_name", "Antonio");

        (await store.GetAsync(Bob, "user_name")).Should().BeNull();
    }

    [Fact]
    public async Task Saving_the_same_key_twice_overwrites_rather_than_appends()
    {
        // Auto-capture re-extracts the same facts on every turn that mentions them. Appending
        // would grow the memory block without bound and push the older, still-true value into the
        // prompt alongside the new one.
        var store = CreateStore();

        await store.SaveAsync(Alice, "preferred_format", "PDF");
        await store.SaveAsync(Alice, "preferred_format", "CSV");

        (await store.GetAsync(Alice, "preferred_format")).Should().Be("CSV");
        (await store.GetRecentAsync(Alice, 10)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Keys_are_case_sensitive()
    {
        // Keys are identifiers, not prose — auto-capture emits snake_case and the sticky-fact rule
        // matches on the "user_"/"pref" prefix. A store that folds case (the default for several
        // SQL collations) would merge user_name and User_Name into one entry.
        var store = CreateStore();

        await store.SaveAsync(Alice, "user_name", "Antonio");
        await store.SaveAsync(Alice, "USER_NAME", "Someone else");

        (await store.GetAsync(Alice, "user_name")).Should().Be("Antonio");
        (await store.GetRecentAsync(Alice, 10)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_empty_value_round_trips()
    {
        var store = CreateStore();

        await store.SaveAsync(Alice, "user_note", "");

        (await store.GetAsync(Alice, "user_note")).Should().Be("");
    }

    [Fact]
    public async Task A_multiline_unicode_value_round_trips_unchanged()
    {
        // Values reach the store as whatever the model extracted. RememberObjectAsync puts JSON
        // here, and auto-capture puts the user's own wording — in any language.
        var store = CreateStore();
        const string value = "Città: Napoli\nRuolo: sviluppatore — 日本語 🎉\t{\"json\":true}";

        await store.SaveAsync(Alice, "user_profile", value);

        (await store.GetAsync(Alice, "user_profile")).Should().Be(value);
    }

    // ── GetRecent ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recent_returns_the_newest_first()
    {
        var store = CreateStore();

        await SaveOrdered(store, Alice, ("first", "1"), ("second", "2"), ("third", "3"));

        var recent = await store.GetRecentAsync(Alice, 10);

        string.Join(",", recent.Select(e => e.Key)).Should().Be("third,second,first");
    }

    [Fact]
    public async Task Recent_honours_the_count()
    {
        var store = CreateStore();

        await SaveOrdered(store, Alice, ("a", "1"), ("b", "2"), ("c", "3"), ("d", "4"));

        string.Join(",", (await store.GetRecentAsync(Alice, 2)).Select(e => e.Key))
              .Should().Be("d,c");
    }

    [Fact]
    public async Task Recent_defaults_to_ten()
    {
        var store = CreateStore();

        for (var i = 0; i < 15; i++) await SaveOrdered(store, Alice, ($"k{i:00}", $"{i}"));

        // The default is what AppContextProvider would get if MemoryContextCount were ever dropped
        // from the call, and it is documented on the interface.
        (await store.GetRecentAsync(Alice)).Should().HaveCount(10);
    }

    [Fact]
    public async Task Recent_keeps_the_newest_when_more_than_the_count_were_saved_in_one_turn()
    {
        // Auto-capture writes every extracted fact in a tight loop, so a whole batch can land
        // inside one clock tick. If the store orders by a timestamp with millisecond resolution,
        // the tie-break decides which memories reach the prompt — and a stable sort over
        // insertion order silently returns the OLDEST N instead of the newest.
        var store = CreateStore();

        for (var i = 0; i < 20; i++) await store.SaveAsync(Alice, $"k{i:00}", $"{i}");

        var recent = await store.GetRecentAsync(Alice, 5);

        string.Join(",", recent.Select(e => e.Key))
              .Should().Be("k19,k18,k17,k16,k15",
                  "the five most recently saved memories are the five that must survive the cap");
    }

    [Fact]
    public async Task Recent_for_an_unknown_user_is_empty_rather_than_null()
    {
        var store = CreateStore();

        var recent = await store.GetRecentAsync(Bob, 10);

        // GetMemoryContextAsync calls .Count on the result without a null check.
        recent.Should().NotBeNull();
        recent.Should().BeEmpty();
    }

    [Fact]
    public async Task Recent_with_a_count_of_zero_is_empty()
    {
        var store = CreateStore();
        await store.SaveAsync(Alice, "user_name", "Antonio");

        (await store.GetRecentAsync(Alice, 0)).Should().BeEmpty();
    }

    [Fact]
    public async Task Recent_carries_the_user_the_key_and_the_value_back()
    {
        var store = CreateStore();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await store.SaveAsync(Alice, "user_name", "Antonio");

        var entry = (await store.GetRecentAsync(Alice, 1)).Single();
        entry.UserId.Should().Be(Alice);
        entry.Key.Should().Be("user_name");
        entry.Value.Should().Be("Antonio");
        entry.SavedAt.Should().BeAfter(before, "the injected memory line prints this timestamp");
        entry.SavedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Overwriting_a_memory_makes_it_the_most_recent_again()
    {
        var store = CreateStore();

        await SaveOrdered(store, Alice, ("user_name", "Antonio"), ("user_role", "dev"));
        await SaveOrdered(store, Alice, ("user_name", "Antonio Macri"));

        string.Join(",", (await store.GetRecentAsync(Alice, 10)).Select(e => e.Key))
              .Should().Be("user_name,user_role");
    }

    // ── Delete / Clear ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_removes_only_that_key()
    {
        var store = CreateStore();
        await SaveOrdered(store, Alice, ("user_name", "Antonio"), ("user_role", "dev"));

        await store.DeleteAsync(Alice, "user_name");

        (await store.GetAsync(Alice, "user_name")).Should().BeNull();
        (await store.GetAsync(Alice, "user_role")).Should().Be("dev");
    }

    [Fact]
    public async Task A_deleted_memory_stops_appearing_in_recent()
    {
        var store = CreateStore();
        await SaveOrdered(store, Alice, ("user_name", "Antonio"), ("user_role", "dev"));

        await store.DeleteAsync(Alice, "user_name");

        string.Join(",", (await store.GetRecentAsync(Alice, 10)).Select(e => e.Key)).Should().Be("user_role");
    }

    [Fact]
    public async Task Deleting_an_unknown_key_or_user_is_a_no_op()
    {
        var store = CreateStore();
        await store.SaveAsync(Alice, "user_name", "Antonio");

        var act = async () =>
        {
            await store.DeleteAsync(Alice, "never_saved");
            await store.DeleteAsync("nobody", "user_name");
        };

        await act.Should().NotThrowAsync();
        (await store.GetAsync(Alice, "user_name")).Should().Be("Antonio");
    }

    [Fact]
    public async Task Clear_removes_everything_for_that_user()
    {
        // This is the whole of the forget_all tool. A partial clear is a privacy failure, not a bug.
        var store = CreateStore();
        await SaveOrdered(store, Alice, ("user_name", "Antonio"), ("user_role", "dev"), ("pref_x", "y"));

        await store.ClearAsync(Alice);

        (await store.GetRecentAsync(Alice, 100)).Should().BeEmpty();
        (await store.GetAsync(Alice, "user_name")).Should().BeNull();
    }

    [Fact]
    public async Task Clearing_an_unknown_user_is_a_no_op()
    {
        var store = CreateStore();
        await store.SaveAsync(Alice, "user_name", "Antonio");

        var act = async () => await store.ClearAsync("nobody");

        await act.Should().NotThrowAsync();
        (await store.GetAsync(Alice, "user_name")).Should().Be("Antonio");
    }

    [Fact]
    public async Task A_cleared_user_can_be_written_to_again()
    {
        // The in-RAM store drops the user's whole bucket on clear; a store that left a tombstone
        // behind would swallow the next write.
        var store = CreateStore();
        await store.SaveAsync(Alice, "user_name", "Antonio");
        await store.ClearAsync(Alice);

        await store.SaveAsync(Alice, "user_name", "Antonio");

        (await store.GetAsync(Alice, "user_name")).Should().Be("Antonio");
    }

    // ── Per-user isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Two_users_holding_the_same_key_do_not_see_each_others_value()
    {
        var store = CreateStore();

        await store.SaveAsync(Alice, "user_name", "Antonio");
        await store.SaveAsync(Bob, "user_name", "Beatrice");

        (await store.GetAsync(Alice, "user_name")).Should().Be("Antonio");
        (await store.GetAsync(Bob, "user_name")).Should().Be("Beatrice");
    }

    [Fact]
    public async Task Recent_never_returns_another_users_memories()
    {
        var store = CreateStore();
        await SaveOrdered(store, Alice, ("a1", "x"), ("a2", "y"));
        await SaveOrdered(store, Bob, ("b1", "x"));

        var recent = await store.GetRecentAsync(Alice, 100);

        string.Join(",", recent.Select(e => e.Key)).Should().Be("a2,a1");
        recent.Select(e => e.UserId).Distinct().Should().ContainSingle().Which.Should().Be(Alice);
    }

    [Fact]
    public async Task Deleting_and_clearing_one_user_leaves_the_other_untouched()
    {
        var store = CreateStore();
        await store.SaveAsync(Alice, "user_name", "Antonio");
        await store.SaveAsync(Bob, "user_name", "Beatrice");
        await store.SaveAsync(Bob, "user_role", "ops");

        await store.ClearAsync(Alice);
        await store.DeleteAsync(Alice, "user_name");

        (await store.GetAsync(Bob, "user_name")).Should().Be("Beatrice");
        (await store.GetRecentAsync(Bob, 10)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_empty_user_id_is_a_user_like_any_other()
    {
        // GetUserIdAsync can only return a claim value or "anonymous", but a host calling the store
        // directly is not bound by that, and silently sharing one bucket would be the worst outcome.
        var store = CreateStore();

        await store.SaveAsync("", "user_name", "Nobody");
        await store.SaveAsync("anonymous", "user_name", "Anon");

        (await store.GetAsync("", "user_name")).Should().Be("Nobody");
        (await store.GetAsync("anonymous", "user_name")).Should().Be("Anon");
    }

    // ── Concurrency ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_writes_for_one_user_all_land()
    {
        // The default store is a singleton shared by every connection, so concurrency is not a
        // stress-test scenario — it is the normal operating mode of a multi-user server.
        var store = CreateStore();

        await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(i => store.SaveAsync(Alice, $"k{i:000}", $"{i}")));

        (await store.GetRecentAsync(Alice, 1000)).Should().HaveCount(100);
    }

    [Fact]
    public async Task Concurrent_writes_and_reads_across_users_do_not_throw()
    {
        var store = CreateStore();

        var act = async () => await Task.WhenAll(Enumerable.Range(0, 200).Select(async i =>
        {
            var user = i % 2 == 0 ? Alice : Bob;
            await store.SaveAsync(user, $"k{i:000}", $"{i}");
            await store.GetRecentAsync(user, 10);
            await store.GetAsync(user, $"k{i:000}");
        }));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Clearing_one_user_while_another_writes_does_not_throw()
    {
        var store = CreateStore();

        var act = async () => await Task.WhenAll(
            Task.Run(async () => { for (var i = 0; i < 200; i++) await store.SaveAsync(Bob, $"k{i:000}", $"{i}"); }),
            Task.Run(async () => { for (var i = 0; i < 200; i++) await store.ClearAsync(Alice); }),
            Task.Run(async () => { for (var i = 0; i < 200; i++) await store.SaveAsync(Alice, $"k{i:000}", $"{i}"); }));

        await act.Should().NotThrowAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves entries with a distinguishable timestamp each. <see cref="DateTime.UtcNow"/> has a
    /// coarse resolution on Windows, so a test about <em>ordering</em> must separate the writes;
    /// the tie-break itself is covered by its own test above.
    /// </summary>
    private static async Task SaveOrdered(
        IMentorMemoryStore store, string userId, params (string Key, string Value)[] entries)
    {
        foreach (var (key, value) in entries)
        {
            await store.SaveAsync(userId, key, value);
            await Task.Delay(20);
        }
    }
}

/// <summary>
/// The contract, run against the store MentorAgent registers when the host does not supply one.
/// </summary>
public sealed class InMemoryMentorMemoryStoreTests : MemoryStoreContract
{
    protected override IMentorMemoryStore CreateStore() => new InMemoryMentorMemoryStore();
}
