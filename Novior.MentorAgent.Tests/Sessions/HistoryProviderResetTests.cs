using FluentAssertions;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Sessions;

/// <summary>
/// BUG-102. A new conversation must start with an empty history, whatever history provider the host
/// installs.
///
/// <para>
/// MentorAgent builds one history provider per coordinator (<c>ChatHistoryProviderFactory</c>) and keeps
/// it for the whole circuit or connection; <c>ResetSessionAsync</c> gives the coordinator a new
/// <b>session</b>, not a new provider. A provider that keeps the transcript in a field therefore carries
/// the previous conversation into the one the user has just started. Found on the rc.13
/// pre-publication pass: after the React sample's "new conversation" button the model still quoted the
/// previous question, because the sample's provider — written the way the README's own examples read —
/// kept a <c>List&lt;ChatMessage&gt;</c> in a field.
/// </para>
/// </summary>
public sealed class HistoryProviderResetTests
{
    private const string Secret = "ZEPHYR-4417";

    /// <summary>The transcript in a field: survives a new session.</summary>
    private sealed class FieldProvider : ChatHistoryProvider
    {
        private readonly List<ChatMessage> _history = [];

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IEnumerable<ChatMessage>>([.. _history]);

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            _history.AddRange(context.RequestMessages);
            _history.AddRange(context.ResponseMessages ?? []);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The README's recipe: the transcript in the session's StateBag.</summary>
    private sealed class SessionProvider : ChatHistoryProvider
    {
        private readonly ProviderSessionState<List<ChatMessage>> _transcript =
            new(_ => [], nameof(SessionProvider), AIJsonUtilities.DefaultOptions);

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IEnumerable<ChatMessage>>([.. _transcript.GetOrInitializeState(context.Session)]);

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            var history = _transcript.GetOrInitializeState(context.Session);
            history.AddRange(context.RequestMessages);
            history.AddRange(context.ResponseMessages ?? []);
            _transcript.SaveState(context.Session, history);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<string> SentAfterReset(Func<ChatHistoryProvider> factory)
    {
        await using var host = MentorHostFixture.Create(o => o.ChatHistoryProviderFactory = _ => factory());
        host.Chat!.Say("ok").Say("ok");

        await host.RunTurnAsync($"il mio codice è {Secret}");
        await host.Orchestrator.ResetSessionAsync();
        await host.RunTurnAsync("qual è il mio codice?");

        return string.Join("\n", host.Chat.Calls[^1].Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task The_documented_in_memory_provider_starts_empty_after_a_reset()
        => (await SentAfterReset(() => new InMemoryChatHistoryProvider())).Should().NotContain(Secret);

    [Fact]
    public async Task A_provider_that_keeps_the_transcript_in_the_session_starts_empty_after_a_reset()
        => (await SentAfterReset(() => new SessionProvider())).Should().NotContain(Secret,
            "a new session has an empty StateBag");

    [Fact]
    public async Task A_provider_that_keeps_the_transcript_in_a_field_carries_it_across_a_reset()
        // Pins why the README says so: the provider outlives the session by design.
        => (await SentAfterReset(() => new FieldProvider())).Should().Contain(Secret);

    [Theory]
    // BUG-111: each package README is read on its own nuget.org page, and the server package — the one
    // whose hub offers ResetSession — carried neither the rule nor the recipe.
    [InlineData("core", "ResetSessionAsync")]
    [InlineData("server", "ResetSession")]
    public void The_readme_tells_a_custom_provider_to_keep_its_transcript_in_the_session(string package, string reset)
    {
        var readme = RepoFiles.Readme(package);
        readme.Should().Contain("ProviderSessionState",
            "the factory guidance must show where a custom provider keeps its transcript");
        readme.Should().Contain(reset);
        readme.Should().Contain("not in a field");
    }

    [Theory]
    [InlineData("core")]
    [InlineData("server")]
    public void No_readme_says_a_reset_clears_history(string package)
    {
        // It does not: the provider is kept across a reset, and a provider that holds the transcript in
        // a field carries the old conversation into the new one (BUG-102, BUG-111). A literal match on
        // "clears history" let "Clear conversation history" and "Clears the history." through (rc.14 review).
        RepoFiles.Readme(package).Should().NotMatchRegex(@"(?i)\bclears?\s+(the\s+|conversation\s+)?history\b");
    }
}
