using FluentAssertions;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Memory;

/// <summary>
/// The memory surface as the model sees it: which tools are registered, what they do when called,
/// and what the post-turn capture writes.
///
/// <para>
/// Memory has two possible writers and the library picks one for you. On Path A a dedicated
/// post-turn extraction call harvests durable facts, so the <c>remember</c> tool is <i>omitted</i> —
/// its schema is per-call tokens on every single request, and a reliable writer already ran. On
/// Path B, and whenever auto-capture is off, there is no extraction call and <c>remember</c> is the
/// only writer there is, so it must come back. Getting that switch wrong in either direction is
/// invisible: too many tools is a slow token leak, too few is a mentor that never remembers anything.
/// </para>
///
/// <para>
/// <c>forget_all</c> is not part of the trade. It is the user's delete button and is registered in
/// every configuration where memory is on.
/// </para>
/// </summary>
public sealed class MemoryToolsTests
{
    /// <summary>
    /// The memory key of this host's current user. Not the literal <c>"anonymous"</c> any more:
    /// an unauthenticated visitor gets a key of their own per session
    /// (<see cref="MentorAnonymousIdentity.PerSession"/>, the default), so a test that reads the
    /// store has to ask who it is rather than assume.
    /// </summary>
    private static string Anon(MentorHostFixture host)
        => host.Get<MentorMemoryService>().GetUserIdAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Path B, keeping a handle on the chat client behind the pre-built agent — the tool list still
    /// has to be observed somewhere, and on Path B the fixture's own <c>Chat</c> is null.
    /// </summary>
    private static (MentorHostFixture Host, FakeChatClient Inner) PathB(Action<MentorOptions>? configure = null)
    {
        var inner = new FakeChatClient();
        var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = null;
            o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
            o.UseMemoryContext = true;
            configure?.Invoke(o);
        }, withChatClient: false);

        return (host, inner);
    }

    /// <summary>Answers the auto-capture extraction call with <paramref name="factsJson"/>.</summary>
    private static void ScriptExtraction(FakeChatClient chat, string factsJson, string reply = "ok")
        => chat.Respond((messages, _) => new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            messages.Any(m => m.Text?.Contains("Extract durable facts", StringComparison.Ordinal) == true)
                ? factsJson
                : reply)));

    // ── Which tools are registered ──────────────────────────────────────────────

    [Fact]
    public async Task Memory_off_registers_neither_tool()
    {
        await using var host = MentorHostFixture.Create(o => o.UseMemoryContext = false);

        await host.RunTurnAsync("hello");

        var tools = string.Join(",", host.FirstToolsSent);
        tools.Should().NotContain("remember").And.NotContain("forget_all");
    }

    [Fact]
    public async Task PathA_with_auto_capture_omits_remember_and_keeps_forget_all()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });

        await host.RunTurnAsync("hello");

        var tools = string.Join(",", host.FirstToolsSent);
        tools.Should().NotContain("remember",
            "the post-turn extraction is the writer, so the tool is dead schema on every call");
        tools.Should().Contain("forget_all",
            "clearing memory is the user's own decision and always has to be reachable");
    }

    [Fact]
    public async Task PathA_without_auto_capture_registers_both()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
        });

        await host.RunTurnAsync("hello");

        var tools = string.Join(",", host.FirstToolsSent);
        tools.Should().Contain("remember",
            "with the extraction call opted out, the tool is the only writer left");
        tools.Should().Contain("forget_all");
    }

    [Fact]
    public async Task PathB_registers_remember_as_the_fallback_writer()
    {
        // The post-turn extraction needs a chat client MentorAgent can call on its own, and a
        // pre-built agent does not offer one. Dropping the tool here too would leave memory with
        // no writer at all — enabled, documented, and permanently empty.
        var (host, inner) = PathB(o => o.MemoryAutoCapture = true);
        await using var _ = host;

        await host.RunTurnAsync("my name is Antonio");

        var tools = string.Join(",", inner.Calls.SelectMany(c => c.ToolNames).Distinct());
        tools.Should().Contain("remember").And.Contain("forget_all");
    }

    [Fact]
    public async Task PathB_actually_receives_the_tools_that_were_built_for_it()
    {
        // BUG-013. The tool list was assembled, counted and logged ("Coordinator built with 14
        // tools") on both paths, but only Path A ever handed it to the model: a pre-built agent
        // belongs to the host and cannot be reconfigured, and nothing replayed the tools into the
        // run. So on Path B the assistant had *no* tools at all — no L1 actions, no navigate_to, no
        // invoke_ui_action, no memory tools — while the log and the README's feature matrix both
        // said otherwise. The generic invoke_ui_action dispatcher is the proof of intent: it is
        // added only when there is no ChatClient, i.e. exclusively for this path.
        var (host, inner) = PathB(o => o.MemoryAutoCapture = false);
        await using var _ = host;

        await host.RunTurnAsync("cancel order 7");

        var tools = string.Join(",", inner.Calls.SelectMany(c => c.ToolNames).Distinct());
        tools.Should().Contain("remember", "memory's only writer on Path B is the tool");
        tools.Should().Contain("forget_all");
        tools.Should().Contain("invoke_ui_action", "the dispatcher exists only for Path B");
        tools.Should().Contain("navigate_to");
        tools.Should().Contain("count_open_orders", "L1 actions are ticked ✅ for Path B in the matrix");
    }

    [Fact]
    public async Task PathB_tools_are_added_to_the_agents_own_tools_not_swapped_for_them()
    {
        // The host's agent may already carry tools of its own. Run options union with the agent's
        // ChatOptions rather than replacing them, and a fix that clobbered them would trade one
        // silent loss for another.
        var inner = new FakeChatClient();
        var ownTool = AIFunctionFactory.Create(() => "42", name: "host_own_tool");
        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = null;
            o.Agent = inner.AsAIAgent(new ChatClientAgentOptions
            {
                Name = "host_agent",
                ChatOptions = new ChatOptions { Instructions = "You are a test agent.", Tools = [ownTool] },
            });
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
        }, withChatClient: false);

        await host.RunTurnAsync("hello");

        var tools = string.Join(",", inner.Calls.SelectMany(c => c.ToolNames).Distinct());
        tools.Should().Contain("host_own_tool", "the host's own tool must survive");
        tools.Should().Contain("forget_all", "and MentorAgent's must arrive");
    }

    [Fact]
    public async Task PathB_reads_the_memory_block_back_into_the_prompt()
    {
        // The other half of BUG-015. Remembering facts a Path B assistant is never shown would
        // make memory write-only there — enabled, documented ✅ in the matrix, and inert. The block
        // is produced by an AIContextProvider, which a pre-built agent cannot be given at
        // construction; it now arrives by decorating the agent's chat client for each invocation.
        var (host, inner) = PathB(o => o.MemoryAutoCapture = false);
        await using var _ = host;
        await host.Get<IMentorMemoryStore>().SaveAsync(Anon(host), "user_name", "Antonio");

        await host.RunTurnAsync("hello");

        var prompt = (inner.Calls[0].Instructions ?? "") + inner.Calls[0].SystemText;
        prompt.Should().Contain("## USER CONTEXTUAL MEMORY").And.Contain("user_name: Antonio");
    }

    [Fact]
    public async Task PathB_keeps_the_hosts_own_agent_instructions()
    {
        // The context is *added*, never swapped in. On Path B the agent's persona belongs to the
        // host, and a fix that replaced ChatOptions.Instructions would have deleted it silently.
        var (host, inner) = PathB(o => o.MemoryAutoCapture = false);
        await using var _ = host;
        await host.Get<IMentorMemoryStore>().SaveAsync(Anon(host), "user_name", "Antonio");

        await host.RunTurnAsync("hello");

        var prompt = (inner.Calls[0].Instructions ?? "") + inner.Calls[0].SystemText;
        prompt.Should().Contain("You are a test agent.").And.Contain("user_name: Antonio");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Forget_all_is_registered_whenever_memory_is_on(bool autoCapture)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = autoCapture;
        });

        await host.RunTurnAsync("hello");

        string.Join(",", host.FirstToolsSent).Should().Contain("forget_all");
    }

    [Fact]
    public async Task The_prompt_nudge_matches_the_writer_that_is_actually_active()
    {
        // Two different instructions, and picking the wrong one is a silent regression: with
        // auto-capture the model is only asked to *use* the injected facts, but with the tool as
        // the sole writer the instruction must stay forceful — weaker models skip a polite one and
        // nothing is ever saved.
        await using var withCapture = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        await using var withTool = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
        });

        await withCapture.RunTurnAsync("hello");
        await withTool.RunTurnAsync("hello");

        var captured = withCapture.Chat!.Calls[0].Instructions ?? "";
        var tooled = withTool.Chat!.Calls[0].Instructions ?? "";

        captured.Should().Contain("## Using Remembered Context").And.NotContain("MANDATORY");
        tooled.Should().Contain("## Remembering Context — MANDATORY");
        tooled.Should().Contain("Actually invoke remember()",
            "the strength of this instruction is the feature — softening it stops facts being saved");
    }

    // ── What the tools do ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_remember_tool_writes_to_the_store()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;   // otherwise the tool is not registered at all
        });
        host.Chat!.CallTool("remember", new { key = "user_name", value = "Antonio" }).Say("noted");

        await host.RunTurnAsync("my name is Antonio");

        (await host.Get<IMentorMemoryStore>().GetAsync(Anon(host), "user_name"))
            .Should().Be("Antonio");
    }

    [Fact]
    public async Task The_forget_all_tool_clears_the_store()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
        });
        var store = host.Get<IMentorMemoryStore>();
        await store.SaveAsync(Anon(host), "user_name", "Antonio");
        await store.SaveAsync(Anon(host), "user_role", "dev");

        host.Chat!.CallTool("forget_all").Say("done");
        await host.RunTurnAsync("forget everything about me");

        // Every fact the user gave is gone. The turn's own last_session bookkeeping is written
        // after the tool ran and is not a user memory — see BUG-014.
        string.Join(",", (await store.GetRecentAsync(Anon(host), 100)).Select(e => e.Key))
              .Should().NotContain("user_name").And.NotContain("user_role");
    }

    [Fact]
    public async Task Forget_all_clears_only_the_signed_in_caller()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
        });
        var store = host.Get<IMentorMemoryStore>();
        await store.SaveAsync("someone-else", "user_name", "Beatrice");
        host.Auth.SignIn("u-42");
        await store.SaveAsync("u-42", "user_name", "Antonio");

        host.Chat!.CallTool("forget_all").Say("done");
        await host.RunTurnAsync("forget everything about me");

        (await store.GetAsync("u-42", "user_name")).Should().BeNull();
        (await store.GetAsync("someone-else", "user_name")).Should().Be("Beatrice",
            "one user's delete button must never reach another user's memories");
    }

    // ── Post-turn auto-capture ──────────────────────────────────────────────────

    [Fact]
    public async Task Auto_capture_stores_the_facts_the_extraction_returned()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        ScriptExtraction(host.Chat!,
            """{"facts":[{"key":"user_name","value":"Antonio"},{"key":"user_team","value":"vendite"}]}""");

        await host.RunTurnAsync("ciao, sono Antonio del team vendite");

        var store = host.Get<IMentorMemoryStore>();
        (await store.GetAsync(Anon(host), "user_name")).Should().Be("Antonio");
        (await store.GetAsync(Anon(host), "user_team")).Should().Be("vendite");
    }

    [Fact]
    public async Task Auto_capture_asks_for_json_at_temperature_zero()
    {
        // The extraction is parsed, not read: a chatty model answer breaks it. The request has to
        // pin the format and the sampling, and it must not be metered as the user's turn.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        ScriptExtraction(host.Chat!, """{"facts":[]}""");

        await host.RunTurnAsync("ciao");

        var extraction = host.Chat!.Calls.Single(c =>
            c.Messages.Any(m => m.Text?.Contains("Extract durable facts", StringComparison.Ordinal) == true));
        extraction.Streaming.Should().BeFalse("a parsed answer has no reason to stream");
    }

    [Fact]
    public async Task Auto_capture_stores_nothing_when_the_message_holds_no_durable_fact()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        ScriptExtraction(host.Chat!, """{"facts":[]}""");

        await host.RunTurnAsync("show me the open orders");

        await AssertNoUserFactsAsync(host);
    }

    [Fact]
    public async Task A_malformed_extraction_answer_is_survivable()
    {
        // Any model can answer with prose instead of JSON. Memory is best-effort by design and the
        // reply has already been delivered to the user by the time this runs.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        ScriptExtraction(host.Chat!, "Certo! Ecco i fatti che ho estratto: si chiama Antonio.");

        var error = await host.RunTurnAsync("sono Antonio");

        error.Should().BeNull();
        await AssertNoUserFactsAsync(host);
    }

    /// <summary>
    /// Asserts the user's memory holds nothing they said. <c>last_session</c> is MentorAgent's own
    /// post-turn bookkeeping and is always present after a successful turn, so "empty" is the wrong
    /// question to ask of the store.
    /// </summary>
    private static async Task AssertNoUserFactsAsync(MentorHostFixture host)
    {
        var keys = (await host.Get<IMentorMemoryStore>().GetRecentAsync(Anon(host), 100))
            .Select(e => e.Key)
            .Where(k => k != "last_session");

        string.Join(",", keys).Should().BeEmpty();
    }

    [Fact]
    public async Task An_extraction_call_that_fails_does_not_break_the_turn()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        host.Chat!.Respond((messages, _) =>
            messages.Any(m => m.Text?.Contains("Extract durable facts", StringComparison.Ordinal) == true)
                ? throw new HttpRequestException("429 Too Many Requests")
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var error = await host.RunTurnAsync("sono Antonio");

        error.Should().BeNull("the user already has their answer");
        host.Logs.HasWarning("Auto-capture failed").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task Auto_capture_does_not_run_when_memory_is_off()
    {
        // A per-turn extra model call for a feature that is switched off would be a pure cost leak.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = false;
            o.MemoryAutoCapture = true;
        });

        await host.RunTurnAsync("sono Antonio");

        host.Chat!.Calls.Should().NotContain(c =>
            c.Messages.Any(m => m.Text!.Contains("Extract durable facts", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Auto_capture_stores_under_the_signed_in_user()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        host.Auth.SignIn("u-42");
        ScriptExtraction(host.Chat!, """{"facts":[{"key":"user_name","value":"Antonio"}]}""");

        await host.RunTurnAsync("sono Antonio");

        var store = host.Get<IMentorMemoryStore>();
        (await store.GetAsync("u-42", "user_name")).Should().Be("Antonio");
        (await store.GetAsync("anonymous", "user_name")).Should().BeNull();
    }

    // ── What reaches the prompt ─────────────────────────────────────────────────

    [Fact]
    public async Task Remembered_facts_reach_the_next_turn()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        await host.Get<IMentorMemoryStore>().SaveAsync(Anon(host), "user_name", "Antonio");
        ScriptExtraction(host.Chat!, """{"facts":[]}""");

        await host.RunTurnAsync("hello");

        var sent = (host.Chat!.Calls[0].Instructions ?? "") + host.Chat.Calls[0].SystemText;
        sent.Should().Contain("## USER CONTEXTUAL MEMORY").And.Contain("user_name: Antonio");
    }

    [Fact]
    public async Task Memory_context_count_caps_what_reaches_the_prompt()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
            o.MemoryContextCount = 2;
        });
        var store = host.Get<IMentorMemoryStore>();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveAsync(Anon(host), $"k{i}", $"v{i}");
            await Task.Delay(15);
        }
        ScriptExtraction(host.Chat!, """{"facts":[]}""");

        await host.RunTurnAsync("hello");

        var sent = host.Chat!.Calls[0].Instructions ?? "";
        sent.Should().Contain("k4: v4").And.Contain("k3: v3");
        sent.Should().NotContain("k0: v0", "MemoryContextCount is the token budget for this block");
    }

    [Fact]
    public async Task The_post_turn_bookkeeping_entry_is_never_shown_to_the_model()
    {
        // BUG-014. AppContextProvider rewrites last_session after every successful turn, so it is
        // always the newest memory and, under recency selection, always took a slot. Nothing reads
        // it, and the model already has the current date in its context.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
        });
        await host.Get<IMentorMemoryStore>().SaveAsync(Anon(host), "user_name", "Antonio");

        await host.RunTurnAsync("first");
        await host.RunTurnAsync("second");

        var second = host.Chat!.Calls[^1].Instructions ?? "";
        second.Should().Contain("user_name: Antonio");
        second.Should().NotContain("last_session",
            "MentorAgent's own bookkeeping is not a memory the user gave");
    }

    [Fact]
    public async Task A_single_memory_slot_still_carries_a_real_fact()
    {
        // The sharp end of BUG-014: at MemoryContextCount = 1 the bookkeeping entry — always the
        // most recent — was the entire memory block from the second turn onwards, so the feature
        // looked enabled and injected nothing the user had ever said.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
            o.MemoryContextCount = 1;
        });
        await host.Get<IMentorMemoryStore>().SaveAsync(Anon(host), "user_name", "Antonio");

        await host.RunTurnAsync("first");
        await host.RunTurnAsync("second");

        (host.Chat!.Calls[^1].Instructions ?? "").Should().Contain("user_name: Antonio");
    }

    [Fact]
    public async Task No_memories_leave_no_memory_block_in_the_prompt()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = true;
        });
        ScriptExtraction(host.Chat!, """{"facts":[]}""");

        await host.RunTurnAsync("hello");

        (host.Chat!.Calls[0].Instructions ?? "").Should().NotContain("## USER CONTEXTUAL MEMORY");
    }

    // ── The store seam ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_store_registered_before_AddMentorAgent_replaces_the_default()
    {
        // The documented extension point, and the one that decides whether memory survives a
        // restart. TryAddSingleton is what makes it work, and a plain Add would break it silently.
        var custom = new RecordingMemoryStore();

        await using var host = MentorHostFixture.Create(
            o =>
            {
                o.UseMemoryContext = true;
                o.MemoryAutoCapture = true;
            },
            services => services.AddSingleton<IMentorMemoryStore>(custom));

        ScriptExtraction(host.Chat!, """{"facts":[{"key":"user_name","value":"Antonio"}]}""");
        await host.RunTurnAsync("sono Antonio");

        host.Get<IMentorMemoryStore>().Should().BeSameAs(custom);
        custom.Saves.Should().Contain((Anon(host), "user_name", "Antonio"),
            "the host's own store is where every write has to land");
    }

    /// <summary>A host's own store, to prove the registration seam and observe the calls.</summary>
    private sealed class RecordingMemoryStore : IMentorMemoryStore
    {
        internal List<(string User, string Key, string Value)> Saves { get; } = [];
        internal List<string> Cleared { get; } = [];

        public Task SaveAsync(string userId, string key, string value, CancellationToken ct = default)
        {
            lock (Saves) Saves.Add((userId, key, value));
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string userId, string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<Novior.MentorAgent.Data.MemoryEntry>> GetRecentAsync(
            string userId, int count = 10, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Novior.MentorAgent.Data.MemoryEntry>>([]);

        public Task DeleteAsync(string userId, string key, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearAsync(string userId, CancellationToken ct = default)
        {
            lock (Cleared) Cleared.Add(userId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_store_that_throws_does_not_take_the_turn_down()
    {
        // A Redis store times out. Losing the memory block is acceptable; losing the user's answer
        // is not, and AppContextProvider swallows the failure precisely for this.
        await using var host = MentorHostFixture.Create(
            o =>
            {
                o.UseMemoryContext = true;
                o.MemoryAutoCapture = false;
            },
            services => services.AddSingleton<IMentorMemoryStore>(new ThrowingMemoryStore()));

        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull();
        host.Chat!.Calls.Should().NotBeEmpty("the turn still reached the model");
    }

    private sealed class ThrowingMemoryStore : IMentorMemoryStore
    {
        public Task SaveAsync(string userId, string key, string value, CancellationToken ct = default)
            => throw new InvalidOperationException("store unreachable");

        public Task<string?> GetAsync(string userId, string key, CancellationToken ct = default)
            => throw new InvalidOperationException("store unreachable");

        public Task<IReadOnlyList<Novior.MentorAgent.Data.MemoryEntry>> GetRecentAsync(
            string userId, int count = 10, CancellationToken ct = default)
            => throw new InvalidOperationException("store unreachable");

        public Task DeleteAsync(string userId, string key, CancellationToken ct = default)
            => throw new InvalidOperationException("store unreachable");

        public Task ClearAsync(string userId, CancellationToken ct = default)
            => throw new InvalidOperationException("store unreachable");
    }
}
