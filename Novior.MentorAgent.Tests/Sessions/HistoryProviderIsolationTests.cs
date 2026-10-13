using FluentAssertions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Sessions;

/// <summary>
/// Conversation history must not travel between users (BUG-055).
///
/// <para>
/// The default is isolated <i>by construction</i>: with <c>ChatHistoryProvider</c> left null, the
/// orchestrator news up an <c>InMemoryChatHistoryProvider</c> <b>inside the per-scope coordinator
/// build</b>, so every circuit, connection and user gets its own. Setting the option replaces that
/// with one instance taken from singleton options — shared by every scope in the process. Taking
/// the documented step therefore <b>inverts</b> the isolation model, silently.
/// </para>
///
/// <para>
/// Found by driving three authenticated principals against <c>MentorAgentNugetReact</c>: a private
/// code written by one was read back verbatim by the other two. The library was doing exactly what
/// it was told — which is the point. The documentation's own examples
/// (<c>new CosmosChatHistoryProvider(...)</c>, <c>new MyRedisChatHistoryProvider(...)</c>) are
/// precisely the shape that shares one object across every user, and nothing said so.
/// </para>
/// </summary>
public sealed class HistoryProviderIsolationTests
{
    /// <summary>
    /// The obvious implementation, and the dangerous one: one list, no notion of a user. This is
    /// what someone writes when the documentation says "implement ChatHistoryProvider".
    /// </summary>
    private sealed class NaiveProvider : ChatHistoryProvider
    {
        private readonly List<ChatMessage> _history = [];
        internal int Instances => 1;

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            lock (_history) return ValueTask.FromResult<IEnumerable<ChatMessage>>([.. _history]);
        }

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            lock (_history)
            {
                _history.AddRange(context.RequestMessages);
                if (context.ResponseMessages is { } r) _history.AddRange(r);
            }
            return ValueTask.CompletedTask;
        }
    }

    private const string Secret = "ZEPHYR-4417";

    /// <summary>Everything the model was sent on the last call that carried tools.</summary>
    private static string SentTo(FakeChatClient chat)
        => string.Join("\n", chat.Calls[^1].Messages.Select(m => m.Text));

    [Fact]
    public async Task With_no_provider_configured_two_scopes_do_not_share_history()
    {
        // The control, and the reason the default is safe: a fresh provider per coordinator build.
        await using var alice = MentorHostFixture.Create();
        await using var bob = MentorHostFixture.Create();

        alice.Chat!.Say("ok");
        bob.Chat!.Say("ok");

        await alice.RunTurnAsync($"il mio codice è {Secret}");
        await bob.RunTurnAsync("qual è il mio codice?");

        SentTo(bob.Chat!).Should().NotContain(Secret,
            "with no provider configured each scope builds its own InMemoryChatHistoryProvider");
    }

    [Fact]
    public async Task A_factory_gives_each_scope_its_own_provider()
    {
        // The fix: a factory is called once per coordinator build, which is the same lifetime the
        // default has. It makes the safe thing the easy thing for an in-memory or per-user store.
        await using var alice = MentorHostFixture.Create(o => o.ChatHistoryProviderFactory = _ => new NaiveProvider());
        await using var bob = MentorHostFixture.Create(o => o.ChatHistoryProviderFactory = _ => new NaiveProvider());

        alice.Chat!.Say("ok");
        bob.Chat!.Say("ok");

        await alice.RunTurnAsync($"il mio codice è {Secret}");
        await bob.RunTurnAsync("qual è il mio codice?");

        SentTo(bob.Chat!).Should().NotContain(Secret,
            "a factory is invoked per scope, so one user's transcript cannot reach another's");
    }

    [Fact]
    public async Task A_shared_instance_still_works_for_a_single_user()
    {
        // The existing option keeps working — a host with one user, or a provider that partitions
        // internally, is entitled to it. Removing it would be a breaking change for no gain.
        var shared = new NaiveProvider();
        await using var host = MentorHostFixture.Create(o => o.ChatHistoryProvider = shared);

        host.Chat!.Say("ok").Say("ok");

        await host.RunTurnAsync($"il mio codice è {Secret}");
        await host.RunTurnAsync("qual è il mio codice?");

        SentTo(host.Chat!).Should().Contain(Secret,
            "the same user's own history is exactly what a history provider is for");
        shared.Instances.Should().Be(1);
    }

    [Fact]
    public void Configuring_both_a_provider_and_a_factory_is_refused_rather_than_silently_picking_one()
    {
        // Two answers to the same question, with opposite isolation semantics. Choosing one for the
        // host would make the safety of their application depend on which line they wrote last.
        var act = () => MentorHostFixture.Create(o =>
        {
            o.ChatHistoryProvider = new NaiveProvider();
            o.ChatHistoryProviderFactory = _ => new NaiveProvider();
        }).Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorOrchestrator>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ChatHistoryProvider*ChatHistoryProviderFactory*");
    }
}
